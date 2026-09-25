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

        /// <summary>Whether the tray window is shown. While it is hidden, Explorer answers position queries.</summary>
        bool TrayWindowVisible { get; set; }

        event EventHandler<TrayBalloonEventArgs>? BalloonReceived;
    }

    public interface ITrayIcon : INotifyPropertyChanged
    {
        /// <summary>Stable identity: the GUID if the app supplied one, else "exe path:uID".</summary>
        string Id { get; }
        ImageSource? Image { get; }
        string? ToolTip { get; }
        bool IsHidden { get; }
        string? OwnerPath { get; }

        /// <summary>Screen rectangle of the icon in physical pixels, for Shell_NotifyIconGetRect.</summary>
        void SetScreenBounds(PixelRect bounds);

        void OnMouseEnter();
        void OnMouseLeave();
        void OnMouseMove();
        void OnMouseDown(MouseButton button);
        void OnMouseUp(MouseButton button);
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
