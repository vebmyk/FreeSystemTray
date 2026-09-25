using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TraySpike.Tray
{
    /// <summary>
    /// The only place that talks to the shell. The UI binds to <see cref="Icons"/> and routes
    /// mouse input back through <see cref="ITrayIcon"/>. Everything is raised on the UI thread.
    /// </summary>
    public interface ITrayHost : IDisposable
    {
        ReadOnlyObservableCollection<ITrayIcon> Icons { get; }

        /// <summary>Take over the Shell_TrayWnd role, import existing icons and broadcast TaskbarCreated.</summary>
        void Start();

        /// <summary>
        /// Where the tray window is on screen, in physical pixels. Answers ABM_GETTASKBARPOS and
        /// other "where is the tray" queries from apps.
        /// </summary>
        void SetHostBounds(Rect screenPx, ScreenEdge edge);

        event EventHandler<TrayBalloonEventArgs>? BalloonReceived;
    }

    public interface ITrayIcon : INotifyPropertyChanged
    {
        /// <summary>Stable identity: GUID if the app supplied one, else "exe path:uID".</summary>
        string Id { get; }
        ImageSource? Image { get; }
        string? ToolTip { get; }
        bool IsHidden { get; }
        string? OwnerPath { get; }

        /// <summary>Screen rectangle of the icon in physical pixels, for Shell_NotifyIconGetRect.</summary>
        void SetScreenBounds(Rect screenPx);

        void OnMouseEnter();
        void OnMouseLeave();
        void OnMouseMove();
        void OnMouseDown(MouseButton button);
        void OnMouseUp(MouseButton button);
    }

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
