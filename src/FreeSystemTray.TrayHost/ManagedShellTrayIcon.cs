using System;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.TrayHost.Interop;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using MouseButton = System.Windows.Input.MouseButton;
using MsNative = ManagedShell.Interop.NativeMethods;

namespace FreeSystemTray.TrayHost
{
    public sealed class ManagedShellTrayIcon : ITrayIcon
    {
        private const uint NIN_POPUPOPEN = 0x0406, NIN_POPUPCLOSE = 0x0407;

        private readonly ManagedShellTrayHost _host;
        private readonly DispatcherTimer _popupDelay;
        private bool _pointerOver;
        private bool _popupOpen;
        private bool _clicked;
        private bool _showsToolTip;

        internal ManagedShellTrayIcon(NotifyIcon source, ManagedShellTrayHost host)
        {
            Source = source;
            _host = host;
            Source.PropertyChanged += Source_PropertyChanged;
            // Explorer opens a version 4 icon's popup after the tooltip's delay: the double-click time.
            _popupDelay = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(NativeMethods.GetDoubleClickTime()) };
            _popupDelay.Tick += PopupDelay_Tick;
            _showsToolTip = ComputeShowsToolTip();
        }

        /// <summary>ManagedShell's icon (diagnostics: window, uID, GUID, version, callback message).</summary>
        public NotifyIcon Source { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id => Source.GUID != Guid.Empty ? Source.GUID.ToString() : $"{Source.Path}:{Source.UID}";
        public Guid Guid => Source.GUID;
        public uint Uid => Source.UID;
        public ImageSource? Image => Source.Icon;
        public string? ToolTip => Source.Title;
        public bool IsHidden => Source.IsHidden;
        public string? OwnerPath => Source.Path;

        /// <summary>
        /// As in Explorer: icons before version 4 always get the standard tooltip; version 4 icons only
        /// when their app's last add or modify had NIF_SHOWTIP, otherwise they get NIN_POPUPOPEN and show
        /// their own popup (FINDINGS.md, Phase 6). Unknown (an icon imported from Explorer's tray whose app
        /// has not sent anything since): the tooltip.
        /// </summary>
        public bool ShowsToolTip => _showsToolTip;

        /// <summary>Whether the app has had NIN_POPUPOPEN and not yet NIN_POPUPCLOSE (diagnostics).</summary>
        public bool PopupOpen => _popupOpen;

        public ExplorerPlacement ExplorerPlacement { get; private set; }

        private bool ComputeShowsToolTip() => Source.Version < 4 || _host.ShowTipOf(Source) != false;

        /// <summary>Called by the host after an add, modify or version change of this icon.</summary>
        internal void RefreshToolTipMode()
        {
            bool shows = ComputeShowsToolTip();
            if (shows == _showsToolTip) return;
            _showsToolTip = shows;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsToolTip)));
        }

        /// <summary>Set by the host after it read Explorer's tray.</summary>
        internal void SetExplorerPlacement(ExplorerPlacement placement)
        {
            if (placement == ExplorerPlacement) return;
            ExplorerPlacement = placement;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExplorerPlacement)));
        }

        public void SetScreenBounds(PixelRect bounds)
        {
            // Shell_NotifyIconGetRect asks twice: message 1 wants the top-left point, message 2 the size.
            // ManagedShell answers message 2 with Placement.Right/Bottom, so those carry width/height.
            Source.Placement = new MsNative.Rect
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Width,
                Bottom = bounds.Height
            };
        }

        // Hovering, as Explorer does it (measured in Phase 6): the pointer arriving sends WM_MOUSEMOVE
        // only (ManagedShell sends WM_MOUSEHOVER, and NIN_POPUPOPEN at once); a version 4 icon without
        // the standard tooltip gets NIN_POPUPOPEN after the delay and NIN_POPUPCLOSE when the pointer
        // leaves or a button goes down, after which it does not open again until the pointer comes back.
        // Nothing else is sent when the pointer leaves (ManagedShell sends WM_MOUSELEAVE).
        public void OnMouseEnter()
        {
            _pointerOver = true;
            _clicked = false;
            Source.IconMouseMove(MouseHelper.GetCursorPositionParam());
            if (WantsPopup) _popupDelay.Start();
        }

        public void OnMouseLeave()
        {
            _pointerOver = false;
            _popupDelay.Stop();
            ClosePopup();
        }

        public void OnMouseMove() => Source.IconMouseMove(MouseHelper.GetCursorPositionParam());

        private bool WantsPopup => Source.Version >= 4 && !_showsToolTip;

        private void PopupDelay_Tick(object? sender, EventArgs e)
        {
            _popupDelay.Stop();
            if (!_pointerOver || _clicked || _popupOpen || !WantsPopup || !NativeMethods.IsWindow(Source.HWnd)) return;
            _popupOpen = true;
            Notify(NIN_POPUPOPEN, MouseHelper.GetCursorPositionParam());
        }

        private void ClosePopup()
        {
            if (!_popupOpen) return;
            _popupOpen = false;
            Notify(NIN_POPUPCLOSE, 0);
        }

        /// <summary>A version 4 callback: the event and uID in lParam, the pointer's position in wParam.</summary>
        private void Notify(uint message, uint position) =>
            NativeMethods.SendNotifyMessage(Source.HWnd, Source.CallbackMessage, (IntPtr)position, (IntPtr)(message | (Source.UID << 16)));

        // Clicks mark an interaction in the tray window: flyouts that ask where the icon is a little
        // later, after the cursor has moved on, still get this window (PositionQueryRouting).
        // Same sequencing as RetroBar and Explorer: the left button's down is sent on release, other
        // buttons' on press (verified against Explorer in Phase 0).
        public void OnMouseDown(MouseButton button)
        {
            _host.NoteInteraction();
            _clicked = true;
            _popupDelay.Stop();
            ClosePopup();
            if (button != MouseButton.Left)
                Source.IconMouseDown(button, MouseHelper.GetCursorPositionParam(), (int)NativeMethods.GetDoubleClickTime());
        }

        public void OnMouseUp(MouseButton button)
        {
            _host.NoteInteraction();
            uint pos = MouseHelper.GetCursorPositionParam();
            int doubleClickTime = (int)NativeMethods.GetDoubleClickTime();
            if (button == MouseButton.Left)
                Source.IconMouseDown(button, pos, doubleClickTime);
            Source.IconMouseUp(button, pos, doubleClickTime);
        }

        internal void Detach()
        {
            Source.PropertyChanged -= Source_PropertyChanged;
            _popupDelay.Stop();
        }

        private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            string? mapped = e.PropertyName switch
            {
                nameof(NotifyIcon.Icon) => nameof(Image),
                nameof(NotifyIcon.Title) => nameof(ToolTip),
                nameof(NotifyIcon.IsHidden) => nameof(IsHidden),
                _ => null
            };
            if (mapped != null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(mapped));
            // The app hiding or showing its icon changes Explorer's tray too.
            if (mapped == nameof(IsHidden)) _host.RequestExplorerRead();
        }
    }
}
