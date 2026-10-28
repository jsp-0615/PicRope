using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Drawing = System.Drawing;

namespace ScreenLine.Tests
{
    internal static class ShortcutUiTests
    {
        static int checks;
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("通过：" + message); }
        static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        [STAThread]
        public static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; LightTheme.Install(app);
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("快捷键设置测试：" + checks + " 项通过"); }
                catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
                app.Shutdown(Environment.ExitCode);
            };
            app.Run();
        }
        static MouseButtonEventArgs Click(RoutedEvent route, int count)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = route };
            typeof(MouseButtonEventArgs).GetProperty("ClickCount").GetSetMethod(true).Invoke(args, new object[] { count });
            return args;
        }
        static async Task Run()
        {
            int savedKey = 0, savedModifiers = 0, calls = 0;
            var window = new CaptureShortcutWindow(112, 0, delegate(int key, int modifiers) { calls++; savedKey = key; savedModifiers = modifiers; return calls == 1 ? "该快捷键已被占用，请选择其他组合。" : null; });
            window.Show(); await Task.Delay(300);
            var preview = Field<TextBlock>(window, "preview"); var keys = Field<ComboBox>(window, "keys"); var ctrl = Field<CheckBox>(window, "control");
            Check(preview.Text == "F1", "设置窗口显示当前快捷键");
            Check(window.ActualWidth >= 450 && window.ActualHeight >= 300 && window.WindowStyle == WindowStyle.None, "使用完整的圆角设置窗口");
            string screenshot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shortcut-settings-preview.png");
            var top = window.PointToScreen(new Point(16, 16)); var bottom = window.PointToScreen(new Point(window.ActualWidth - 16, window.ActualHeight - 16));
            using (var image = new Drawing.Bitmap((int)Math.Ceiling(bottom.X - top.X), (int)Math.Ceiling(bottom.Y - top.Y)))
            {
                using (var graphics = Drawing.Graphics.FromImage(image)) graphics.CopyFromScreen((int)top.X, (int)top.Y, 0, 0, image.Size);
                image.Save(screenshot, Drawing.Imaging.ImageFormat.Png);
            }
            keys.IsDropDownOpen = true; await Task.Delay(120);
            var popup = keys.Template.FindName("PART_Popup", keys) as Popup;
            Check(popup != null && popup.IsOpen, "自定义按键列表可展开");
            keys.SelectedIndex = 24; keys.IsDropDownOpen = false;
            Check(preview.Text == "A", "主按键更改后立即更新预览");
            var apply = typeof(CaptureShortcutWindow).GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic);
            apply.Invoke(window, null);
            Check(calls == 0 && Field<TextBlock>(window, "status").Visibility == Visibility.Visible, "无组合键的字母会提示且不保存");
            ctrl.IsChecked = true; Check(preview.Text == "Ctrl+A", "点击组合键后立即更新预览");
            apply.Invoke(window, null);
            Check(calls == 1 && window.IsVisible && Field<TextBlock>(window, "status").Visibility == Visibility.Visible, "快捷键冲突时保持窗口并显示错误");
            keys.SelectedIndex = 0; Check(Field<TextBlock>(window, "status").Visibility == Visibility.Collapsed, "重新选择按键时清除旧错误");
            apply.Invoke(window, null);
            Check(calls == 2 && savedKey == 112 && savedModifiers == 2 && !window.IsVisible, "保存选中的组合并关闭窗口");
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "shortcut-ui-" + Guid.NewGuid().ToString("N"));
            var gallery = new Gallery(new StateStore(root));
            var shelf = new ShelfWindow(gallery, delegate { }, delegate { });
            var hint = Field<TextBlock>(shelf, "helpDetail");
            shelf.SetCaptureShortcut("F1"); Check(hint.Text.Contains("F1") && !hint.Text.Contains("PrtScn"), "空状态提示使用实际截图快捷键");
            shelf.SetCaptureShortcut("Ctrl+Alt+A"); Check(hint.Text.Contains("Ctrl+Alt+A"), "快捷键更改后空状态同步刷新");
            shelf.SetCaptureShortcut(null); Check(hint.Text.Contains("托盘") && !hint.Text.Contains("F1"), "快捷键不可用时提示从托盘截图");
            shelf.Close();
            BitmapSource desktopImage;
            using (var sourceBitmap = new Drawing.Bitmap(2, 2))
            {
                sourceBitmap.SetPixel(0, 0, Drawing.Color.FromArgb(255, 12, 34, 56));
                sourceBitmap.SetPixel(1, 0, Drawing.Color.FromArgb(255, 200, 70, 10));
                sourceBitmap.SetPixel(0, 1, Drawing.Color.FromArgb(0, 0, 0, 0));
                desktopImage = CaptureRendering.FromDesktopBitmap(sourceBitmap);
            }
            var rgbImage = new FormatConvertedBitmap(desktopImage, PixelFormats.Bgr32, null, 0);
            var pixels = new byte[16]; rgbImage.CopyPixels(pixels, 8, 0);
            Check(desktopImage.IsFrozen && pixels[0] == 56 && pixels[1] == 34 && pixels[2] == 12 && pixels[4] == 10 && pixels[5] == 70 && pixels[6] == 200 && pixels[8] == 0 && pixels[9] == 0 && pixels[10] == 0,
                "直接读取桌面位图保留像素，来源释放后仍可使用");
            bool cancelled = false, completed = false;
            var capture = new ScreenCapture(delegate { completed = true; });
            capture.Finished += delegate { cancelled = true; };
            capture.Show(); await Task.Delay(150);
            var captureWindows = Field<System.Collections.Generic.List<ScreenCapture.CaptureOverlay>>(capture, "windows");
            bool deferred = true;
            foreach (var overlay in captureWindows) deferred &= Field<Border>(overlay, "toolbar") == null;
            Check(deferred, "截图蒙层显示时不会提前创建工具栏");
            mouse_event(8, 0, 0, 0, UIntPtr.Zero); mouse_event(16, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
            Check(cancelled && !completed, "实际鼠标右键取消截图且不生成图片");
            cancelled = false; completed = false;
            capture = new ScreenCapture(delegate { completed = true; });
            capture.Finished += delegate { cancelled = true; };
            capture.Show(); await Task.Delay(150);
            captureWindows = Field<System.Collections.Generic.List<ScreenCapture.CaptureOverlay>>(capture, "windows");
            captureWindows[0].Refresh(new Drawing.Rectangle(10, 10, 100, 100), true, CaptureTool.Select);
            int createdToolbars = 0;
            foreach (var overlay in captureWindows) if (Field<Border>(overlay, "toolbar") != null) createdToolbars++;
            Check(createdToolbars == 1 && Field<System.Collections.Generic.Dictionary<CaptureTool, Button>>(captureWindows[0], "toolButtons").Count == 8, "选区完成后只在目标屏幕创建完整工具栏");
            var palettes = Field<System.Collections.Generic.List<Popup>>(captureWindows[0], "palettes");
            var surface = (Border)palettes[0].Child;
            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            Check(cancelled && !completed && !palettes[0].IsOpen, "截图工具面板内右键也会取消");
            var screens = System.Windows.Forms.Screen.AllScreens;
            var firstScreen = screens[0].Bounds;
            var secondScreen = screens.Length > 1 ? screens[1].Bounds : firstScreen;
            var pointer = new WindowsNative.Point { X = firstScreen.Left + firstScreen.Width / 3, Y = firstScreen.Top + firstScreen.Height / 3 };
            byte[] screenshotPng = null;
            capture = new ScreenCapture(delegate(byte[] png, Point origin, CaptureResult result) { screenshotPng = png; }, delegate { return pointer; });
            capture.Show();
            captureWindows = Field<System.Collections.Generic.List<ScreenCapture.CaptureOverlay>>(capture, "windows");
            var firstOverlay = captureWindows.Find(item => item.Bounds.Contains(pointer.X, pointer.Y));
            capture.Begin(firstOverlay);
            pointer = new WindowsNative.Point { X = secondScreen.Left + secondScreen.Width * 2 / 3, Y = secondScreen.Top + secondScreen.Height * 2 / 3 };
            capture.Move(); capture.End(firstOverlay);
            var selected = capture.Selection;
            Check(selected.Width > 0 && selected.Height > 0, "选区可跨屏完成且无需抢占鼠标");
            var owner = Field<ScreenCapture.CaptureOverlay>(capture, "toolbarOwner");
            Check(owner != null && Field<Border>(owner, "toolbar") != null, "跨屏选区结束后显示完整工具栏");
            capture.SetTool(CaptureTool.Rectangle);
            pointer = new WindowsNative.Point { X = selected.Left + selected.Width / 4, Y = selected.Top + selected.Height / 4 };
            capture.Begin(owner);
            pointer = new WindowsNative.Point { X = selected.Left + selected.Width / 2, Y = selected.Top + selected.Height / 2 };
            capture.Move(); capture.End(owner);
            Check(capture.Annotations.Count == 1, "延后创建工具栏后仍可进行矩形标注");
            capture.Finish(CaptureResult.Complete);
            using (var stream = new MemoryStream(screenshotPng))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check(image.PixelWidth == selected.Width && image.PixelHeight == selected.Height, "跨屏标注结果按选区像素尺寸导出");
            }
            string imagePath = Path.Combine(root, "pinned.png");
            Directory.CreateDirectory(root);
            var imageSource = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 50, 80, 120, 255, 50, 80, 120, 255, 50, 80, 120, 255, 50, 80, 120, 255 }, 8);
            File.WriteAllBytes(imagePath, ImageContent.EncodeClipboard(imageSource));
            var pinned = new PinnedImageWindow(new CaptureItem(imagePath, ImageContent.Read(imagePath)), new Rect(300, 200, 120, 80), 0, imageSource, LineTheme.All[0], 0);
            bool pinClosed = false; pinned.Closed += delegate { pinClosed = true; }; pinned.Show(); await Task.Delay(700);
            pinned.Frame.RaiseEvent(Click(UIElement.MouseLeftButtonDownEvent, 1));
            pinned.Frame.RaiseEvent(Click(UIElement.MouseLeftButtonUpEvent, 1));
            Check(!pinClosed && pinned.IsVisible, "单击桌面贴图仍保留图片窗口");
            pinned.Frame.RaiseEvent(Click(UIElement.MouseLeftButtonDownEvent, 2));
            Check(pinClosed && File.Exists(imagePath), "双击关闭桌面贴图并保留图片文件");
        }
    }
}
