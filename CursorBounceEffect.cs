using System;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BounceCursor
{
    public class CursorBounceEffect
    {
        private CursorKind _activeKind = CursorKind.None;
        private DispatcherTimer? _timer;
        private DateTime _phaseStart;

        private const double ShrinkScale = 0.8;
        private static readonly TimeSpan PressDuration = TimeSpan.FromMilliseconds(150);
        private static readonly TimeSpan ReleaseDuration = TimeSpan.FromMilliseconds(250);

        private readonly QuadraticEase _pressEase = new() { EasingMode = EasingMode.EaseOut };
        private readonly ElasticEase _releaseEase = new() { Oscillations = 1, Springiness = 4, EasingMode = EasingMode.EaseOut };

        public void OnPress()
        {
            var kind = CursorAnimator.GetActiveCursorKind();
            if (kind == CursorKind.None) return; // chỉ xử lý Arrow / Hand
            _activeKind = kind;
            StartPhase(1.0, ShrinkScale, PressDuration, _pressEase, null);
        }

        public void OnRelease()
        {
            if (_activeKind == CursorKind.None) return;
            StartPhase(ShrinkScale, 1.0, ReleaseDuration, _releaseEase, () =>
            {
                CursorAnimator.RestoreAll();
                _activeKind = CursorKind.None;
            });
        }

        private void StartPhase(double from, double to, TimeSpan duration, IEasingFunction ease, Action? onDone)
        {
            _timer?.Stop();
            _phaseStart = DateTime.Now;
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) =>
            {
                double t = Math.Min(1.0, (DateTime.Now - _phaseStart).TotalMilliseconds / duration.TotalMilliseconds);
                double eased = ease.Ease(t);
                CursorAnimator.ApplyScale(_activeKind, from + (to - from) * eased);

                if (t >= 1.0)
                {
                    _timer!.Stop();
                    onDone?.Invoke();
                }
            };
            _timer.Start();
        }
    }
}