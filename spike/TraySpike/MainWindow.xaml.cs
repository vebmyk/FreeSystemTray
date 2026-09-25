using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TraySpike.Tray;

namespace TraySpike
{
    public partial class MainWindow : Window
    {
        private readonly ManagedShellTrayHost _host;
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly Dictionary<ITrayIcon, Rect> _rects = new();
        private readonly List<string> _balloons = new();
        private IntPtr _hwnd;
        private bool? _lastWeAreHost;
        private bool? _lastExplorerTopmost;
        private Rect _lastHostBounds;

        public MainWindow(ManagedShellTrayHost host)
        {
            _host = host;
            InitializeComponent();

            Resources["IconSize"] = App.Options.IconSize;
            Resources["CellSize"] = Math.Max(24, App.Options.IconSize + 8);

            IconList.ItemsSource = host.Icons;
            ((INotifyCollectionChanged)host.Icons).CollectionChanged += Icons_CollectionChanged;
            host.BalloonReceived += Host_BalloonReceived;

            SourceInitialized += MainWindow_SourceInitialized;
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;

            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW;
            if (!App.Options.Activating) ex |= Native.WS_EX_NOACTIVATE;
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);

            int x = App.Options.X ?? 700;
            int y = App.Options.Y ?? 200;
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Log.Write($"Spike window 0x{_hwnd.ToInt64():X} at {x},{y} (noactivate={!App.Options.Activating})");
        }

        #region Logging of icon changes

        private void Icons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (ITrayIcon icon in e.NewItems)
                {
                    icon.PropertyChanged += Icon_PropertyChanged;
                    Log.Write("ICON ADDED   " + IconDescriber.OneLine(icon));
                }
            }
            if (e.OldItems != null)
            {
                foreach (ITrayIcon icon in e.OldItems)
                {
                    icon.PropertyChanged -= Icon_PropertyChanged;
                    _rects.Remove(icon);
                    Log.Write("ICON REMOVED " + IconDescriber.OneLine(icon));
                }
            }
        }

        private void Icon_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is ITrayIcon icon)
                Log.Write($"ICON CHANGED ({e.PropertyName}) " + IconDescriber.OneLine(icon));
        }

        private void Host_BalloonReceived(object? sender, TrayBalloonEventArgs e)
        {
            string line = $"BALLOON from '{e.Icon?.ToolTip}': title='{e.Title}' text='{e.Text}'";
            _balloons.Add($"{DateTime.Now:HH:mm:ss} {line}");
            Log.Write(line);
        }

        #endregion

        #region Periodic: icon rects, host bounds, host status, state.json

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_hwnd == IntPtr.Zero) return;
            UpdateRects();
            WatchHostStatus();
            WriteState();
        }

        private void UpdateRects()
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);

            foreach (ITrayIcon icon in _host.Icons)
            {
                if (IconList.ItemContainerGenerator.ContainerFromItem(icon) is not FrameworkElement c ||
                    !c.IsVisible || c.ActualWidth <= 0)
                {
                    _rects.Remove(icon);
                    continue;
                }
                Point p = c.PointToScreen(new Point(0, 0));
                var r = new Rect(p.X, p.Y, c.ActualWidth * dpi.DpiScaleX, c.ActualHeight * dpi.DpiScaleY);
                if (!_rects.TryGetValue(icon, out Rect old) || old != r)
                {
                    _rects[icon] = r;
                    icon.SetScreenBounds(r);
                }
            }

            Point wp = PointToScreen(new Point(0, 0));
            var hostBounds = new Rect(wp.X, wp.Y, ActualWidth * dpi.DpiScaleX, ActualHeight * dpi.DpiScaleY);
            if (hostBounds != _lastHostBounds)
            {
                _lastHostBounds = hostBounds;
                _host.SetHostBounds(hostBounds, NearestEdge(hostBounds));
            }
        }

        private ScreenEdge NearestEdge(Rect b)
        {
            var wa = System.Windows.Forms.Screen.FromHandle(_hwnd).WorkingArea;
            double cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2;
            var d = new (ScreenEdge edge, double dist)[]
            {
                (ScreenEdge.Left, cx - wa.Left), (ScreenEdge.Top, cy - wa.Top),
                (ScreenEdge.Right, wa.Right - cx), (ScreenEdge.Bottom, wa.Bottom - cy)
            };
            return d.OrderBy(t => t.dist).First().edge;
        }

        private void WatchHostStatus()
        {
            var s = ShellState.Capture(_host.TrayWindowHandle);
            bool weAreHost = (bool)s["weAreHost"]!;
            if (_lastWeAreHost != weAreHost)
            {
                Log.Write($"HOST STATUS: weAreHost={weAreHost} findWindow={s["findWindowResult"]} explorerTray={s["explorerTray"]}");
                _lastWeAreHost = weAreHost;
            }
            if (s.TryGetValue("explorerTrayTopmost", out object? t) && t is bool top && _lastExplorerTopmost != top)
            {
                Log.Write($"EXPLORER TASKBAR topmost={top}");
                _lastExplorerTopmost = top;
            }
        }

        private void WriteState()
        {
            var state = new Dictionary<string, object?>
            {
                ["spikePid"] = Environment.ProcessId,
                ["spikeHwnd"] = $"0x{_hwnd.ToInt64():X}",
                ["window"] = new[] { (int)_lastHostBounds.Left, (int)_lastHostBounds.Top, (int)_lastHostBounds.Width, (int)_lastHostBounds.Height },
                ["shell"] = ShellState.Capture(_host.TrayWindowHandle),
                ["iconCount"] = _host.Icons.Count,
                ["icons"] = _host.Icons.Select(i => IconDescriber.Describe(i, _rects.TryGetValue(i, out Rect r) ? r : null)).ToList(),
                ["balloons"] = _balloons.ToList(),
            };
            StateFile.WriteIfChanged(Path.Combine(App.Options.LogDir, "state.json"), state);
        }

        #endregion

        #region Mouse routing

        private static ITrayIcon? IconOf(object sender) => (sender as FrameworkElement)?.DataContext as ITrayIcon;

        private void Icon_MouseEnter(object sender, MouseEventArgs e)
        {
            if (IconOf(sender) is not { } icon) return;
            if (sender is FrameworkElement fe)
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(fe);
                Point p = fe.PointToScreen(new Point(0, 0));
                icon.SetScreenBounds(new Rect(p.X, p.Y, fe.ActualWidth * dpi.DpiScaleX, fe.ActualHeight * dpi.DpiScaleY));
            }
            icon.OnMouseEnter();
            e.Handled = true;
        }

        private void Icon_MouseLeave(object sender, MouseEventArgs e)
        {
            IconOf(sender)?.OnMouseLeave();
            e.Handled = true;
        }

        private void Icon_MouseMove(object sender, MouseEventArgs e)
        {
            IconOf(sender)?.OnMouseMove();
            e.Handled = true;
        }

        private void Icon_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IconOf(sender) is not { } icon) return;
            Log.Write($"MOUSE DOWN {e.ChangedButton} (clicks={e.ClickCount}) on '{icon.ToolTip}' fg=0x{Native.GetForegroundWindow().ToInt64():X}");
            icon.OnMouseDown(e.ChangedButton);
            e.Handled = true;
        }

        private void Icon_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (IconOf(sender) is not { } icon) return;
            Log.Write($"MOUSE UP   {e.ChangedButton} on '{icon.ToolTip}'");
            icon.OnMouseUp(e.ChangedButton);
            e.Handled = true;
        }

        #endregion

        private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    }
}
