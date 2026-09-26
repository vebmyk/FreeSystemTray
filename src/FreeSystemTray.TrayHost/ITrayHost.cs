using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;

namespace FreeSystemTray.TrayHost
{
    /// <summary>
    /// The only place that talks to the shell. The UI binds to <see cref="Icons"/> and routes mouse
    /// input back through <see cref="ITrayIcon"/>. Collection and property changes are raised on the
    /// thread that created the host (the UI thread).
    /// </summary>
    public interface ITrayHost : IDisposable
    {
        ReadOnlyObservableCollection<ITrayIcon> Icons { get; }

        /// <summary>True while our Shell_TrayWnd is the one apps send their icon messages to.</summary>
        bool HoldsTrayRole { get; }

        /// <summary>Take over the tray host role, import Explorer's icons and ask running apps to re-register.</summary>
        void Start();

        /// <summary>
        /// Where the tray window is, in physical pixels, and the screen edge it is nearest to. Apps
        /// use this to place menus and flyouts for clicks that start in the tray window.
        /// </summary>
        void SetTrayWindowBounds(PixelRect bounds, ScreenEdge edge);

        /// <summary>
        /// Where the tray's hidden-icons flyout is while it is open, empty while it is closed. The
        /// pointer on it counts as on the tray window when apps ask where their icon is.
        /// </summary>
        void SetOverflowBounds(PixelRect bounds);

        /// <summary>
        /// Whether the tray window is shown. While it is hidden, Explorer answers icon- and
        /// taskbar-position queries, so menus and flyouts open at the taskbar; icons keep registering.
        /// </summary>
        bool TrayWindowVisible { get; set; }

        event EventHandler<TrayBalloonEventArgs>? BalloonReceived;
    }

    public interface ITrayIcon : INotifyPropertyChanged
    {
        /// <summary>Stable identity: the GUID if the app supplied one, else "exe path:uID".</summary>
        string Id { get; }

        /// <summary>The GUID the app identifies the icon with, or <see cref="Guid.Empty"/> if it uses its window and <see cref="Uid"/>.</summary>
        Guid Guid { get; }

        /// <summary>The app's number for the icon (uID), unique among its window's icons.</summary>
        uint Uid { get; }

        ImageSource? Image { get; }
        string? ToolTip { get; }

        /// <summary>
        /// Whether the tray shows the standard tooltip for the icon, as Explorer would: not for a
        /// version 4 icon whose app did not ask for it (NIF_SHOWTIP); that app gets NIN_POPUPOPEN when
        /// the pointer rests on the icon and shows its own popup.
        /// </summary>
        bool ShowsToolTip { get; }

        /// <summary>The app has hidden its icon (NIS_HIDDEN).</summary>
        bool IsHidden { get; }

        string? OwnerPath { get; }

        /// <summary>Where Explorer's own tray shows this icon, as last read; it can change at any time.</summary>
        ExplorerPlacement ExplorerPlacement { get; }

        /// <summary>Screen rectangle of the icon in physical pixels, for Shell_NotifyIconGetRect.</summary>
        void SetScreenBounds(PixelRect bounds);

        /// <summary>The pointer arrived on the icon: its app hears of it as from Explorer's tray, popup included.</summary>
        void OnMouseEnter();

        /// <summary>The pointer left the icon, or the icon is no longer on screen.</summary>
        void OnMouseLeave();
        void OnMouseMove();
        void OnMouseDown(MouseButton button);
        void OnMouseUp(MouseButton button);
    }

    /// <summary>Where Explorer's own tray shows an icon.</summary>
    public enum ExplorerPlacement
    {
        /// <summary>Not known: Explorer's tray could not be read, or it has no such icon.</summary>
        Unknown,

        /// <summary>On the taskbar.</summary>
        Taskbar,

        /// <summary>In the hidden-icons flyout.</summary>
        Overflow,

        /// <summary>
        /// Nowhere, although the app has not hidden it: Windows hides it, as it does Meet Now while that
        /// is turned off in "Turn system icons on or off".
        /// </summary>
        Hidden
    }

    /// <summary>Same values as ABE_LEFT..ABE_BOTTOM.</summary>
    public enum ScreenEdge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3
    }

    public sealed class TrayBalloonEventArgs : EventArgs
    {
        public TrayBalloonEventArgs(ITrayIcon? icon, string title, string text)
        {
            Icon = icon;
            Title = title;
            Text = text;
        }

        public ITrayIcon? Icon { get; }
        public string Title { get; }
        public string Text { get; }
    }
}
