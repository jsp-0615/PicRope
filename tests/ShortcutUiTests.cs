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
            bool cancelled = false, completed = false;
            var capture = new ScreenCapture(delegate { completed = true; });
            capture.Finished += delegate { cancelled = true; };
            capture.Show(); await Task.Delay(150);
            var captureWindows = Field<System.Collections.Generic.List<ScreenCapture.CaptureOverlay>>(capture, "windows");
            mouse_event(8, 0, 0, 0, UIntPtr.Zero); mouse_event(16, 0, 0, 0, UIntPtr.Zero); await Task.Delay(150);
            Check(cancelled && !completed, "实际鼠标右键取消截图且不生成图片");
            cancelled = false; completed = false;
            capture = new ScreenCapture(delegate { completed = true; });
            capture.Finished += delegate { cancelled = true; };
            capture.Show(); await Task.Delay(150);
            captureWindows = Field<System.Collections.Generic.List<ScreenCapture.CaptureOverlay>>(capture, "windows");
            var palettes = Field<System.Collections.Generic.List<Popup>>(captureWindows[0], "palettes");
            var surface = (Border)palettes[0].Child;
            surface.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent });
            Check(cancelled && !completed && !palettes[0].IsOpen, "截图工具面板内右键也会取消");
        }
    }
}
