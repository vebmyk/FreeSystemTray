using System;
using System.ComponentModel;
using System.Windows.Media;
using FreeSystemTray.TrayHost.Interop;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using MouseButton = System.Windows.Input.MouseButton;
using MsNative = ManagedShell.Interop.NativeMethods;

namespace FreeSystemTray.TrayHost
{
    public sealed class ManagedShellTrayIcon : ITrayIcon
    {
        private readonly ManagedShellTrayHost _host;

        internal ManagedShellTrayIcon(NotifyIcon source, ManagedShellTrayHost host)
        {
            Source = source;
            _host = host;
            Source.PropertyChanged += Source_PropertyChanged;
        }

        /// <summary>ManagedShell's icon (diagnostics: window, uID, GUID, version, callback message).</summary>
        public NotifyIcon Source { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Id => Source.GUID != Guid.Empty ? Source.GUID.ToString() : $"{Source.Path}:{Source.UID}";
        public ImageSource? Image => Source.Icon;
        public string? ToolTip => Source.Title;
        public bool IsHidden => Source.IsHidden;
        public string? OwnerPath => Source.Path;

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

        public void OnMouseEnter() => Source.IconMouseEnter(MouseHelper.GetCursorPositionParam());

        public void OnMouseLeave() => Source.IconMouseLeave(MouseHelper.GetCursorPositionParam());

        public void OnMouseMove() => Source.IconMouseMove(MouseHelper.GetCursorPositionParam());

        // Clicks mark an interaction in the tray window: flyouts that ask where the icon is a little
        // later, after the cursor has moved on, still get this window (PositionQueryRouting).
        // Same sequencing as RetroBar and Explorer: the left button's down is sent on release, other
        // buttons' on press (verified against Explorer in Phase 0).
        public void OnMouseDown(MouseButton button)
        {
            _host.NoteInteraction();
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

        internal void Detach() => Source.PropertyChanged -= Source_PropertyChanged;

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
        }
    }
}
