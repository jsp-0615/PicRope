using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal enum CaptureResult { Complete, Pin }
    internal sealed class ScreenCapture : IDisposable
    {
        readonly Drawing.Rectangle desktop = Forms.SystemInformation.VirtualScreen;
        readonly List<CaptureOverlay> windows = new List<CaptureOverlay>();
        readonly List<CaptureAnnotation> annotations = new List<CaptureAnnotation>();
        readonly Action<byte[], Point, CaptureResult> completed;
        readonly Func<WindowsNative.Point> cursor;
        public event Action Finished;
        BitmapSource snapshot, mosaic;
        Drawing.Rectangle selection, original;
        WindowsNative.Point start;
        CaptureAnnotation active;
        CaptureTool tool = CaptureTool.Select;
        CaptureOverlay toolbarOwner;
        Color color = Colors.Red;
        double lineWidth = 3;
        string emoji = "😀", dragMode;
        bool dragging, closed;
        internal Drawing.Rectangle Selection { get { return selection; } }
        internal IList<CaptureAnnotation> Annotations { get { return annotations.AsReadOnly(); } }

        public ScreenCapture(Action<byte[], Point, CaptureResult> completed) : this(completed, WindowsNative.Cursor) { }
        internal ScreenCapture(Action<byte[], Point, CaptureResult> completed, Func<WindowsNative.Point> cursor)
        {
            this.completed = completed;
            this.cursor = cursor;
            using (var bitmap = new Drawing.Bitmap(desktop.Width, desktop.Height))
            {
                using (var graphics = Drawing.Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(desktop.Left, desktop.Top, 0, 0, desktop.Size, Drawing.CopyPixelOperation.SourceCopy);
                snapshot = CaptureRendering.FromDesktopBitmap(bitmap);
            }
        }
        public void Show()
        {
            try
            {
                foreach (var screen in Forms.Screen.AllScreens)
                {
                    var bounds = screen.Bounds;
                    var part = new CroppedBitmap(snapshot, new Int32Rect(bounds.Left - desktop.Left, bounds.Top - desktop.Top, bounds.Width, bounds.Height));
                    var window = new CaptureOverlay(this, bounds, part);
                    windows.Add(window); window.Show();
                }
                var activeWindow = WindowAt(cursor());
                if (activeWindow != null) { activeWindow.Activate(); activeWindow.Focus(); }
            }
            catch { Dispose(); throw; }
        }
        CaptureOverlay WindowAt(WindowsNative.Point point)
        {
            return windows.Find(w => w.Bounds.Contains(point.X, point.Y)) ?? (windows.Count > 0 ? windows[0] : null);
        }
        internal void Begin(CaptureOverlay overlay)
        {
            CommitText(); start = cursor(); original = selection; active = null;
            if (selection.IsEmpty) dragMode = "new";
            else if (tool == CaptureTool.Select) dragMode = HitSelection(start, 10 * WindowsNative.MonitorScale(Forms.Screen.FromPoint(new Drawing.Point(start.X, start.Y))));
            else if (!selection.Contains(start.X, start.Y)) return;
            else if (tool == CaptureTool.Text) { overlay.StartText(start); return; }
            else if (tool == CaptureTool.Emoji)
            {
                annotations.Add(new CaptureAnnotation { Tool = tool, Color = color, Width = Math.Max(lineWidth, 4), Text = emoji, Points = { ToPoint(start) } }); Refresh(); return;
            }
            else
            {
                if (tool == CaptureTool.Mosaic && mosaic == null) mosaic = CaptureRendering.Pixelate(snapshot);
                active = new CaptureAnnotation { Tool = tool, Color = color, Width = lineWidth, Points = { ToPoint(start) } };
                annotations.Add(active); dragMode = "draw";
            }
            if (string.IsNullOrEmpty(dragMode)) return;
            dragging = true; toolbarOwner = null; overlay.CapturePointer(); Refresh();
        }
        internal void Move()
        {
            if (!dragging) return;
            var point = cursor();
            if (dragMode == "new") selection = Bounds(start, point, desktop);
            else if (dragMode == "move")
            {
                int x = Math.Max(desktop.Left, Math.Min(desktop.Right - original.Width, original.Left + point.X - start.X));
                int y = Math.Max(desktop.Top, Math.Min(desktop.Bottom - original.Height, original.Top + point.Y - start.Y));
                var next = new Drawing.Rectangle(x, y, original.Width, original.Height);
                int dx = next.Left - selection.Left, dy = next.Top - selection.Top;
                foreach (var annotation in annotations) for (int i = 0; i < annotation.Points.Count; i++) annotation.Points[i] = annotation.Points[i] + new Vector(dx, dy);
                selection = next;
            }
            else if (dragMode == "draw")
            {
                var p = Clamp(point);
                if (active.Tool == CaptureTool.Pen || active.Tool == CaptureTool.Mosaic) active.Points.Add(p);
                else if (active.Points.Count == 1) active.Points.Add(p); else active.Points[1] = p;
            }
            else selection = Resize(original, start, point, desktop, dragMode);
            Refresh();
        }
        internal void End(CaptureOverlay overlay)
        {
            if (!dragging) return;
            Move(); dragging = false; overlay.ReleasePointer();
            if (selection.Width < 2 || selection.Height < 2) { selection = Drawing.Rectangle.Empty; annotations.Clear(); }
            if (active != null && active.Tool != CaptureTool.Pen && active.Tool != CaptureTool.Mosaic && (active.Points.Count < 2 || (active.Points[1] - active.Points[0]).Length < 2)) annotations.Remove(active);
            active = null; toolbarOwner = selection.IsEmpty ? null : WindowAt(cursor()); Refresh();
        }
        internal void SetTool(CaptureTool value) { CommitText(); tool = value; Refresh(); }
        internal void SetColor(Color value) { color = value; }
        internal void SetWidth(double value) { lineWidth = value; }
        internal void SetEmoji(string value) { if (!string.IsNullOrEmpty(value)) { emoji = value; SetTool(CaptureTool.Emoji); } }
        internal void Undo() { CommitText(); if (annotations.Count > 0) annotations.RemoveAt(annotations.Count - 1); Refresh(); }
        internal void AddText(WindowsNative.Point position, string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) annotations.Add(new CaptureAnnotation { Tool = CaptureTool.Text, Color = color, Width = lineWidth, Text = text, Points = { ToPoint(position) } });
            Refresh();
        }
        void CommitText() { foreach (var window in windows) window.CommitText(); }
        internal void Refresh() { foreach (var window in windows) window.Refresh(selection, window == toolbarOwner && !dragging, tool); }
        Point Clamp(WindowsNative.Point p) { return new Point(Math.Max(selection.Left, Math.Min(selection.Right, p.X)), Math.Max(selection.Top, Math.Min(selection.Bottom, p.Y))); }
        static Point ToPoint(WindowsNative.Point p) { return new Point(p.X, p.Y); }
        string HitSelection(WindowsNative.Point point, double tolerance)
        {
            if (point.X < selection.Left - tolerance || point.X > selection.Right + tolerance || point.Y < selection.Top - tolerance || point.Y > selection.Bottom + tolerance) return "";
            string vertical = Math.Abs(point.Y - selection.Top) <= tolerance ? "t" : Math.Abs(point.Y - selection.Bottom) <= tolerance ? "b" : "";
            string horizontal = Math.Abs(point.X - selection.Left) <= tolerance ? "l" : Math.Abs(point.X - selection.Right) <= tolerance ? "r" : "";
            return vertical + horizontal == "" ? "move" : vertical + horizontal;
        }
        internal static Drawing.Rectangle Resize(Drawing.Rectangle original, WindowsNative.Point start, WindowsNative.Point current, Drawing.Rectangle desktop, string edges)
        {
            int left = original.Left, top = original.Top, right = original.Right, bottom = original.Bottom;
            if (edges.Contains("l")) left = Math.Max(desktop.Left, Math.Min(right - 2, original.Left + current.X - start.X));
            if (edges.Contains("r")) right = Math.Min(desktop.Right, Math.Max(left + 2, original.Right + current.X - start.X));
            if (edges.Contains("t")) top = Math.Max(desktop.Top, Math.Min(bottom - 2, original.Top + current.Y - start.Y));
            if (edges.Contains("b")) bottom = Math.Min(desktop.Bottom, Math.Max(top + 2, original.Bottom + current.Y - start.Y));
            return Drawing.Rectangle.FromLTRB(left, top, right, bottom);
        }
        internal static Drawing.Rectangle Bounds(WindowsNative.Point a, WindowsNative.Point b, Drawing.Rectangle desktop)
        {
            int left = Math.Max(desktop.Left, Math.Min(a.X, b.X)), top = Math.Max(desktop.Top, Math.Min(a.Y, b.Y));
            int right = Math.Min(desktop.Right, Math.Max(a.X, b.X)), bottom = Math.Min(desktop.Bottom, Math.Max(a.Y, b.Y));
            return Drawing.Rectangle.FromLTRB(left, top, Math.Max(left, right), Math.Max(top, bottom));
        }
        internal byte[] Export()
        {
            CommitText();
            var image = CaptureRendering.Compose(snapshot, mosaic, desktop, selection, annotations);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
        }
        internal void Finish(CaptureResult result)
        {
            if (selection.IsEmpty) return;
            byte[] png = Export(); var origin = new Point(selection.Left + selection.Width / 2.0, selection.Top + selection.Height / 2.0);
            Dispose(); completed(png, origin, result);
        }
        internal void Save()
        {
            if (selection.IsEmpty) return;
            CommitText();
            foreach (var window in windows) window.Topmost = false;
            try
            {
                var dialog = new SaveFileDialog { Title = "保存截图", FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", Filter = "PNG 图片|*.png", DefaultExt = ".png", AddExtension = true };
                if (dialog.ShowDialog(toolbarOwner) == true) File.WriteAllBytes(dialog.FileName, Export());
            }
            catch (Exception error) { MessageBox.Show("无法保存截图：" + error.Message, "图绳", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { foreach (var window in windows) window.Topmost = true; if (toolbarOwner != null) toolbarOwner.Activate(); }
        }
        public void Dispose()
        {
            if (closed) return; closed = true;
            foreach (var window in windows) window.Close(); windows.Clear(); snapshot = null; mosaic = null;
            if (Finished != null) Finished();
        }
        internal sealed class CaptureOverlay : Window
        {
            readonly ScreenCapture capture;
            readonly Canvas canvas;
            readonly PreviewLayer preview;
            Border toolbar;
            static readonly Lazy<Style> buttonStyle = new Lazy<Style>(delegate { return (Style)XamlReader.Parse(@"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
  <Setter Property='Foreground' Value='#35404B'/><Setter Property='Background' Value='Transparent'/><Setter Property='BorderThickness' Value='0'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='surface' Background='{TemplateBinding Background}' CornerRadius='5'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='surface' Property='Background' Value='#EAF2FA'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='surface' Property='Background' Value='#D7E8F7'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#79A3C7'/><Setter TargetName='surface' Property='BorderThickness' Value='1'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
</Style>"); });
            readonly Dictionary<CaptureTool, Button> toolButtons = new Dictionary<CaptureTool, Button>();
            readonly List<Popup> palettes = new List<Popup>();
            Ellipse colorIndicator;
            System.Windows.Shapes.Line widthIndicator;
            TextBox textInput;
            WindowsNative.Point textPosition;
            internal readonly Drawing.Rectangle Bounds;
            double ScaleX { get { return canvas.ActualWidth / Bounds.Width; } }
            double ScaleY { get { return canvas.ActualHeight / Bounds.Height; } }
            public CaptureOverlay(ScreenCapture capture, Drawing.Rectangle bounds, BitmapSource image)
            {
                this.capture = capture; Bounds = bounds;
                WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
                ShowInTaskbar = false; Topmost = true; Cursor = Cursors.Cross;
                Left = bounds.Left; Top = bounds.Top; Width = bounds.Width; Height = bounds.Height;
                var grid = new Grid();
                grid.Children.Add(new Image { Source = image, Stretch = Stretch.Fill });
                grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(125, 0, 0, 0)), IsHitTestVisible = false });
                preview = new PreviewLayer(this) { IsHitTestVisible = false }; grid.Children.Add(preview);
                canvas = new Canvas { Background = Brushes.Transparent };
                grid.Children.Add(canvas);
                var hint = new TextBlock { Text = "拖动选择截图区域 · Esc 取消", Foreground = Brushes.White, FontSize = 14, Margin = new Thickness(20), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
                grid.Children.Add(hint); Content = grid;
                SourceInitialized += delegate { WindowsNative.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x10); };
                PreviewKeyDown += delegate(object sender, KeyEventArgs args)
                {
                    if (args.Key == Key.Escape) { args.Handled = true; capture.Dispose(); }
                    else if (args.Key == Key.Enter && textInput == null && !capture.selection.IsEmpty) { args.Handled = true; capture.Finish(CaptureResult.Complete); }
                    else if (args.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && textInput == null) { args.Handled = true; capture.Undo(); }
                };
                PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args) { args.Handled = true; capture.Dispose(); };
                canvas.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
                {
                    if (args.OriginalSource != canvas) return;
                    if (args.ClickCount == 2 && !capture.selection.IsEmpty && capture.tool == CaptureTool.Select) { capture.Finish(CaptureResult.Complete); return; }
                    capture.Begin(this);
                };
                canvas.MouseMove += delegate { capture.Move(); };
                canvas.MouseLeftButtonUp += delegate { capture.End(this); };
                Closed += delegate { foreach (var popup in palettes) popup.IsOpen = false; };
            }
            Border CreateToolbar()
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                AddTool(row, "select", "移动或调整选区", CaptureTool.Select);
                AddTool(row, "rectangle", "矩形", CaptureTool.Rectangle);
                AddTool(row, "ellipse", "椭圆", CaptureTool.Ellipse);
                AddTool(row, "arrow", "箭头", CaptureTool.Arrow);
                AddTool(row, "pen", "画笔", CaptureTool.Pen);
                AddTool(row, "mosaic", "马赛克画笔", CaptureTool.Mosaic);
                AddTool(row, "text", "文字", CaptureTool.Text);

                Popup emojiPopup = null;
                var emojiButton = AddButton(row, "emoji", "表情", delegate { capture.SetTool(CaptureTool.Emoji); emojiPopup.IsOpen = !emojiPopup.IsOpen; });
                toolButtons[CaptureTool.Emoji] = emojiButton;
                var faces = new WrapPanel { Width = 176 };
                foreach (string face in new[] { "😀", "😂", "😍", "👍", "❤️", "⭐", "❗", "✅" })
                {
                    string selectedFace = face;
                    var option = new Button { Style = buttonStyle.Value, Content = face, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 20, Width = 40, Height = 38, Margin = new Thickness(2), ToolTip = "添加表情 " + face, Cursor = Cursors.Hand };
                    System.Windows.Automation.AutomationProperties.SetName(option, "添加表情 " + face);
                    option.Click += delegate { capture.SetEmoji(selectedFace); emojiPopup.IsOpen = false; };
                    faces.Children.Add(option);
                }
                emojiPopup = MakePalette(emojiButton, faces);
                AddDivider(row);

                Popup colorPopup = null;
                var colorButton = AddButton(row, "color", "标注颜色", delegate { colorPopup.IsOpen = !colorPopup.IsOpen; });
                colorIndicator = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(capture.color), Stroke = LightTheme.Brush("#D3DBE3"), StrokeThickness = 1 };
                colorButton.Content = colorIndicator;
                var colors = new StackPanel { Orientation = Orientation.Horizontal };
                var colorNames = new[] { "红色", "橙色", "黄色", "绿色", "蓝色", "黑色", "白色" };
                var colorValues = new[] { Colors.Red, Colors.Orange, Colors.Gold, Colors.LimeGreen, Colors.DodgerBlue, Colors.Black, Colors.White };
                for (int i = 0; i < colorValues.Length; i++)
                {
                    Color value = colorValues[i]; string name = colorNames[i];
                    var swatch = new Ellipse { Width = 18, Height = 18, Fill = new SolidColorBrush(value), Stroke = LightTheme.Brush("#C5CED8"), StrokeThickness = 1 };
                    var option = new Button { Style = buttonStyle.Value, Content = swatch, Width = 29, Height = 30, ToolTip = name, Cursor = Cursors.Hand };
                    System.Windows.Automation.AutomationProperties.SetName(option, name);
                    option.Click += delegate { capture.SetColor(value); colorIndicator.Fill = new SolidColorBrush(value); colorPopup.IsOpen = false; };
                    colors.Children.Add(option);
                }
                colorPopup = MakePalette(colorButton, colors);

                Popup widthPopup = null;
                var widthButton = AddButton(row, "width", "线条粗细和文字大小", delegate { widthPopup.IsOpen = !widthPopup.IsOpen; });
                widthIndicator = new System.Windows.Shapes.Line { X1 = 1, X2 = 17, Y1 = 8, Y2 = 8, Stroke = LightTheme.Brush("#45535F"), StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                var widthCanvas = new Canvas { Width = 18, Height = 16 }; widthCanvas.Children.Add(widthIndicator); widthButton.Content = widthCanvas;
                var widths = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (double size in new[] { 2.0, 3.0, 6.0 })
                {
                    double selectedSize = size;
                    var line = new System.Windows.Shapes.Line { X1 = 1, X2 = 19, Y1 = 9, Y2 = 9, Stroke = LightTheme.Brush("#45535F"), StrokeThickness = size, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                    var surface = new Canvas { Width = 20, Height = 18 }; surface.Children.Add(line);
                    string name = size == 2 ? "细" : size == 3 ? "中" : "粗";
                    var option = new Button { Style = buttonStyle.Value, Content = surface, Width = 34, Height = 32, ToolTip = name, Cursor = Cursors.Hand };
                    System.Windows.Automation.AutomationProperties.SetName(option, name);
                    option.Click += delegate { capture.SetWidth(selectedSize); widthIndicator.StrokeThickness = selectedSize; widthPopup.IsOpen = false; };
                    widths.Children.Add(option);
                }
                widthPopup = MakePalette(widthButton, widths);
                AddDivider(row);
                AddButton(row, "undo", "撤销 (Ctrl+Z)", capture.Undo);
                AddButton(row, "save", "保存截图到文件", capture.Save);
                AddButton(row, "pin", "完成并固定到桌面", delegate { capture.Finish(CaptureResult.Pin); });
                AddButton(row, "copy", "复制并完成", delegate { capture.Finish(CaptureResult.Complete); });
                AddDivider(row);
                AddButton(row, "close", "取消 (Esc)", capture.Dispose);
                AddButton(row, "check", "完成并复制 (Enter)", delegate { capture.Finish(CaptureResult.Complete); });
                var scroll = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = row };
                return new Border { Background = Brushes.White, BorderBrush = LightTheme.Brush("#DCE2EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Child = scroll };
            }
            Popup MakePalette(Button anchor, UIElement contents)
            {
                var surface = new Border { Background = Brushes.White, BorderBrush = LightTheme.Brush("#DCE2EA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(5), Child = contents };
                var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Top, VerticalOffset = -7, AllowsTransparency = true, StaysOpen = false, Child = surface };
                surface.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args) { args.Handled = true; capture.Dispose(); };
                palettes.Add(popup); return popup;
            }
            static void AddDivider(Panel row)
            {
                row.Children.Add(new Border { Width = 1, Height = 20, Background = LightTheme.Brush("#E2E7EC"), Margin = new Thickness(5, 0, 5, 0) });
            }
            void AddTool(Panel row, string icon, string tip, CaptureTool tool)
            {
                var button = AddButton(row, icon, tip, delegate { capture.SetTool(tool); }); toolButtons[tool] = button;
            }
            static Button AddButton(Panel row, string icon, string tip, Action action)
            {
                var button = new Button { Content = ToolbarIcon(icon), ToolTip = tip, Style = buttonStyle.Value, Width = 34, Height = 34, Margin = new Thickness(1, 0, 1, 0), Cursor = Cursors.Hand };
                System.Windows.Automation.AutomationProperties.SetName(button, tip);
                button.Click += delegate { action(); }; row.Children.Add(button); return button;
            }
            static UIElement ToolbarIcon(string name)
            {
                var paths = new Dictionary<string, string>
                {
                    { "select", "M5,3 L5,18 L9,14 L12,21 L15,20 L12,13 L18,13 Z" },
                    { "rectangle", "M4,5 L20,5 L20,19 L4,19 Z" },
                    { "ellipse", "M12,4 A8,8 0 1 1 11.99,4" },
                    { "arrow", "M5,19 L19,5 M10,5 L19,5 L19,14" },
                    { "pen", "M4,17 L16,5 L19,8 L7,20 L4,20 Z M14,7 L17,10" },
                    { "mosaic", "M4,4 L20,4 L20,20 L4,20 Z M9,4 L9,20 M15,4 L15,20 M4,9 L20,9 M4,15 L20,15" },
                    { "text", "M4,5 L20,5 M12,5 L12,19 M8,19 L16,19" },
                    { "emoji", "M12,3 A9,9 0 1 1 11.99,3 M8,10 L8.05,10 M16,10 L16.05,10 M8,15 Q12,19 16,15" },
                    { "color", "M12,4 A8,8 0 1 1 11.99,4" },
                    { "width", "M4,12 L20,12" },
                    { "undo", "M9,7 L5,11 L9,15 M5,11 L15,11 Q20,11 20,16" },
                    { "save", "M12,3 L12,15 M8,11 L12,15 L16,11 M4,16 L4,20 L20,20 L20,16" },
                    { "pin", "M9,4 L15,4 L15,9 L18,12 L18,14 L6,14 L6,12 L9,9 Z M12,14 L12,21" },
                    { "copy", "M8,7 L18,7 L18,20 L8,20 Z M6,17 L4,17 L4,4 L14,4 L14,5" },
                    { "close", "M5,5 L19,19 M19,5 L5,19" },
                    { "check", "M4,12 L9,17 L20,6" }
                };
                var shape = new System.Windows.Shapes.Path { Data = Geometry.Parse(paths[name]), Stroke = LightTheme.Brush(name == "close" ? "#D45B5B" : name == "check" ? "#14A574" : "#45535F"), StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
                var canvas = new Canvas { Width = 24, Height = 24 }; canvas.Children.Add(shape);
                return new Viewbox { Width = 20, Height = 20, Child = canvas, IsHitTestVisible = false };
            }
            internal void CapturePointer() { canvas.CaptureMouse(); }
            internal void ReleasePointer() { canvas.ReleaseMouseCapture(); }
            internal void StartText(WindowsNative.Point position)
            {
                textPosition = position;
                textInput = new TextBox { MinWidth = 100, MaxWidth = Math.Max(100, (capture.selection.Right - position.X) * ScaleX), MinHeight = 32, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(capture.color), Background = Brushes.White, BorderBrush = Brushes.DodgerBlue, FontSize = Math.Max(18, capture.lineWidth * 8) * ScaleY };
                var input = textInput;
                canvas.Children.Add(input); Canvas.SetLeft(input, (position.X - Bounds.Left) * ScaleX); Canvas.SetTop(input, (position.Y - Bounds.Top) * ScaleY);
                input.LostKeyboardFocus += delegate { if (textInput == input) CommitText(); };
                input.PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { args.Handled = true; CommitText(); Focus(); } };
                input.Focus(); Keyboard.Focus(input);
            }
            internal void CommitText()
            {
                if (textInput == null) return;
                var input = textInput; textInput = null; canvas.Children.Remove(input);
                capture.AddText(textPosition, input.Text);
            }
            internal void Refresh(Drawing.Rectangle rect, bool showToolbar, CaptureTool selectedTool)
            {
                preview.InvalidateVisual();
                if (toolbar == null)
                {
                    if (!showToolbar) return;
                    toolbar = CreateToolbar();
                    canvas.Children.Add(toolbar);
                }
                foreach (var pair in toolButtons) pair.Value.Background = pair.Key == selectedTool ? LightTheme.Brush("#DCEEFF") : Brushes.Transparent;
                toolbar.Visibility = showToolbar ? Visibility.Visible : Visibility.Collapsed;
                if (!showToolbar) { foreach (var popup in palettes) popup.IsOpen = false; return; }
                colorIndicator.Fill = new SolidColorBrush(capture.color);
                widthIndicator.StrokeThickness = capture.lineWidth;
                toolbar.MaxWidth = Math.Max(200, canvas.ActualWidth - 16);
                toolbar.Measure(new Size(toolbar.MaxWidth, double.PositiveInfinity));
                double width = Math.Min(toolbar.MaxWidth, toolbar.DesiredSize.Width), height = toolbar.DesiredSize.Height;
                double x = (rect.Right - Bounds.Left) * ScaleX - width;
                double y = (rect.Bottom - Bounds.Top) * ScaleY + 8;
                x = Math.Max(8, Math.Min(canvas.ActualWidth - width - 8, x));
                if (y + height > canvas.ActualHeight - 8) y = (rect.Top - Bounds.Top) * ScaleY - height - 8;
                y = Math.Max(8, Math.Min(canvas.ActualHeight - height - 8, y));
                Canvas.SetLeft(toolbar, x); Canvas.SetTop(toolbar, y);
            }
            sealed class PreviewLayer : FrameworkElement
            {
                readonly CaptureOverlay owner;
                public PreviewLayer(CaptureOverlay owner) { this.owner = owner; }
                protected override void OnRender(DrawingContext dc)
                {
                    var capture = owner.capture; var rect = capture.selection;
                    if (rect.IsEmpty || ActualWidth <= 0 || ActualHeight <= 0) return;
                    double sx = ActualWidth / owner.Bounds.Width, sy = ActualHeight / owner.Bounds.Height;
                    dc.PushTransform(new MatrixTransform(sx, 0, 0, sy, -owner.Bounds.Left * sx, -owner.Bounds.Top * sy));
                    dc.PushClip(new RectangleGeometry(new Rect(rect.Left, rect.Top, rect.Width, rect.Height)));
                    var desktopRect = new Rect(capture.desktop.Left, capture.desktop.Top, capture.desktop.Width, capture.desktop.Height);
                    dc.DrawImage(capture.snapshot, desktopRect);
                    foreach (var annotation in capture.annotations) annotation.Draw(dc, capture.mosaic, desktopRect);
                    dc.Pop();
                    var stroke = new Pen(Brushes.LimeGreen, Math.Max(1, 2 / sx));
                    dc.DrawRectangle(null, stroke, new Rect(rect.Left, rect.Top, rect.Width, rect.Height));
                    double r = 4 / sx;
                    foreach (var p in new[] { new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Top), new Point(rect.Left, rect.Bottom), new Point(rect.Right, rect.Bottom), new Point((rect.Left + rect.Right) / 2.0, rect.Top), new Point((rect.Left + rect.Right) / 2.0, rect.Bottom), new Point(rect.Left, (rect.Top + rect.Bottom) / 2.0), new Point(rect.Right, (rect.Top + rect.Bottom) / 2.0) })
                        dc.DrawRectangle(Brushes.LimeGreen, null, new Rect(p.X - r, p.Y - r, r * 2, r * 2));
                    dc.Pop();
                }
            }
        }
    }
}
