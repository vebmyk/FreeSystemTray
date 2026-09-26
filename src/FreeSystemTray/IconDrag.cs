using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FreeSystemTray.Icons;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray
{
    /// <summary>
    /// Moves an icon while the left button is held, as in Explorer's tray: over another icon it takes
    /// that icon's place, over the chevron it goes into the hidden-icons flyout (which opens), and from
    /// the flyout it can go back into the tray. The icons move as the pointer does, by previewing the
    /// settings the drop would save (<see cref="IconModel.Preview"/>); the drop saves them. The window
    /// the drag started in captures the mouse, so the pointer can cross over to the other window.
    /// </summary>
    internal sealed class IconDrag
    {
        private readonly TrayWindow _tray;
        private readonly OverflowWindow _overflow;
        private readonly IconModel _model;
        private readonly Window _source;
        private readonly IconSettings _original;
        private readonly IconPlacement _startPlacement;
        private readonly bool _overflowWasOpen;
        private (IconPlacement Group, int Index)? _target;
        private bool _ended;

        public IconDrag(TrayWindow tray, OverflowWindow overflow, IconModel model, Window source, IconItem item)
        {
            _tray = tray;
            _overflow = overflow;
            _model = model;
            _source = source;
            Item = item;
            _original = model.Settings.Clone();
            _startPlacement = item.Placement;
            _overflowWasOpen = overflow.IsOpen;
        }

        public IconItem Item { get; }

        /// <summary>Called with the settings to save when the icon was dropped somewhere new, and null when nothing changed.</summary>
        public event Action<IconDrag, IconSettings?>? Ended;

        public void Start()
        {
            Item.IsDragged = true;
            _source.MouseMove += Source_MouseMove;
            _source.MouseLeftButtonUp += Source_MouseLeftButtonUp;
            _source.LostMouseCapture += Source_LostMouseCapture;
            if (!_source.CaptureMouse()) End(commit: false);
        }

        private void Source_MouseMove(object sender, MouseEventArgs e)
        {
            e.Handled = true;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                End(commit: true); // released outside any window of ours
                return;
            }
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT p)) MoveTo(new PixelPoint(p.X, p.Y));
        }

        private void Source_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            End(commit: true);
        }

        private void Source_LostMouseCapture(object sender, MouseEventArgs e) => End(commit: false);

        /// <summary>Puts the icon where the pointer is, if that is a place for it.</summary>
        private void MoveTo(PixelPoint cursor)
        {
            (IconPlacement Group, int Index)? target = null;
            if (_overflow.IsOpen && _overflow.ScreenBounds.Contains(cursor))
            {
                IconItem? over = _model.Overflow.FirstOrDefault(i => _overflow.CellOf(i) is { IsVisible: true } c && c.ScreenBounds().Contains(cursor));
                target = over != null ? IndexAt(IconPlacement.Overflow, over)
                    : _model.Overflow.All(i => i == Item) ? (IconPlacement.Overflow, 0) : null;
            }
            else if (_tray.ScreenBounds.Contains(cursor))
            {
                if (_tray.ChevronBounds() is { IsEmpty: false } chevron && chevron.Contains(cursor))
                {
                    target = (IconPlacement.Overflow, _model.Overflow.Count(i => i != Item));
                    if (!_overflow.IsOpen) _tray.OpenOverflow("dragging an icon onto the chevron");
                }
                else if (_model.Tray.FirstOrDefault(i => _tray.CellOf(i) is { IsVisible: true } c && c.ScreenBounds().Contains(cursor)) is { } over)
                {
                    target = IndexAt(IconPlacement.Tray, over);
                }
                else if (_model.Tray.All(i => i == Item))
                {
                    target = (IconPlacement.Tray, 0);
                }
            }
            if (target == null || target == _target) return;
            _target = target;
            Preview(target.Value.Group, target.Value.Index);
        }

        /// <summary>
        /// The place the icon takes when the pointer is over <paramref name="over"/>: that icon's, which
        /// pushes it one place towards where the dragged icon came from.
        /// </summary>
        private (IconPlacement, int)? IndexAt(IconPlacement group, IconItem over)
        {
            if (over == Item) return null;
            IList<IconItem> items = group == IconPlacement.Tray ? _model.Tray : _model.Overflow;
            int at = items.Where(i => i != Item).ToList().IndexOf(over);
            int mine = items.IndexOf(Item), theirs = items.IndexOf(over);
            return (group, mine >= 0 && mine < theirs ? at + 1 : at);
        }

        private void Preview(IconPlacement group, int index)
        {
            IconSettings draft = _original.Clone();
            IList<IconItem> items = group == IconPlacement.Tray ? _model.Tray : _model.Overflow;
            draft.Order = IconOrder.Move(_original.Order, _model.All.Select(i => i.Key).ToList(),
                items.Where(i => i != Item).Select(i => i.Key).ToList(), Item.Key, index);
            // A rule only when the icon changes places: moved within the tray, it stays where the
            // defaults put it.
            if (group != _startPlacement) draft.EditRule(Item.Key, r => r.Placement = group);
            _model.Preview(draft);
        }

        private void End(bool commit)
        {
            if (_ended) return;
            _ended = true;
            _source.MouseMove -= Source_MouseMove;
            _source.MouseLeftButtonUp -= Source_MouseLeftButtonUp;
            _source.LostMouseCapture -= Source_LostMouseCapture;
            if (_source.IsMouseCaptured) _source.ReleaseMouseCapture();
            Item.IsDragged = false;

            bool droppedInFlyout = NativeMethods.GetCursorPos(out NativeMethods.POINT p) && _overflow.ScreenBounds.Contains(new PixelPoint(p.X, p.Y));
            if (_overflow.IsOpen && !_overflowWasOpen && !droppedInFlyout) _overflow.CloseFlyout("icon dropped elsewhere");
            Ended?.Invoke(this, commit && _target != null ? _model.Settings : null);
        }
    }
}
