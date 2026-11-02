using System;
using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Controls;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine.Tests
{
    internal static class RopeActionTests
    {
        static int checks;
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
        static async Task Until(Func<bool> predicate)
        {
            for (int i = 0; i < 100 && !predicate(); i++) await Task.Delay(20);
            if (!predicate()) throw new Exception("等待异步操作超时");
        }
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("绳子操作测试：" + checks + " 项通过"); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                app.Shutdown(Environment.ExitCode);
            };
            app.Run();
        }
        static async Task Run()
        {
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "rope-actions-" + Guid.NewGuid().ToString("N"));
            var store = new StateStore(root); Directory.CreateDirectory(root);
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 20, 30, 40, 255, 20, 30, 40, 255, 20, 30, 40, 255, 20, 30, 40, 255 }, 8);
            image.Freeze(); byte[] png = ImageContent.EncodeClipboard(image);
            string firstPath = Path.Combine(root, "first.png"), secondPath = Path.Combine(root, "second.png");
            File.WriteAllBytes(firstPath, png); File.WriteAllBytes(secondPath, png);
            var controller = new DesktopController(store, Path.Combine(root, "watched"), false);
            try
            {
                controller.Gallery.Add(firstPath, ImageContent.Read(firstPath));
                controller.Gallery.Add(secondPath, ImageContent.Read(secondPath));
                var first = controller.Gallery.Items[0]; var second = controller.Gallery.Items[1];
                var realPreparation = await controller.PrepareImageCopy(firstPath);
                Check(realPreparation.Item1.IsFrozen && realPreparation.Item1.PixelWidth == 2 && ImageContent.Decode(realPreparation.Item2).PixelHeight == 2,
                    "后台实际解码生成可跨线程使用的完整图片和 PNG");
                int width = 3000, height = 2000;
                var largePixels = new byte[width * height * 4]; new Random(31).NextBytes(largePixels);
                for (int i = 3; i < largePixels.Length; i += 4) largePixels[i] = 255;
                var largeImage = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, largePixels, width * 4);
                string largePath = Path.Combine(root, "large.png"); File.WriteAllBytes(largePath, ImageContent.EncodeClipboard(largeImage));
                controller.Gallery.Add(largePath, ImageContent.Read(largePath));
                var largeItem = controller.Gallery.Items[2]; int realWrites = 0; IDataObject realPayload = null;
                controller.WriteClipboard = payload => { realWrites++; realPayload = (IDataObject)payload; return Task.FromResult(true); };
                var actualWatch = Stopwatch.StartNew(); controller.Act(ShotAction.Copy, largeItem); actualWatch.Stop();
                Check(actualWatch.ElapsedMilliseconds < 150 && realWrites == 0, "实际大图复制立即让出界面线程");
                await Until(() => realWrites == 1);
                Check(realPayload.GetDataPresent("PNG") && ((BitmapSource)realPayload.GetData(DataFormats.Bitmap)).PixelWidth == width,
                    "后台完成的大图复制保留完整尺寸及 PNG");
                controller.Gallery.Forget(largeItem);
                var copyFirst = new TaskCompletionSource<Tuple<BitmapSource, byte[]>>();
                var copySecond = new TaskCompletionSource<Tuple<BitmapSource, byte[]>>();
                int clipboardWrites = 0; IDataObject clipboard = null;
                controller.PrepareImageCopy = path => path == firstPath ? copyFirst.Task : copySecond.Task;
                controller.WriteClipboard = payload => { clipboardWrites++; clipboard = (IDataObject)payload; return Task.FromResult(true); };
                var watch = Stopwatch.StartNew(); controller.Act(ShotAction.Copy, first); watch.Stop();
                Check(watch.ElapsedMilliseconds < 150 && clipboardWrites == 0, "复制图片等待准备时不阻塞界面");
                controller.Act(ShotAction.Copy, second);
                copySecond.SetResult(Tuple.Create(image, png)); await Until(() => clipboardWrites == 1);
                copyFirst.SetResult(Tuple.Create(image, png)); await Task.Delay(50);
                Check(clipboardWrites == 1 && clipboard.GetDataPresent("PNG") && clipboard.GetDataPresent(DataFormats.Bitmap) && clipboard.GetDataPresent(DataFormats.FileDrop), "快速连续复制以最后选择的图片为准并保留三种格式");
                var names = (string[])clipboard.GetData(DataFormats.FileDrop);
                Check(names[0] == secondPath, "剪贴板文件指向最后选择的图片");
                int changed = 0, requests = 0; controller.Gallery.Changed += delegate { changed++; };
                var recycle = new TaskCompletionSource<bool>();
                controller.RecycleFile = path => { requests++; return recycle.Task; };
                watch.Restart(); controller.Act(ShotAction.Trash, first); watch.Stop();
                controller.Act(ShotAction.Trash, first);
                var closeFirst = (Button)typeof(ShotCard).GetField("close", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller.Shelf.CardFor(firstPath));
                Check(!closeFirst.IsEnabled, "删除进行时禁用重复删除按钮");
                Check(watch.ElapsedMilliseconds < 150 && requests == 1 && File.Exists(firstPath) && controller.Gallery.Contains(firstPath), "删除等待回收站时界面可继续响应，重复点击不重复执行");
                File.Delete(firstPath); recycle.SetResult(true);
                await Until(() => !controller.Gallery.Collected.Contains(first));
                Check(!controller.Gallery.Contains(firstPath) && changed == 1 && controller.Gallery.Collected.Contains(second), "删除完成后一次更新挂图和图片篮子");
                var saved = new StateStore(root);
                Check(saved.State.Images.Count == 1 && saved.State.Collection.Count == 1 && saved.State.Images[0] == secondPath, "删除结果持久化且保留其他图片");
                controller.RecycleFile = path => Task.FromResult(true); controller.Act(ShotAction.Trash, second);
                var closeSecond = (Button)typeof(ShotCard).GetField("close", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller.Shelf.CardFor(secondPath));
                Check(File.Exists(secondPath) && controller.Gallery.Contains(secondPath) && closeSecond.IsEnabled, "回收站取消或文件仍存在时保留图片记录并恢复按钮");
                var failed = new TaskCompletionSource<bool>(); controller.RecycleFile = path => failed.Task;
                controller.Act(ShotAction.Trash, second); failed.SetException(new IOException("模拟回收站失败"));
                await Task.Delay(100);
                Check(File.Exists(secondPath) && controller.Gallery.Contains(secondPath), "回收站报错时保留图片记录");
            }
            finally { controller.Dispose(); }
        }
    }
}
