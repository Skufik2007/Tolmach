using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Tolmach;

// "Drag a rectangle over the text": every monitor is frozen as a picture
// with a dark veil, the chosen part lights up. The picture is what gets
// read, so a moving game or video doesn't matter.
internal static class RegionSelector
{
    public sealed record Selection(System.Drawing.Bitmap Image, System.Drawing.Rectangle ScreenArea);

    static bool _active;

    public static Task<Selection?> SelectAsync()
    {
        var done = new TaskCompletionSource<Selection?>();
        if (_active)
        {
            done.SetResult(null);
            return done.Task;
        }
        _active = true;

        // The hotkey's Alt or Win is still held. Released over the overlay,
        // it would put the overlay into Windows' menu mode and the first
        // click would only end that mode; an unassigned key tapped now makes
        // the release part of a combination instead.
        if (Native.IsDown(Native.VK_MENU) || Native.IsDown(Native.VK_LWIN) || Native.IsDown(Native.VK_RWIN))
        {
            Native.SendKeys((0xE8, false), (0xE8, true));
        }

        var overlays = new List<Overlay>();
        void Finish(Selection? selection)
        {
            foreach (var overlay in overlays)
            {
                overlay.Close();
                overlay.Dispose();
            }
            overlays.Clear();
            _active = false;
            done.TrySetResult(selection);
        }

        foreach (var (_, bounds) in Native.AllMonitors())
        {
            var area = System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            overlays.Add(new Overlay(ScreenOcr.Capture(area), area, Finish));
        }
        foreach (var overlay in overlays)
        {
            overlay.ShowOver();
        }

        // Keyboard focus to the overlay under the cursor, for Esc.
        Native.GetCursorPos(out var cursor);
        foreach (var overlay in overlays)
        {
            if (overlay.Area.Contains(cursor.X, cursor.Y))
            {
                overlay.Activate();
                Native.SetForegroundWindow(new WindowInteropHelper(overlay).Handle);
            }
        }
        return done.Task;
    }

    sealed class Overlay : Window, IDisposable
    {
        readonly System.Drawing.Bitmap _screen;
        readonly Action<Selection?> _finish;
        readonly Canvas _canvas = new();
        readonly Path _veil;
        readonly Rectangle _frame;
        readonly RectangleGeometry _hole = new(Rect.Empty);
        Point? _start;

        public System.Drawing.Rectangle Area { get; }

        public Overlay(System.Drawing.Bitmap screen, System.Drawing.Rectangle area, Action<Selection?> finish)
        {
            _screen = screen;
            Area = area;
            _finish = finish;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Cross;
            Title = "Толмач — выделите текст";
            Background = Brushes.Black;

            var image = new Image
            {
                Source = ToBitmapSource(screen),
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);

            // The veil covers everything except the hole.
            var full = new RectangleGeometry(new Rect(0, 0, 100000, 100000));
            _veil = new Path
            {
                Fill = Theme.Brush(0x80, 0x00, 0x00, 0x00),
                Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, _hole),
                IsHitTestVisible = false,
            };
            _frame = new Rectangle
            {
                Stroke = Theme.AccentBrush,
                StrokeThickness = 1.5,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            var hint = new Border
            {
                Background = Theme.Card,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 7, 14, 8),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 24, 0, 0),
                IsHitTestVisible = false,
                Child = Theme.Label("Обведите текст для перевода  ·  Esc — отмена", 13),
            };

            var root = new Grid();
            root.Children.Add(image);
            root.Children.Add(_canvas);
            _canvas.Children.Add(_veil);
            _canvas.Children.Add(_frame);
            root.Children.Add(hint);
            Content = root;

            MouseLeftButtonDown += (_, e) =>
            {
                _start = e.GetPosition(_canvas);
                CaptureMouse();
                hint.Visibility = Visibility.Collapsed;
            };
            MouseMove += (_, e) =>
            {
                if (_start is Point start)
                {
                    Update(new Rect(start, e.GetPosition(_canvas)));
                }
            };
            MouseLeftButtonUp += (_, e) =>
            {
                if (_start is not Point start)
                {
                    return;
                }
                ReleaseMouseCapture();
                _start = null;
                var rect = new Rect(start, e.GetPosition(_canvas));
                if (rect.Width < 6 || rect.Height < 6)
                {
                    Update(Rect.Empty);
                    hint.Visibility = Visibility.Visible;
                    return;
                }
                _finish(Crop(rect));
            };
            MouseRightButtonUp += (_, _) => _finish(null);
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    _finish(null);
                }
            };
        }

        public void ShowOver()
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.SetWindowPos(hwnd, new IntPtr(-1), Area.Left, Area.Top, Area.Width, Area.Height, 0x0040);
            Show();
            // Showing on a monitor with other scaling may resize it once more.
            Native.SetWindowPos(hwnd, new IntPtr(-1), Area.Left, Area.Top, Area.Width, Area.Height, 0x0040);
        }

        void Update(Rect rect)
        {
            _hole.Rect = rect;
            if (rect.IsEmpty)
            {
                _frame.Visibility = Visibility.Collapsed;
                return;
            }
            _frame.Visibility = Visibility.Visible;
            Canvas.SetLeft(_frame, rect.Left - 1);
            Canvas.SetTop(_frame, rect.Top - 1);
            _frame.Width = rect.Width + 2;
            _frame.Height = rect.Height + 2;
        }

        // The window is in DIPs, the picture in pixels.
        Selection Crop(Rect rect)
        {
            double sx = _screen.Width / Math.Max(1, _canvas.ActualWidth);
            double sy = _screen.Height / Math.Max(1, _canvas.ActualHeight);
            var pixels = System.Drawing.Rectangle.FromLTRB(
                (int)Math.Floor(rect.Left * sx), (int)Math.Floor(rect.Top * sy),
                (int)Math.Ceiling(rect.Right * sx), (int)Math.Ceiling(rect.Bottom * sy));
            pixels.Intersect(new System.Drawing.Rectangle(0, 0, _screen.Width, _screen.Height));
            var crop = _screen.Clone(pixels, _screen.PixelFormat);
            pixels.Offset(Area.Location);
            return new Selection(crop, pixels);
        }

        static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
        {
            IntPtr hbitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);

        public void Dispose() => _screen.Dispose();
    }
}
