using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.ShowHide
{
    /// <summary>
    /// Fades or slides the tray window's content in and out. Progress runs from 0 (hidden) to 1
    /// (shown), and a new run starts from wherever the last one stopped, so a hide can turn into a
    /// show halfway. The window is per-pixel transparent (AllowsTransparency), so both only change
    /// the content:
    /// <list type="bullet">
    /// <item>Fade: the content's opacity.</item>
    /// <item>Slide: the content moves out through the window edge that faces the nearest screen edge.
    /// The window clips it, so it never passes over other windows or onto another monitor.</item>
    /// </list>
    /// </summary>
    internal sealed class TrayAnimator
    {
        private readonly UIElement _content;
        private TrayAnimation _kind;
        private ScreenEdge _edge;
        private double _progress = 1;
        private double _from;
        private double _to = 1;
        private double _durationMs;
        private long _start;
        private Action? _completed;
        private bool _running;

        public TrayAnimator(UIElement content)
        {
            _content = content;
        }

        /// <summary>1 when fully shown, 0 when fully hidden.</summary>
        public double Progress => _progress;

        public bool IsRunning => _running;

        /// <summary>Shows state <paramref name="progress"/> at once, for example fully hidden just before the window is shown.</summary>
        public void Jump(double progress, TrayAnimation kind, ScreenEdge edge)
        {
            Stop();
            _kind = kind;
            _edge = edge;
            _progress = progress;
            Apply(progress);
        }

        /// <summary>Animates to <paramref name="to"/> (0 or 1) and then calls <paramref name="completed"/>.</summary>
        public void Run(double to, TrayAnimation kind, TimeSpan duration, ScreenEdge edge, Action completed)
        {
            Stop();
            if (kind != _kind || edge != _edge) Clear();
            _kind = kind;
            _edge = edge;
            _from = _progress;
            _to = to;
            _completed = completed;
            _durationMs = kind == TrayAnimation.None ? 0 : duration.TotalMilliseconds * Math.Abs(to - _from);
            if (_durationMs < 1)
            {
                Finish();
                return;
            }
            _start = Stopwatch.GetTimestamp();
            _running = true;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>Stops a running animation where it is, without calling its completion.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            _completed = null;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double t = Math.Min(1, Stopwatch.GetElapsedTime(_start).TotalMilliseconds / _durationMs);
            // Decelerate when showing, accelerate when hiding.
            double eased = _to > _from ? 1 - Math.Pow(1 - t, 3) : t * t * t;
            _progress = _from + (_to - _from) * eased;
            if (t >= 1) Finish();
            else Apply(_progress);
        }

        private void Finish()
        {
            if (_running)
            {
                _running = false;
                CompositionTarget.Rendering -= OnRendering;
            }
            _progress = _to;
            Apply(_progress);
            Action? completed = _completed;
            _completed = null;
            completed?.Invoke();
        }

        private void Apply(double progress)
        {
            if (progress >= 1 || _kind == TrayAnimation.None)
            {
                Clear();
                return;
            }
            progress = Math.Max(0, progress);
            if (_kind == TrayAnimation.Fade)
            {
                _content.Opacity = progress;
                return;
            }
            // Slide, in whole device pixels so the content stays sharp.
            Size size = _content.RenderSize;
            DpiScale dpi = VisualTreeHelper.GetDpi(_content);
            double hidden = 1 - progress;
            double dx = 0, dy = 0;
            switch (_edge)
            {
                case ScreenEdge.Top: dy = -Math.Round(hidden * size.Height * dpi.DpiScaleY) / dpi.DpiScaleY; break;
                case ScreenEdge.Left: dx = -Math.Round(hidden * size.Width * dpi.DpiScaleX) / dpi.DpiScaleX; break;
                case ScreenEdge.Right: dx = Math.Round(hidden * size.Width * dpi.DpiScaleX) / dpi.DpiScaleX; break;
                default: dy = Math.Round(hidden * size.Height * dpi.DpiScaleY) / dpi.DpiScaleY; break;
            }
            _content.RenderTransform = new TranslateTransform(dx, dy);
        }

        private void Clear()
        {
            _content.Opacity = 1;
            _content.RenderTransform = Transform.Identity;
        }
    }
}
