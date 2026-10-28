using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Drawing = System.Drawing;

namespace ScreenLine.Tests
{
    internal static class CaptureTests
    {
        static int checks;
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
        [STAThread]
        public static void Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("截图测试：" + checks + " 项通过"); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                app.Shutdown(Environment.ExitCode);
            };
            app.Run();
        }
        static async Task Run()
        {
            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "state.json"), "{\"Images\":[\"old.png\"]}");
            var store = new StateStore(root);
            Check(store.State.CaptureHotkeyKey == 112 && store.State.CaptureHotkeyModifiers == 0, "旧设置默认使用 F1");
            store.State.CaptureHotkeyKey = 65; store.State.CaptureHotkeyModifiers = 3; store.Save();
            var restored = new StateStore(root);
            Check(restored.State.CaptureHotkeyKey == 65 && restored.State.CaptureHotkeyModifiers == 3 && restored.State.Images[0] == "old.png", "快捷键保存后恢复并保留已有数据");
            Check(CaptureShortcutWindow.Valid(112, 0) && CaptureShortcutWindow.Valid(65, 3) && !CaptureShortcutWindow.Valid(65, 0) && !CaptureShortcutWindow.Valid(0, 0), "快捷键检查接受 F1 和组合键，拒绝无效键");
            Check(CaptureShortcutWindow.Format(65, 3) == "Ctrl+Alt+A", "快捷键名称正确显示");
            var desktop = new Drawing.Rectangle(-1920, -100, 3840, 1180);
            var rect = ScreenCapture.Bounds(new WindowsNative.Point { X = 400, Y = 200 }, new WindowsNative.Point { X = -50, Y = -30 }, desktop);
            Check(rect == new Drawing.Rectangle(-50, -30, 450, 230), "反向拖动可跨越负坐标屏幕");
            rect = ScreenCapture.Bounds(new WindowsNative.Point { X = -2000, Y = -200 }, new WindowsNative.Point { X = 2000, Y = 2000 }, desktop);
            Check(rect == desktop, "选择范围被限制在虚拟桌面内");
            using (var events = new WindowsEvents())
            using (var other = new WindowsEvents())
            {
                Check(events.SetCaptureShortcut(135, 7), "注册截图组合键");
                Check(other.SetCaptureShortcut(134, 7), "注册第二个组合键用于冲突测试");
                Check(!events.SetCaptureShortcut(134, 7), "快捷键冲突时拒绝替换");
                int toggle = 0, capture = 0;
                events.ToggleRequested += delegate { toggle++; }; events.CaptureRequested += delegate { capture++; };
                var source = (HwndSource)typeof(WindowsEvents).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(events);
                SendMessage(source.Handle, 0x0312, new IntPtr(2), IntPtr.Zero);
                SendMessage(source.Handle, 0x0312, new IntPtr(1), IntPtr.Zero);
                Check(capture == 1 && toggle == 1, "冲突后原快捷键仍有效，截图和显示快捷键分开处理");
            }
            using (var bitmap = new Drawing.Bitmap(2, 2))
            {
                bitmap.SetPixel(0, 0, Drawing.Color.FromArgb(255, 12, 34, 56));
                bitmap.SetPixel(1, 0, Drawing.Color.FromArgb(255, 200, 70, 10));
                bitmap.SetPixel(0, 1, Drawing.Color.FromArgb(0, 0, 0, 0));
                var converted = CaptureRendering.FromDesktopBitmap(bitmap);
                var rgb = new FormatConvertedBitmap(converted, PixelFormats.Bgr32, null, 0);
                var pixels = new byte[16]; rgb.CopyPixels(pixels, 8, 0);
                Check(converted.IsFrozen && pixels[0] == 56 && pixels[1] == 34 && pixels[2] == 12 && pixels[4] == 10 && pixels[5] == 70 && pixels[6] == 200 && pixels[8] == 0 && pixels[9] == 0 && pixels[10] == 0,
                    "直接转换桌面位图保留像素及透明黑色并可在释放内存锁后使用");
            }
            var savedCursor = WindowsNative.Cursor();
            var savedFocus = WindowsNative.GetForegroundWindow();
            ScreenCapture screenCapture = null;
            try
            {
                byte[] actualPng = null;
                var monitor = WindowsNative.ScreenAtPointer().Bounds;
                screenCapture = new ScreenCapture(delegate(byte[] bytes, Point origin, CaptureResult result) { actualPng = bytes; });
                screenCapture.Show();
                await Task.Delay(200);
                var overlays = (System.Collections.IEnumerable)typeof(ScreenCapture).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture);
                bool positioned = true; int count = 0;
                foreach (Window overlay in overlays)
                {
                    var expected = (Drawing.Rectangle)overlay.GetType().GetField("Bounds", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                    WindowsNative.Rectangle actual;
                    positioned &= WindowsNative.GetWindowRect(new WindowInteropHelper(overlay).Handle, out actual)
                        && actual.Left == expected.Left && actual.Top == expected.Top && actual.Right == expected.Right && actual.Bottom == expected.Bottom;
                    count++;
                }
                Check(positioned && count == System.Windows.Forms.Screen.AllScreens.Length, "截图蒙层覆盖每块屏幕的实际像素范围");
                bool deferred = true;
                foreach (Window overlay in overlays) deferred &= overlay.GetType().GetField("toolbar", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay) == null;
                Check(deferred, "选择区域前不创建编辑工具栏");
                SetCursorPos(monitor.Left + 20, monitor.Top + 20); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                SetCursorPos(monitor.Left + 70, monitor.Top + 60); await Task.Delay(80);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(200);
                Check(actualPng == null && screenCapture.Selection.Width == 50 && screenCapture.Selection.Height == 40, "拖动后保留选区等待编辑（" + screenCapture.Selection.ToString() + "）");
                var owner = (Window)typeof(ScreenCapture).GetField("toolbarOwner", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture);
                var buttons = (System.Collections.Generic.Dictionary<CaptureTool, Button>)owner.GetType().GetField("toolButtons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
                int createdToolbars = 0;
                foreach (Window overlay in overlays) if (overlay.GetType().GetField("toolbar", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay) != null) createdToolbars++;
                Check(createdToolbars == 1, "完成选区后只在当前屏幕创建工具栏");
                var palettes = (System.Collections.Generic.List<Popup>)owner.GetType().GetField("palettes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
                await ClickControl(buttons[CaptureTool.Emoji]);
                Check(palettes[0].IsOpen, "表情图标打开表情面板");
                var facePanel = (Panel)((Border)palettes[0].Child).Child;
                await ClickControl((Button)facePanel.Children[1]);
                Check(!palettes[0].IsOpen && (string)typeof(ScreenCapture).GetField("emoji", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture) == "😂", "选择表情后更新标注并关闭面板");
                await ClickControl((Button)palettes[1].PlacementTarget);
                Check(palettes[1].IsOpen, "颜色图标打开色板");
                var colors = (Panel)((Border)palettes[1].Child).Child;
                await ClickControl((Button)colors.Children[4]);
                Check(!palettes[1].IsOpen && (Color)typeof(ScreenCapture).GetField("color", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture) == Colors.DodgerBlue, "色板选择更新标注颜色");
                await ClickControl((Button)palettes[2].PlacementTarget);
                Check(palettes[2].IsOpen, "粗细图标打开线条选项");
                var widths = (Panel)((Border)palettes[2].Child).Child;
                await ClickControl((Button)widths.Children[2]);
                Check(!palettes[2].IsOpen && (double)typeof(ScreenCapture).GetField("lineWidth", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture) == 6, "线条粗细选择生效");
                var rectangleButton = buttons[CaptureTool.Rectangle];
                var buttonPoint = rectangleButton.PointToScreen(new Point(rectangleButton.ActualWidth / 2, rectangleButton.ActualHeight / 2));
                SetCursorPos((int)buttonPoint.X, (int)buttonPoint.Y); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check((CaptureTool)typeof(ScreenCapture).GetField("tool", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture) == CaptureTool.Rectangle, "实际点击工具栏选择矩形工具");
                SetCursorPos(monitor.Left + 25, monitor.Top + 25); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                SetCursorPos(monitor.Left + 60, monitor.Top + 50); await Task.Delay(80);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
                Check(screenCapture.Annotations.Count == 1, "鼠标拖动创建矩形标注");
                screenCapture.Undo(); Check(screenCapture.Annotations.Count == 0, "撤销移除标注");
                screenCapture.SetTool(CaptureTool.Select);
                SetCursorPos(monitor.Left + 70, monitor.Top + 60); await Task.Delay(100);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                SetCursorPos(monitor.Left + 90, monitor.Top + 70); await Task.Delay(80);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
                Check(screenCapture.Selection.Width == 70 && screenCapture.Selection.Height == 50, "实际拖动控制点调整选区");
                screenCapture.SetTool(CaptureTool.Text);
                SetCursorPos(monitor.Left + 30, monitor.Top + 30); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                var textOverlay = (Window)typeof(ScreenCapture).GetField("toolbarOwner", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture);
                var input = (TextBox)textOverlay.GetType().GetField("textInput", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(textOverlay);
                Check(input != null, "文字工具可在选区内创建输入框");
                input.Text = "测试";
                screenCapture.Finish(CaptureResult.Complete);
                Check(actualPng != null, "完成后导出截图");
                using (var data = new MemoryStream(actualPng))
                {
                    var frame = BitmapFrame.Create(data, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    Check(frame.PixelWidth == 70 && frame.PixelHeight == 50, "实际截图按拖动区域输出对应像素尺寸（实际 " + frame.PixelWidth + "×" + frame.PixelHeight + "）");
                }
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (screens.Length >= 2)
                {
                    actualPng = null;
                    var first = screens[0].Bounds; var second = screens[1].Bounds;
                    int x1 = first.Left + first.Width / 2, y1 = first.Top + first.Height / 2;
                    int x2 = second.Left + second.Width / 2, y2 = second.Top + second.Height / 2;
                    screenCapture = new ScreenCapture(delegate(byte[] bytes, Point origin, CaptureResult result) { actualPng = bytes; });
                    screenCapture.Show(); await Task.Delay(200);
                    SetCursorPos(x1, y1); await Task.Delay(80);
                    mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                    SetCursorPos(x2, y2); await Task.Delay(80);
                    mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(300);
                    Check(screenCapture.Selection.Width == Math.Abs(x2 - x1) && screenCapture.Selection.Height == Math.Abs(y2 - y1), "实际鼠标可跨屏拖动选择区域");
                    screenCapture.Finish(CaptureResult.Complete);
                    Check(actualPng != null, "跨屏选区完成后导出截图");
                    using (var data = new MemoryStream(actualPng))
                    {
                        var frame = BitmapFrame.Create(data, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        Check(frame.PixelWidth == Math.Abs(x2 - x1) && frame.PixelHeight == Math.Abs(y2 - y1), "跨屏截图的像素尺寸正确");
                    }
                }
                bool cancelled = false, completed = false;
                screenCapture = new ScreenCapture(delegate(byte[] bytes, Point origin, CaptureResult result) { completed = true; });
                screenCapture.Finished += delegate { cancelled = true; };
                screenCapture.Show(); await Task.Delay(200);
                SetCursorPos(monitor.Left + 100, monitor.Top + 100); await Task.Delay(80);
                mouse_event(8, 0, 0, 0, UIntPtr.Zero); mouse_event(16, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
                Check(cancelled && !completed, "右键可在选择区域前取消截图，不产生结果");
                cancelled = false; completed = false;
                screenCapture = new ScreenCapture(delegate(byte[] bytes, Point origin, CaptureResult result) { completed = true; });
                screenCapture.Finished += delegate { cancelled = true; };
                screenCapture.Show(); await Task.Delay(200);
                SetCursorPos(monitor.Left + 100, monitor.Top + 100); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                SetCursorPos(monitor.Left + 300, monitor.Top + 240); await Task.Delay(80);
                mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
                var rightClickOwner = (Window)typeof(ScreenCapture).GetField("toolbarOwner", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(screenCapture);
                var rightClickButtons = (System.Collections.Generic.Dictionary<CaptureTool, Button>)rightClickOwner.GetType().GetField("toolButtons", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rightClickOwner);
                var rightClickPalettes = (System.Collections.Generic.List<Popup>)rightClickOwner.GetType().GetField("palettes", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(rightClickOwner);
                await ClickControl(rightClickButtons[CaptureTool.Emoji]);
                var emojiOptions = (Panel)((Border)rightClickPalettes[0].Child).Child;
                var rightTarget = (Button)emojiOptions.Children[0];
                var rightPoint = rightTarget.PointToScreen(new Point(rightTarget.ActualWidth / 2, rightTarget.ActualHeight / 2));
                SetCursorPos((int)rightPoint.X, (int)rightPoint.Y); await Task.Delay(80);
                mouse_event(8, 0, 0, 0, UIntPtr.Zero); mouse_event(16, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
                Check(cancelled && !completed && !rightClickPalettes[0].IsOpen, "右键可从打开的工具面板取消截图并关闭面板");
            }
            finally { mouse_event(4, 0, 0, 0, UIntPtr.Zero); if (screenCapture != null) screenCapture.Dispose(); SetCursorPos(savedCursor.X, savedCursor.Y); WindowsNative.SetForegroundWindow(savedFocus); }
            var resized = ScreenCapture.Resize(new Drawing.Rectangle(-100, 0, 100, 100), new WindowsNative.Point { X = 0, Y = 100 }, new WindowsNative.Point { X = 40, Y = 130 }, desktop, "br");
            Check(resized == new Drawing.Rectangle(-100, 0, 140, 130), "可拖动选区控制点调整大小");
            var basePixels = new byte[64 * 64 * 4];
            for (int i = 0; i < basePixels.Length; i += 4) { basePixels[i] = (byte)(i % 251); basePixels[i + 1] = 100; basePixels[i + 2] = 100; basePixels[i + 3] = 255; }
            var baseImage = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, basePixels, 64 * 4);
            var pixelated = CaptureRendering.Pixelate(baseImage);
            var area = new Drawing.Rectangle(0, 0, 64, 64);
            foreach (var kind in new[] { CaptureTool.Rectangle, CaptureTool.Ellipse, CaptureTool.Arrow, CaptureTool.Pen, CaptureTool.Mosaic, CaptureTool.Text, CaptureTool.Emoji })
            {
                var annotation = new CaptureAnnotation { Tool = kind, Color = Colors.Red, Width = 3, Text = kind == CaptureTool.Emoji ? "☺" : "测试" };
                annotation.Points.Add(new Point(8, 8)); annotation.Points.Add(new Point(48, 48));
                var output = CaptureRendering.Compose(baseImage, pixelated, area, area, new[] { annotation });
                var pixels = new byte[basePixels.Length]; output.CopyPixels(pixels, 64 * 4, 0);
                Check(!System.Linq.Enumerable.SequenceEqual(pixels, basePixels), kind + " 标注写入截图像素");
            }
            var gallery = new Gallery(store);
            using (var pipeline = new CapturePipeline(gallery, appDispatcher(), Path.Combine(root, "missing")))
            {
                var bitmap = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 3 * 4], 4 * 4);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                byte[] png; using (var stream = new MemoryStream()) { encoder.Save(stream); png = stream.ToArray(); }
                store.State.AutoHang = false;
                await pipeline.AddScreenCapture(png, new Point(2, 2));
                Check(gallery.Collected.Count == 1 && gallery.Items.Count == 0 && File.Exists(gallery.Collected[0].Path), "关闭自动挂图后截图保存到篮子");
                store.State.AutoHang = true; int arrivals = 0; pipeline.Arrived += delegate { arrivals++; };
                await pipeline.AddScreenCapture(png, new Point(2, 2));
                Check(gallery.Collected.Count == 2 && gallery.Items.Count == 1 && arrivals == 1, "开启自动挂图后截图挂上并发送到达事件");
            }
        }
        static async Task ClickControl(FrameworkElement element)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            SetCursorPos((int)point.X, (int)point.Y); await Task.Delay(80);
            mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
        }
        static System.Windows.Threading.Dispatcher appDispatcher() { return Application.Current.Dispatcher; }
    }
}
