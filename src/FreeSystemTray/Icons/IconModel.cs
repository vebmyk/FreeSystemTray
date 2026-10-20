using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.Icons
{
    /// <summary>One tray icon as the tray shows it: the app's icon with the rules that apply to it.</summary>
    public sealed class IconItem : INotifyPropertyChanged
    {
        internal IconItem(ITrayIcon icon)
        {
            Icon = icon;
            Identity = new IconIdentity(icon.Id, icon.Guid, icon.OwnerPath, icon.Uid);
            icon.PropertyChanged += Icon_PropertyChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ITrayIcon Icon { get; }
        public IconIdentity Identity { get; }

        /// <summary>The name rules and the saved order use for it.</summary>
        public string Key => Identity.Key;

        public string Id => Icon.Id;
        public string? OwnerPath => Icon.OwnerPath;
        public ImageSource? Image => Icon.Image;

        /// <summary>
        /// The tooltip shown: a rule's replacement, or the app's if it wants the standard tooltip (not a
        /// version 4 icon that shows its own popup, <see cref="ITrayIcon.ShowsToolTip"/>). Null for none.
        /// </summary>
        public string? ToolTip => Rule.Tooltip != null || Icon.ShowsToolTip ? Rule.ToolTipFor(Icon.ToolTip) : null;

        /// <summary>What to call the icon: its label, else the first line of the app's tooltip, else its exe.</summary>
        public string Name => Rule.Label is { Length: > 0 } label ? label
            : FirstLine(Icon.ToolTip) ?? Identity.ExeName ?? Key;

        public EffectiveRule Rule { get; private set; } = EffectiveRule.None;
        public IconPlacement Placement { get; private set; }

        /// <summary>In the hidden-icons flyout only because the tray is full.</summary>
        public bool Spilled { get; private set; }

        /// <summary>Being dragged to a new place.</summary>
        public bool IsDragged
        {
            get => _isDragged;
            internal set
            {
                if (_isDragged == value) return;
                _isDragged = value;
                Raise(nameof(IsDragged));
            }
        }

        private bool _isDragged;

        internal void Update(ArrangedIcon arranged)
        {
            bool ruleChanged = !Equals(Rule, arranged.Rule);
            bool placementChanged = Placement != arranged.Placement || Spilled != arranged.Spilled;
            Rule = arranged.Rule;
            Placement = arranged.Placement;
            Spilled = arranged.Spilled;
            if (ruleChanged)
            {
                Raise(nameof(Rule));
                Raise(nameof(ToolTip));
                Raise(nameof(Name));
            }
            if (placementChanged)
            {
                Raise(nameof(Placement));
                Raise(nameof(Spilled));
            }
        }

        internal void Detach() => Icon.PropertyChanged -= Icon_PropertyChanged;

        private void Icon_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ITrayIcon.Image):
                    Raise(nameof(Image));
                    break;
                case nameof(ITrayIcon.ToolTip):
                    Raise(nameof(ToolTip));
                    Raise(nameof(Name));
                    break;
                case nameof(ITrayIcon.ShowsToolTip):
                    Raise(nameof(ToolTip));
                    break;
            }
        }

        private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        private static string? FirstLine(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int end = text.IndexOfAny(new[] { '\r', '\n' });
            string line = (end < 0 ? text : text[..end]).Trim();
            return line.Length > 0 ? line : null;
        }

        public override string ToString() => $"{Name} ({Key})";
    }

    /// <summary>
    /// The tray's icons, arranged by the icon settings: <see cref="Tray"/> for the tray window,
    /// <see cref="Overflow"/> for the hidden-icons flyout, and <see cref="All"/> including the hidden
    /// ones. Rearranged when the host's icons or the settings change. UI thread only.
    /// </summary>
    internal sealed class IconModel
    {
        private readonly ITrayHost _host;
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<ITrayIcon, IconItem> _items = new(ReferenceEqualityComparer.Instance);
        private IconSettings _settings;
        private IconSettings? _preview;
        private int _capacity;
        private List<IconItem> _all = new();
        private bool _queued;

        /// <param name="capacity">The tray's cells (<see cref="IconGrid.Capacity"/>), 0 for no limit.</param>
        public IconModel(ITrayHost host, IconSettings settings, Dispatcher dispatcher, int capacity = 0)
        {
            _host = host;
            _settings = settings;
            _capacity = capacity;
            _dispatcher = dispatcher;
            // The host changes its collection one icon at a time; one arrangement covers them all.
            ((INotifyCollectionChanged)host.Icons).CollectionChanged += (_, _) => QueueArrange();
            Arrange();
        }

        public ObservableCollection<IconItem> Tray { get; } = new();
        public ObservableCollection<IconItem> Overflow { get; } = new();

        /// <summary>Every icon in display order: the tray's, the flyout's, then the hidden ones.</summary>
        public IReadOnlyList<IconItem> All => _all;

        /// <summary>The settings in effect: a drag's preview while one runs.</summary>
        public IconSettings Settings => _preview ?? _settings;

        /// <summary>The saved settings, without a drag's preview.</summary>
        public IconSettings SavedSettings => _settings;

        /// <summary>Raised after every rearrangement.</summary>
        public event EventHandler? Arranged;

        /// <summary>The tray's cells (<see cref="IconGrid.Capacity"/>), 0 for no limit.</summary>
        public int Capacity => _capacity;

        public void ApplySettings(IconSettings settings, int capacity)
        {
            _settings = settings;
            _capacity = capacity;
            _preview = null;
            Arrange();
        }

        /// <summary>Arranges the icons by <paramref name="draft"/> until <see cref="EndPreview"/>, without saving it (while dragging).</summary>
        public void Preview(IconSettings draft)
        {
            _preview = draft;
            Arrange();
        }

        public void EndPreview()
        {
            if (_preview == null) return;
            _preview = null;
            Arrange();
        }

        /// <summary>The item for a host icon, if it is still there.</summary>
        public IconItem? ItemFor(ITrayIcon icon) => _items.TryGetValue(icon, out IconItem? item) ? item : null;

        /// <summary>
        /// Arranges soon rather than now: the icon's state can change while ManagedShell handles a tray
        /// message, and a burst of changes needs only one arrangement.
        /// </summary>
        private void QueueArrange()
        {
            if (_queued) return;
            _queued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, Arrange);
        }

        public void Arrange()
        {
            _queued = false;
            IReadOnlyList<ITrayIcon> icons = _host.Icons;

            foreach (ITrayIcon gone in _items.Keys.Where(i => !icons.Contains(i)).ToList())
            {
                _items[gone].Detach();
                gone.PropertyChanged -= Icon_PropertyChanged;
                _items.Remove(gone);
            }
            var items = new List<IconItem>(icons.Count);
            var facts = new List<IconFacts>(icons.Count);
            foreach (ITrayIcon icon in icons)
            {
                if (!_items.TryGetValue(icon, out IconItem? item))
                {
                    item = new IconItem(icon);
                    _items.Add(icon, item);
                    icon.PropertyChanged += Icon_PropertyChanged;
                }
                items.Add(item);
                facts.Add(new IconFacts(item.Identity, icon.IsHidden, icon.ExplorerPlacement));
            }

            IReadOnlyList<ArrangedIcon> arranged = IconArrangement.Arrange(facts, Settings, _capacity);
            _all = arranged.Select(a => items[a.Index]).ToList();
            foreach (ArrangedIcon a in arranged) items[a.Index].Update(a);
            Sync(Tray, _all.Where(i => i.Placement == IconPlacement.Tray).ToList());
            Sync(Overflow, _all.Where(i => i.Placement == IconPlacement.Overflow).ToList());
            Arranged?.Invoke(this, EventArgs.Empty);
        }

        private void Icon_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ITrayIcon.IsHidden) or nameof(ITrayIcon.ExplorerPlacement)) QueueArrange();
        }

        /// <summary>Brings <paramref name="target"/> to <paramref name="wanted"/> with as few changes as possible.</summary>
        private static void Sync(ObservableCollection<IconItem> target, List<IconItem> wanted)
        {
            var keep = new HashSet<IconItem>(wanted);
            for (int i = target.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(target[i])) target.RemoveAt(i);
            }
            for (int i = 0; i < wanted.Count; i++)
            {
                int at = target.IndexOf(wanted[i]);
                if (at == i) continue;
                if (at >= 0) target.Move(at, i);
                else target.Insert(i, wanted[i]);
            }
        }
    }
}
