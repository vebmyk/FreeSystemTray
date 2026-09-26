using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.Diagnostics
{
    /// <summary>
    /// With --diag DIR: writes DIR\state.json every 250 ms when it changes. It lists the host status,
    /// Explorer's taskbar flags, the window's bounds and visibility, and every icon with its screen
    /// rectangle. The smoke test reads it to find icons to click.
    /// </summary>
    internal sealed class StateWriter : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly ManagedShellTrayHost _host;
        private readonly TrayWindow _window;
        private readonly string _path;
        private readonly DispatcherTimer _timer;
        private readonly List<string> _balloons = new();
        private string? _last;

        public StateWriter(ManagedShellTrayHost host, TrayWindow window, string dir)
        {
            _host = host;
            _window = window;
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "state.json");
            _host.BalloonReceived += Host_BalloonReceived;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Write();
            _timer.Start();
        }

        private void Host_BalloonReceived(object? sender, TrayBalloonEventArgs e) =>
            _balloons.Add($"{DateTime.Now:HH:mm:ss} '{e.Icon?.ToolTip}': {e.Title} / {e.Text}");

        private void Write()
        {
            PixelRect w = _window.ScreenBounds;
            PixelRect hotspot = _window.ShowHide?.HotspotZone ?? default;
            var state = new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId,
                ["windowHandle"] = Hex(_window.Handle),
                ["window"] = new[] { w.Left, w.Top, w.Width, w.Height },
                ["visible"] = _window.IsVisible,
                ["shown"] = _window.ShowHide?.IsShown,
                ["trayWindowVisible"] = _host.TrayWindowVisible,
                ["topmost"] = (NativeMethods.GetWindowLong(_window.Handle, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0,
                ["behindFullScreen"] = _window.BehindFullScreen == IntPtr.Zero ? null : Hex(_window.BehindFullScreen),
                ["autoHide"] = _window.ShowHide?.AutoHide,
                ["hotkey"] = _window.ShowHide?.Hotkey?.ToString(),
                ["hotspot"] = hotspot.IsEmpty ? null : new[] { hotspot.Left, hotspot.Top, hotspot.Width, hotspot.Height },
                ["shell"] = Shell(),
                ["iconCount"] = _host.Icons.Count,
                ["icons"] = _host.Icons.Select(Describe).ToList(),
                ["balloons"] = _balloons.ToList()
            };
            string json = JsonSerializer.Serialize(state, JsonOptions);
            if (json == _last) return;
            _last = json;
            try
            {
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json, Encoding.UTF8);
                File.Move(temp, _path, true);
            }
            catch (IOException)
            {
                // Diagnostics only; the next tick tries again.
            }
        }

        private Dictionary<string, object?> Shell()
        {
            IntPtr ours = _host.TrayWindowHandle;
            IntPtr first = NativeMethods.FindWindow("Shell_TrayWnd", null);
            IntPtr explorer = IntPtr.Zero, h = IntPtr.Zero;
            while ((h = NativeMethods.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
            {
                if (h != ours)
                {
                    explorer = h;
                    break;
                }
            }
            var d = new Dictionary<string, object?>
            {
                ["ourTray"] = Hex(ours),
                ["findWindowResult"] = Hex(first),
                ["weAreHost"] = _host.HoldsTrayRole,
                ["explorerTray"] = Hex(explorer)
            };
            if (explorer != IntPtr.Zero)
            {
                d["explorerTrayTopmost"] = (NativeMethods.GetWindowLong(explorer, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) != 0;
                d["explorerTrayVisible"] = NativeMethods.IsWindowVisible(explorer);
            }
            return d;
        }

        private Dictionary<string, object?> Describe(ITrayIcon icon)
        {
            var d = new Dictionary<string, object?>
            {
                ["id"] = icon.Id,
                ["tip"] = icon.ToolTip,
                ["hidden"] = icon.IsHidden,
                ["path"] = icon.OwnerPath
            };
            if (icon is ManagedShellTrayIcon ms)
            {
                var ni = ms.Source;
                NativeMethods.GetWindowThreadProcessId(ni.HWnd, out uint pid);
                d["hwnd"] = Hex(ni.HWnd);
                d["pid"] = pid;
                d["process"] = ProcessInfo.ImagePath(pid) is string exe ? Path.GetFileNameWithoutExtension(exe) : null;
                d["uid"] = ni.UID;
                d["guid"] = ni.GUID == Guid.Empty ? null : ni.GUID.ToString();
                d["version"] = ni.Version;
                d["callbackMsg"] = $"0x{ni.CallbackMessage:X}";
                d["windowAlive"] = NativeMethods.IsWindow(ni.HWnd);
            }
            if (icon.Image is BitmapSource bs) d["iconPx"] = $"{bs.PixelWidth}x{bs.PixelHeight}";
            if (_window.IconBounds.TryGetValue(icon, out PixelRect r)) d["rect"] = new[] { r.Left, r.Top, r.Width, r.Height };
            return d;
        }

        private static string Hex(IntPtr h) => $"0x{h.ToInt64():X}";

        public void Dispose()
        {
            _timer.Stop();
            _host.BalloonReceived -= Host_BalloonReceived;
        }
    }
}
