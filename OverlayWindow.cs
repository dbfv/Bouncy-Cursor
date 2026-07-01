using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BounceCursor
{
    public class OverlayWindow : Window
    {
        private readonly Canvas _canvas;

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

        public void ShowBounceAt(double screenX, double screenY)
        {
            var source = PresentationSource.FromVisual(this);
            double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double x = screenX / dpiX - Left;
            double y = screenY / dpiY - Top;

            var circle = new Ellipse
            {
                Width = 24,
                Height = 24,
                Fill = new SolidColorBrush(Color.FromArgb(200, 30, 144, 255)),
                RenderTransformOrigin = new Point(0.5, 0.5)
            };
            var scale = new ScaleTransform(0.2, 0.2);
            circle.RenderTransform = scale;

            Canvas.SetLeft(circle, x - circle.Width / 2);
            Canvas.SetTop(circle, y - circle.Height / 2);
            _canvas.Children.Add(circle);

            var growAnim = new DoubleAnimation
            {
                From = 0.2,
                To = 1.3,
                Duration = TimeSpan.FromMilliseconds(350),
                EasingFunction = new BounceEase { Bounces = 2, Bounciness = 3, EasingMode = EasingMode.EaseOut }
            };

            var shrinkAnim = new DoubleAnimation
            {
                From = 1.3,
                To = 0,
                BeginTime = TimeSpan.FromMilliseconds(350),
                Duration = TimeSpan.FromMilliseconds(250)
            };

            var fadeAnim = new DoubleAnimation
            {
                From = 1,
                To = 0,
                BeginTime = TimeSpan.FromMilliseconds(350),
                Duration = TimeSpan.FromMilliseconds(250)
            };

            growAnim.Completed += (_, _) =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrinkAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrinkAnim);
                circle.BeginAnimation(OpacityProperty, fadeAnim);
            };
            shrinkAnim.Completed += (_, _) => _canvas.Children.Remove(circle);

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, growAnim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, growAnim);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_LAYERED = 0x80000;
        private const int WS_EX_TOOLWINDOW = 0x80;

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}