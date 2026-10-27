using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenLine
{
    internal enum CaptureTool { Select, Rectangle, Ellipse, Arrow, Pen, Mosaic, Text, Emoji }
    internal sealed class CaptureAnnotation
    {
        public CaptureTool Tool;
        public readonly List<Point> Points = new List<Point>();
        public Color Color;
        public double Width;
        public string Text;

        public void Draw(DrawingContext dc, ImageSource mosaic, Rect desktop)
        {
            if (Points.Count == 0) return;
            var brush = new SolidColorBrush(Color);
            var pen = new Pen(brush, Width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            Point a = Points[0], b = Points[Points.Count - 1];
            if (Tool == CaptureTool.Rectangle || Tool == CaptureTool.Ellipse)
            {
                var rect = new Rect(a, b);
                if (Tool == CaptureTool.Rectangle) dc.DrawRectangle(null, pen, rect);
                else dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
            }
            else if (Tool == CaptureTool.Arrow)
            {
                Vector v = b - a;
                if (v.Length < 1) return;
                v.Normalize(); Vector side = new Vector(-v.Y, v.X);
                double head = Math.Max(12, Width * 4);
                dc.DrawLine(pen, a, b);
                dc.DrawLine(pen, b, b - v * head + side * head * .5);
                dc.DrawLine(pen, b, b - v * head - side * head * .5);
            }
            else if (Tool == CaptureTool.Pen || Tool == CaptureTool.Mosaic)
            {
                var path = new StreamGeometry();
                using (var context = path.Open())
                {
                    context.BeginFigure(a, false, false);
                    if (Points.Count == 1) context.LineTo(new Point(a.X + .01, a.Y), true, false);
                    else for (int i = 1; i < Points.Count; i++) context.LineTo(Points[i], true, false);
                }
                path.Freeze();
                if (Tool == CaptureTool.Pen) dc.DrawGeometry(null, pen, path);
                else
                {
                    var wide = new Pen(Brushes.Black, Math.Max(12, Width * 6)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    dc.PushClip(path.GetWidenedPathGeometry(wide)); dc.DrawImage(mosaic, desktop); dc.Pop();
                }
            }
            else if ((Tool == CaptureTool.Text || Tool == CaptureTool.Emoji) && !string.IsNullOrEmpty(Text))
            {
                var formatted = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(Tool == CaptureTool.Emoji ? "Segoe UI Emoji" : "Microsoft YaHei"), Math.Max(18, Width * 8), brush, 1.0);
                dc.DrawText(formatted, a);
            }
        }
    }

    internal static class CaptureRendering
    {
        public static BitmapSource Pixelate(BitmapSource source)
        {
            int width = Math.Max(1, source.PixelWidth / 12), height = Math.Max(1, source.PixelHeight / 12);
            var reduced = Render(source, width, height, BitmapScalingMode.LowQuality);
            return Render(reduced, source.PixelWidth, source.PixelHeight, BitmapScalingMode.NearestNeighbor);
        }
        static BitmapSource Render(BitmapSource image, int width, int height, BitmapScalingMode mode)
        {
            var visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, mode);
            using (var dc = visual.RenderOpen()) dc.DrawImage(image, new Rect(0, 0, width, height));
            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual); result.Freeze(); return result;
        }
        public static BitmapSource Compose(BitmapSource source, BitmapSource mosaic, System.Drawing.Rectangle desktop, System.Drawing.Rectangle selection, IList<CaptureAnnotation> annotations)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, selection.Width, selection.Height)));
                dc.PushTransform(new TranslateTransform(-selection.Left, -selection.Top));
                var desktopRect = new Rect(desktop.Left, desktop.Top, desktop.Width, desktop.Height);
                dc.DrawImage(source, desktopRect);
                foreach (var annotation in annotations) annotation.Draw(dc, mosaic, desktopRect);
                dc.Pop(); dc.Pop();
            }
            var output = new RenderTargetBitmap(selection.Width, selection.Height, 96, 96, PixelFormats.Pbgra32);
            output.Render(visual); output.Freeze(); return output;
        }
    }
}
