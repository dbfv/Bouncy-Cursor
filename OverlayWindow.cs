using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;

namespace BounceCursor
{
    public class OverlayWindow : Window
    {
        private readonly Canvas _canvas;
        private Image? _activeImage;
        private ScaleTransform? _activeScale;

        private const double ShrinkScale = 0.8;
        private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan ReleaseDuration = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan HoldBeforeFade = TimeSpan.FromMilliseconds(80);
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(180);

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            _canvas = new Canvas();
            Content = _canvas;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        private (double x, double y) ToLocal(double screenX, double screenY)
        {
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            return (screenX / dpiX - Left, screenY / dpiY - Top);
        }

        public void OnPress(double screenX, double screenY)
        {
            var (x, y) = ToLocal(screenX, screenY);
            var cursorImg = GetCurrentCursorImage();
            if (cursorImg == null) return;

            var image = new Image
            {
                Source = cursorImg,
                Width = cursorImg.Width,
                Height = cursorImg.Height,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var scale = new ScaleTransform(1.0, 1.0);
            image.RenderTransform = scale;

            Canvas.SetLeft(image, x - image.Width / 2);
            Canvas.SetTop(image, y - image.Height / 2);

            // clear previous active shape immediately if still there
            if (_activeImage != null) _canvas.Children.Remove(_activeImage);

            _canvas.Children.Add(image);
            _activeImage = image;
            _activeScale = scale;

            var shrink = new DoubleAnimation
            {
                From = 1.0,
                To = ShrinkScale,
                Duration = PressDuration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        public void OnRelease()
        {
            if (_activeImage == null || _activeScale == null) return;
            var image = _activeImage;
            var scale = _activeScale;

            var bounceBack = new DoubleAnimation
            {
                To = 1.0,
                Duration = ReleaseDuration,
                EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceBack);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceBack);

            var fade = new DoubleAnimation
            {
                From = 1,
                To = 0,
                BeginTime = ReleaseDuration + HoldBeforeFade,
                Duration = FadeDuration
            };
            fade.Completed += (_, _) =>
            {
                _canvas.Children.Remove(image);
                if (_activeImage == image) { _activeImage = null; _activeScale = null; }
            };
            image.BeginAnimation(OpacityProperty, fade);
        }

        private static BitmapSource? GetCurrentCursorImage()
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (!GetCursorInfo(out ci) || ci.hCursor == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ci.hCursor, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch
            {
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFOPOINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public CURSORINFOPOINT ptScreenPos;
        }

        [DllImport("user32.dll")] private static extern bool GetCursorInfo(out CURSORINFO pci);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x80;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}