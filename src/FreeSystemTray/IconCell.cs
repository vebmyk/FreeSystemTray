using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FreeSystemTray.Icons;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray
{
    /// <summary>What an icon's cell needs from the tray around it.</summary>
    internal interface IIconCellHost
    {
        /// <summary>An icon is being dragged: cells leave the pointer to the drag.</summary>
        bool IsDraggingIcon { get; }

        /// <summary>A button went down or up on an icon: menus and flyouts that open next belong to the tray.</summary>
        void NoteIconClick();

        /// <summary>The pointer moved far enough with the left button down: the icon moves instead of being clicked.</summary>
        void BeginIconDrag(IconCell cell, IconItem item);

        /// <summary>Shift+right-click: the tray's own menu for the icon, instead of the app's.</summary>
        void ShowIconMenu(IconCell cell, IconItem item);
    }

    /// <summary>
    /// The cell of one tray icon, in the tray window or its hidden-icons flyout. The pointer's
    /// movements and clicks go to the icon's app, except a left-button drag, which moves the icon,
    /// and Shift+right-click, which opens the tray's menu for it. The app hears of a left click only
    /// when the button is released, as with Explorer, so a drag never reaches it.
    /// </summary>
    public sealed class IconCell : Border
    {
        /// <summary>Set on each window; its cells inherit it.</summary>
        internal static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
            "Host", typeof(IIconCellHost), typeof(IconCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        internal static void SetHost(DependencyObject d, IIconCellHost? value) => d.SetValue(HostProperty, value);

        private IIconCellHost? Host => (IIconCellHost?)GetValue(HostProperty);
        private IconItem? Item => DataContext as IconItem;

        private Point? _leftDownAt;
        private bool _menuGesture;

        /// <summary>The cell's screen rectangle in physical pixels.</summary>
        internal PixelRect ScreenBounds() => TrayWindow.ScreenBoundsOf(this);

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            e.Handled = true;
            if (Item is not { } item || Host?.IsDraggingIcon == true) return;
            // Apps ask where their icon is right after it is clicked; make sure the answer is current.
            item.Icon.SetScreenBounds(ScreenBounds());
            item.Icon.OnMouseEnter();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            e.Handled = true;
            CellState.SetIsPressed(this, false);
            Point? pressed = _leftDownAt;
            _leftDownAt = null;
            if (Item is not { } item || Host is not { } host || host.IsDraggingIcon) return;
            item.Icon.OnMouseLeave();
            // Pressed here and pulled out quickly: that is a drag too.
            if (pressed != null && e.LeftButton == MouseButtonState.Pressed) host.BeginIconDrag(this, item);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            e.Handled = true;
            if (Item is not { } item || Host is not { } host || host.IsDraggingIcon) return;
            if (_leftDownAt is Point start && e.LeftButton == MouseButtonState.Pressed)
            {
                Vector moved = e.GetPosition(this) - start;
                if (Math.Abs(moved.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
                {
                    _leftDownAt = null;
                    CellState.SetIsPressed(this, false);
                    item.Icon.OnMouseLeave(); // the app's tooltip or popup closes, as when the pointer leaves
                    host.BeginIconDrag(this, item);
                    return;
                }
            }
            item.Icon.OnMouseMove();
        }

        // Clicks are noted before the app hears of them: the menu or flyout it opens in response
        // counts as part of the tray for auto-hide and the flyout.
        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            base.OnMouseDown(e);
            e.Handled = true;
            if (Item is not { } item) return;
            Host?.NoteIconClick();
            if (e.ChangedButton == MouseButton.Right && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                _menuGesture = true;
                return;
            }
            _menuGesture = false;
            CellState.SetIsPressed(this, true);
            if (e.ChangedButton == MouseButton.Left) _leftDownAt = e.GetPosition(this);
            item.Icon.OnMouseDown(e.ChangedButton);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            e.Handled = true;
            if (Item is not { } item) return;
            CellState.SetIsPressed(this, false);
            if (e.ChangedButton == MouseButton.Right && _menuGesture)
            {
                _menuGesture = false;
                Host?.ShowIconMenu(this, item);
                return;
            }
            if (Host?.IsDraggingIcon == true) return;
            // Pressed elsewhere (or a drag began and ended): no click.
            if (e.ChangedButton == MouseButton.Left && _leftDownAt == null) return;
            _leftDownAt = null;
            Host?.NoteIconClick();
            item.Icon.OnMouseUp(e.ChangedButton);
        }
    }

    /// <summary>
    /// The chevron that opens the hidden-icons flyout. Shown while the flyout has icons, and during a
    /// drag, so an icon can be dropped on it.
    /// </summary>
    public sealed class ChevronItem : INotifyPropertyChanged
    {
        private bool _isShown;
        private double _angle;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsShown
        {
            get => _isShown;
            set
            {
                if (_isShown == value) return;
                _isShown = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown)));
            }
        }

        /// <summary>Which way the chevron points: towards where the flyout opens (0: up).</summary>
        public double Angle
        {
            get => _angle;
            set
            {
                if (_angle == value) return;
                _angle = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Angle)));
            }
        }

        /// <summary>Points away from the screen edge the tray is nearest to, where the flyout opens.</summary>
        public static double AngleFor(ScreenEdge trayEdge) => trayEdge switch
        {
            ScreenEdge.Top => 180,
            ScreenEdge.Left => 90,
            ScreenEdge.Right => 270,
            _ => 0
        };
    }

    /// <summary>The chevron's cell: a click opens or closes the hidden-icons flyout.</summary>
    public sealed class ChevronCell : Border
    {
        /// <summary>Set on the tray window; the cell inherits it.</summary>
        internal static readonly DependencyProperty ToggleProperty = DependencyProperty.RegisterAttached(
            "Toggle", typeof(Action), typeof(ChevronCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

        internal static void SetToggle(DependencyObject d, Action? value) => d.SetValue(ToggleProperty, value);

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            e.Handled = true;
            CellState.SetIsPressed(this, true);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            e.Handled = true;
            if (!CellState.GetIsPressed(this)) return;
            CellState.SetIsPressed(this, false);
            ((Action?)GetValue(ToggleProperty))?.Invoke();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            CellState.SetIsPressed(this, false);
        }
    }
}
