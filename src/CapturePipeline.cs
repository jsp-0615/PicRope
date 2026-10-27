using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ScreenLine
{
    internal sealed class CapturePipeline : IDisposable
    {
        readonly Gallery gallery;
        readonly Dispatcher dispatcher;
        readonly string folder;
        readonly CaptureReceipts receipts = new CaptureReceipts();
        readonly Dictionary<string, int> pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> processing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        readonly DateTime launched = DateTime.UtcNow;
        FileSystemWatcher watcher;
        uint clipboardSequence;
        DateTime clipboardDue = DateTime.MaxValue;
        bool clipboardBusy, disposed;
        Point? clipboardOrigin;
        int ticks;
        public event Action<CaptureItem, Point?> Arrived;
        public event Action<string> Failed;

        public CapturePipeline(Gallery gallery, Dispatcher dispatcher, string folder)
        {
            this.gallery = gallery;
            this.dispatcher = dispatcher;
            this.folder = folder;
            clipboardSequence = WindowsNative.GetClipboardSequenceNumber();
            timer.Tick += Tick;
        }

        public bool CollectClipboard
        {
            get { return gallery.Store.State.CollectClipboard; }
            set
            {
                gallery.Store.State.CollectClipboard = value;
                gallery.Store.Save();
                clipboardSequence = WindowsNative.GetClipboardSequenceNumber();
                clipboardDue = DateTime.MaxValue;
            }
        }

        public bool AutoHang
        {
            get { return gallery.Store.State.AutoHang; }
            set
            {
                gallery.Store.State.AutoHang = value;
                gallery.Store.Save();
                gallery.Notify();
            }
        }

        public void IgnoreCurrentClipboard()
        {
            clipboardSequence = WindowsNative.GetClipboardSequenceNumber();
            clipboardDue = DateTime.MaxValue;
        }

        public async Task Start()
        {
            AttachWatcher();
            timer.Start();
            var saved = gallery.Store.State.Images.ToArray();
            var collected = saved.Concat(gallery.Store.State.Collection);
            if (Directory.Exists(gallery.Store.Inbox))
                collected = collected.Concat(Directory.EnumerateFiles(gallery.Store.Inbox).Where(FileRules.IsImage));
            // 恢复图片篮子时不触发自动挂图，也不重新计入截图去重记录。
            foreach (string path in collected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            {
                if (disposed) return;
                if (!File.Exists(path)) continue;
                try
                {
                    ImageContent image = await Task.Run(() => ImageContent.Read(path));
                    if (disposed) return;
                    if (saved.Contains(path, StringComparer.OrdinalIgnoreCase)) gallery.Add(path, image);
                    else gallery.Collect(path, image);
                }
                catch (IOException) { }
                catch (NotSupportedException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        void AttachWatcher()
        {
            if (watcher != null || !Directory.Exists(folder)) return;
            watcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            watcher.Created += delegate(object sender, FileSystemEventArgs args) { Queue(args.FullPath); };
            watcher.Changed += delegate(object sender, FileSystemEventArgs args) { Queue(args.FullPath); };
            watcher.Renamed += delegate(object sender, RenamedEventArgs args) { Queue(args.FullPath); };
            watcher.EnableRaisingEvents = true;
            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (File.GetCreationTimeUtc(path) < launched) seen.Add(path);
                else Queue(path);
            }
        }

        void Queue(string path)
        {
            if (!FileRules.IsImage(path) || disposed) return;
            dispatcher.BeginInvoke(new Action(delegate { if (!disposed && !seen.Contains(path)) pending[path] = 0; }));
        }

        public void ClipboardChanged()
        {
            if (CollectClipboard && WindowsNative.GetClipboardSequenceNumber() != clipboardSequence)
            {
                var cursor = WindowsNative.Cursor();
                clipboardOrigin = new Point(cursor.X, cursor.Y);
                clipboardDue = DateTime.UtcNow.AddMilliseconds(160);
            }
        }

        void Tick(object sender, EventArgs args)
        {
            AttachWatcher();
            foreach (var candidate in pending.ToArray())
                if (!processing.Contains(candidate.Key)) ProcessFile(candidate.Key);
            if (!clipboardBusy && DateTime.UtcNow >= clipboardDue)
            {
                clipboardDue = DateTime.MaxValue;
                ReadClipboard();
            }
            if (++ticks % 10 == 0)
            {
                gallery.Prune();
                foreach (var item in gallery.Collected.ToArray())
                    if (!processing.Contains(item.Path) && File.GetLastWriteTimeUtc(item.Path) != item.Written) Refresh(item);
            }
        }

        async void ProcessFile(string path)
        {
            processing.Add(path);
            bool complete = await Import(path, CaptureSource.Folder, true);
            processing.Remove(path);
            if (disposed) return;
            if (complete) { seen.Add(path); pending.Remove(path); }
            else if (pending.ContainsKey(path) && ++pending[path] >= 20) pending.Remove(path);
        }

        public async Task<bool> Import(string path, CaptureSource? source, bool announce)
        {
            if (source.HasValue ? gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)) : gallery.Contains(path)) return true;
            try
            {
                ImageContent image = await Task.Run(() => ImageContent.Read(path));
                if (disposed) return false;
                if (source.HasValue && !receipts.Accept(image.Fingerprint, source.Value, DateTime.UtcNow)) return true;
                if (source.HasValue && !AutoHang) gallery.Collect(path, image);
                else
                {
                    gallery.Add(path, image);
                    if (announce && Arrived != null)
                    {
                        var cursor = WindowsNative.Cursor();
                        Arrived(gallery.Items[gallery.Items.Count - 1], source.HasValue ? new Point?(new Point(cursor.X, cursor.Y)) : null);
                    }
                }
                return true;
            }
            catch (IOException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public async Task<CaptureItem> AddScreenCapture(byte[] png, Point origin)
        {
            ImageContent content = await Task.Run(() => ImageContent.Decode(png));
            if (disposed) return null;
            Directory.CreateDirectory(gallery.Store.Inbox);
            string path = Path.Combine(gallery.Store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            await Task.Run(() => File.WriteAllBytes(path, png));
            if (disposed) return null;
            CaptureItem item;
            if (!AutoHang) item = gallery.Collect(path, content);
            else
            {
                gallery.Add(path, content);
                item = gallery.Items[gallery.Items.Count - 1];
                if (Arrived != null) Arrived(item, origin);
            }
            return item;
        }

        public void Hang(CaptureItem item)
        {
            if (disposed || gallery.Contains(item.Path)) return;
            if (!File.Exists(item.Path)) { gallery.Prune(); return; }
            gallery.Add(item.Path, item.Image);
            if (Arrived != null) Arrived(item, null);
        }

        async void Refresh(CaptureItem item)
        {
            processing.Add(item.Path);
            try
            {
                ImageContent image = await Task.Run(() => ImageContent.Read(item.Path));
                if (!disposed && gallery.Collected.Contains(item))
                {
                    item.Image = image;
                    item.Written = File.GetLastWriteTimeUtc(item.Path);
                    gallery.Notify();
                }
            }
            catch (IOException) { }
            catch (NotSupportedException) { }
            finally { processing.Remove(item.Path); }
        }

        async void ReadClipboard()
        {
            clipboardBusy = true;
            uint sequence = WindowsNative.GetClipboardSequenceNumber();
            Point? origin = clipboardOrigin;
            try
            {
                if (!CollectClipboard || sequence == clipboardSequence) return;
                IDataObject data = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { data = Clipboard.GetDataObject(); break; }
                    catch (ExternalException) { }
                    await Task.Delay(50);
                }
                if (data == null || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                clipboardSequence = sequence;
                if (!ClipboardRules.IsPlainImage(data.GetFormats())) return;
                byte[] png = null;
                var stream = data.GetData("PNG") as Stream ?? data.GetData("image/png") as Stream;
                if (stream != null)
                {
                    using (var memory = new MemoryStream()) { stream.Position = 0; stream.CopyTo(memory); png = memory.ToArray(); }
                }
                else
                {
                    BitmapSource image = Clipboard.GetImage();
                    if (image == null) return;
                    image.Freeze();
                    png = await Task.Run(() => ImageContent.EncodeClipboard(image));
                }
                if (png.Length > 64L * 1024 * 1024) return;
                ImageContent content = await Task.Run(() => ImageContent.Decode(png));
                if (disposed || !CollectClipboard || WindowsNative.GetClipboardSequenceNumber() != sequence) return;
                if (!receipts.Accept(content.Fingerprint, CaptureSource.Clipboard, DateTime.UtcNow)) return;
                Directory.CreateDirectory(gallery.Store.Inbox);
                string path = Path.Combine(gallery.Store.Inbox, "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                await Task.Run(() => File.WriteAllBytes(path, png));
                if (disposed) return;
                if (!AutoHang) gallery.Collect(path, content);
                else
                {
                    gallery.Add(path, content);
                    if (Arrived != null) Arrived(gallery.Items[gallery.Items.Count - 1], origin);
                }
            }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                if (Failed != null) Failed("这张截图暂时无法读取，请重试。");
            }
            finally { clipboardBusy = false; }
        }

        public void Dispose()
        {
            disposed = true;
            timer.Stop();
            if (watcher != null) watcher.Dispose();
        }
    }
}
