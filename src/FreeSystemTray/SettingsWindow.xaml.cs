using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FreeSystemTray.Icons;
using FreeSystemTray.Logging;
using FreeSystemTray.Scaling;
using FreeSystemTray.Settings;
using FreeSystemTray.ShowHide;
using FreeSystemTray.Theming;
using FreeSystemTray.TrayHost;
using Microsoft.Win32;

namespace FreeSystemTray
{
    /// <summary>
    /// The settings window. Every change is applied and saved at once through
    /// <see cref="TrayWindow.UpdateSettings"/>; the window reloads when the settings change in any
    /// other way (the tray's menus, a drag, the settings file edited by hand) and when the icons change.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private static readonly string[] IconSizes = { "75%", "100%", "125%", "150%", "167%", "200%", "250%", "300%", "20px", "24px", "32px" };
        private static readonly string[] TraySizes = { "50%", "75%", "100%", "125%", "150%", "200%", "32px", "40px" };

        private readonly TrayWindow _tray;
        private readonly Autostart _autostart;
        private readonly ObservableCollection<IconRow> _rows = new();
        private bool _loading;

        internal SettingsWindow(TrayWindow tray, Autostart autostart)
        {
            _tray = tray;
            _autostart = autostart;
            InitializeComponent();
            IconListView.ItemsSource = _rows;
            IconSizeBox.ItemsSource = IconSizes;
            TraySizeBox.ItemsSource = TraySizes;
            HotspotBox.ItemsSource = Enum.GetValues<HotspotLocation>();
            AnimationBox.ItemsSource = Enum.GetValues<TrayAnimation>();
            VersionText.Text = $"FreeSystemTray {typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]}" +
                               " · built on ManagedShell (Apache-2.0)";

            _tray.SettingsApplied += Tray_SettingsApplied;
            _tray.Icons.Arranged += Icons_Arranged;
            Closed += (_, _) =>
            {
                _tray.SettingsApplied -= Tray_SettingsApplied;
                _tray.Icons.Arranged -= Icons_Arranged;
            };
            Load();
        }

        private AppSettings Settings => _tray.Settings;

        /// <summary>Shows the Icons tab with the icon <paramref name="key"/> selected.</summary>
        internal void SelectIcon(string key)
        {
            Tabs.SelectedItem = IconsTab;
            IconRow? row = _rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
            if (row == null) return;
            IconListView.SelectedItem = row;
            IconListView.ScrollIntoView(row);
        }

        private void Tray_SettingsApplied(object? sender, EventArgs e) => Load();

        private void Icons_Arranged(object? sender, EventArgs e) => LoadRows();

        /// <summary>Shows the settings in use. Text a box is being edited in stays as it is.</summary>
        private void Load()
        {
            _loading = true;
            try
            {
                AppSettings s = Settings;
                SelectByTag(DefaultPlacementBox, s.Icons.Default.ToString());
                HideWindowsHiddenBox.IsChecked = s.Icons.HideWindowsHidden;
                SetText(MaxIconsBox, s.Icons.MaxTrayIcons.ToString(CultureInfo.InvariantCulture));

                List<ThemeInfo> themes = _tray.Themes.List().ToList();
                ThemeBox.ItemsSource = themes;
                ThemeBox.SelectedItem = themes.FirstOrDefault(t => string.Equals(t.Id, _tray.Theme.Id, StringComparison.OrdinalIgnoreCase));
                if (!IconSizeBox.IsKeyboardFocusWithin) IconSizeBox.Text = s.Scaling.IconSize;
                if (!TraySizeBox.IsKeyboardFocusWithin) TraySizeBox.Text = s.Scaling.TraySize;
                SnapToPixelsBox.IsChecked = s.Scaling.SnapToPixels;
                SelectByTag(IconScalingBox, s.Scaling.IconScaling.ToString());

                HotkeyBox.Text = string.IsNullOrWhiteSpace(s.ShowHide.Hotkey) ? "(none)" : s.ShowHide.Hotkey;
                HotkeyStatus.Text = _tray.ShowHide?.HotkeyProblem is string problem ? problem
                    : _tray.ShowHide?.Hotkey is HotkeyGesture hotkey ? $"{hotkey} shows and hides the tray." : "No hotkey.";
                AutoHideBox.IsChecked = s.ShowHide.AutoHide;
                HideOnClickAwayBox.IsChecked = s.ShowHide.HideOnClickAway;
                SetText(HideDelayBox, s.ShowHide.HideDelayMs.ToString(CultureInfo.InvariantCulture));
                HotspotBox.SelectedItem = s.ShowHide.Hotspot;
                SetText(HotspotDelayBox, s.ShowHide.HotspotDelayMs.ToString(CultureInfo.InvariantCulture));
                AnimationBox.SelectedItem = s.ShowHide.Animation;
                SetText(AnimationMsBox, s.ShowHide.AnimationMs.ToString(CultureInfo.InvariantCulture));

                LoadAutostart();
                AppIconBox.IsChecked = s.AppIcon;
                RestartAfterCrashBox.IsChecked = s.RestartAfterCrash;
                LockBox.IsChecked = s.LockPosition;
                SnapBox.IsChecked = s.SnapToEdges;
                FilesText.Text = $"Settings: {_tray.SettingsPath}\nThemes: {_tray.Themes.UserDirectory}\nLogs: {Path.GetDirectoryName(AppLog.FilePath)}";
                LoadRows();
            }
            finally
            {
                _loading = false;
            }
        }

        private static void SetText(TextBox box, string text)
        {
            if (!box.IsKeyboardFocusWithin) box.Text = text;
        }

        private static void SelectByTag(ComboBox box, string tag) =>
            box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

        private static string? SelectedTag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

        /// <summary>Changes the settings (saved and applied at once), unless the window is showing them.</summary>
        private void Change(Action<AppSettings> change)
        {
            if (_loading) return;
            _tray.UpdateSettings(change);
            StatusText.Text = $"Saved at {DateTime.Now:T}";
            StatusText.ToolTip = _tray.SettingsPath;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Enter applies a text box (it moves the focus on, which commits it).</summary>
        private void TextBox_EnterCommits(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ((UIElement)sender).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }

        private static bool TryParseInt(TextBox box, int min, int max, out int value)
        {
            bool ok = int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value >= min && value <= max;
            box.ToolTip = ok ? null : $"A whole number from {min} to {max}";
            box.BorderBrush = ok ? SystemColors.ControlDarkBrush : Brushes.Firebrick;
            return ok;
        }

        #region Icons

        private void DefaultPlacementBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedTag(DefaultPlacementBox) is string tag) Change(s => s.Icons.Default = Enum.Parse<DefaultPlacement>(tag));
        }

        private void HideWindowsHiddenBox_Click(object sender, RoutedEventArgs e) => Change(s => s.Icons.HideWindowsHidden = HideWindowsHiddenBox.IsChecked == true);

        private void MaxIconsBox_Commit(object sender, RoutedEventArgs e)
        {
            if (TryParseInt(MaxIconsBox, 0, 1000, out int max) && max != Settings.Icons.MaxTrayIcons) Change(s => s.Icons.MaxTrayIcons = max);
        }

        /// <summary>
        /// One row per icon, in the tray's order (tray, hidden icons, hidden), then one per rule that
        /// names no running icon exactly (an app that is not running, or a rule for all of an exe's icons).
        /// Rows are updated in place, so a selection or an open list survives.
        /// </summary>
        private void LoadRows()
        {
            IconModel model = _tray.Icons;
            IconSettings settings = Settings.Icons;
            var wanted = new List<IconRow>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IconItem item in model.All)
            {
                keys.Add(item.Key);
                keys.Add(item.Id);
                if (item.Identity.Guid != Guid.Empty) keys.Add(item.Identity.Guid.ToString());
                // Icons can share a key (two copies of an app, same exe and uID): one row, as one rule covers them.
                IconRow row = RowFor(item.Key, wanted);
                if (wanted.Contains(row)) continue;
                row.Update(item.Name, item.Identity.ExeName ?? "", StatusOf(item), item.Image, true, item.Rule);
                wanted.Add(row);
            }
            foreach (IconRule rule in settings.Rules)
            {
                string match = rule.Match.Trim();
                if (match.Length == 0 || keys.Contains(match) || wanted.Any(r => string.Equals(r.Key, match, StringComparison.OrdinalIgnoreCase))) continue;
                int applies = model.All.Count(i => i.Identity.MatchStrength(match) > 0);
                var effective = new EffectiveRule(rule.Placement, rule.Label, rule.Tooltip);
                IconRow row = RowFor(match, wanted);
                row.Update(rule.Label ?? match, "", applies > 0 ? $"rule for {applies} icon{(applies == 1 ? "" : "s")}" : "not running",
                           null, false, effective);
                wanted.Add(row);
            }

            IconRow? selected = IconListView.SelectedItem as IconRow;
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!wanted.Contains(_rows[i])) _rows.RemoveAt(i);
            }
            for (int i = 0; i < wanted.Count; i++)
            {
                int at = _rows.IndexOf(wanted[i]);
                if (at == i) continue;
                if (at >= 0) _rows.Move(at, i);
                else _rows.Insert(i, wanted[i]);
            }
            if (selected != null && _rows.Contains(selected)) IconListView.SelectedItem = selected;
            LoadDetails();
        }

        private IconRow RowFor(string key, List<IconRow> wanted) =>
            wanted.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? _rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)) ?? new IconRow(key, RowChoiceChanged);

        private static string StatusOf(IconItem item)
        {
            if (item.Placement == IconPlacement.Tray) return "in the tray";
            if (item.Placement == IconPlacement.Overflow) return item.Spilled ? "hidden icons (tray full)" : "in the hidden icons";
            if (item.Icon.IsHidden) return "hidden by its app";
            if (item.Rule.Placement == null && item.Icon.ExplorerPlacement == ExplorerPlacement.Hidden) return "hidden (Windows hides it)";
            return "hidden";
        }

        private void RowChoiceChanged(IconRow row)
        {
            if (_loading) return;
            Change(s => s.Icons.EditRule(row.Key, r => r.Placement = row.Choice.Placement));
        }

        private void IconListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadDetails();

        private void LoadDetails()
        {
            var row = IconListView.SelectedItem as IconRow;
            DetailsBox.IsEnabled = row != null;
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                SetText(LabelBox, row?.Label ?? "");
                NoTooltipBox.IsChecked = row?.Tooltip == "";
                TooltipBox.IsEnabled = row?.Tooltip != "";
                SetText(TooltipBox, row?.Tooltip ?? "");
            }
            finally
            {
                _loading = wasLoading;
            }
            IdentityText.Text = row == null ? "" : $"Rules name it \"{row.Key}\"" + (row.IsRunning ? "" : " (no running icon has exactly this name)");
            IconItem? item = row == null ? null : ItemFor(row.Key);
            IList<IconItem>? group = GroupOf(item);
            MoveUpButton.IsEnabled = group != null && group.IndexOf(item!) > 0;
            MoveDownButton.IsEnabled = group != null && group.IndexOf(item!) < group.Count - 1;
            ForgetButton.IsEnabled = row != null;
        }

        private IconItem? ItemFor(string key) =>
            _tray.Icons.All.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

        private IList<IconItem>? GroupOf(IconItem? item) => item?.Placement switch
        {
            IconPlacement.Tray => _tray.Icons.Tray,
            IconPlacement.Overflow => _tray.Icons.Overflow,
            _ => null
        };

        private void LabelBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_loading || IconListView.SelectedItem is not IconRow row) return;
            string? label = string.IsNullOrWhiteSpace(LabelBox.Text) ? null : LabelBox.Text.Trim();
            if (label != row.Label) Change(s => s.Icons.EditRule(row.Key, r => r.Label = label));
        }

        private void TooltipBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_loading || IconListView.SelectedItem is not IconRow row || NoTooltipBox.IsChecked == true) return;
            string? tooltip = string.IsNullOrEmpty(TooltipBox.Text) ? null : TooltipBox.Text;
            if (tooltip != row.Tooltip) Change(s => s.Icons.EditRule(row.Key, r => r.Tooltip = tooltip));
        }

        private void NoTooltipBox_Click(object sender, RoutedEventArgs e)
        {
            if (IconListView.SelectedItem is not IconRow row) return;
            bool none = NoTooltipBox.IsChecked == true;
            Change(s => s.Icons.EditRule(row.Key, r => r.Tooltip = none ? "" : null));
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

        private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

        private void MoveSelected(int by)
        {
            if (IconListView.SelectedItem is not IconRow row || ItemFor(row.Key) is not { } item || GroupOf(item) is not { } group) return;
            int index = Math.Clamp(group.IndexOf(item) + by, 0, group.Count - 1);
            List<string> order = IconOrder.Move(Settings.Icons.Order, _tray.Icons.All.Select(i => i.Key).ToList(),
                group.Where(i => i != item).Select(i => i.Key).ToList(), item.Key, index);
            Change(s => s.Icons.Order = order);
        }

        private void Forget_Click(object sender, RoutedEventArgs e)
        {
            if (IconListView.SelectedItem is not IconRow row) return;
            string key = row.Key;
            Change(s =>
            {
                s.Icons.Rules.RemoveAll(r => string.Equals(r.Match.Trim(), key, StringComparison.OrdinalIgnoreCase));
                s.Icons.Order.RemoveAll(k => string.Equals(k.Trim(), key, StringComparison.OrdinalIgnoreCase));
            });
        }

        #endregion

        #region Appearance

        private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeBox.SelectedItem is ThemeInfo theme && !string.Equals(theme.Id, Settings.Theme, StringComparison.OrdinalIgnoreCase))
                Change(s => s.Theme = theme.Id);
        }

        private void OpenThemes_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_tray.Themes.UserDirectory);
            OpenInExplorer(_tray.Themes.UserDirectory);
        }

        private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // A preset picked from the list: apply it (typed text applies on Enter or leaving the box).
            if (sender is ComboBox box && box.SelectedItem is string text) CommitSize(box, text);
        }

        private void SizeBox_Commit(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox box) CommitSize(box, box.Text);
        }

        private void CommitSize(ComboBox box, string text)
        {
            if (_loading) return;
            try
            {
                string value = SizeValue.Parse(text).ToString();
                AppearanceProblem.Text = "";
                if (box == IconSizeBox && value != Settings.Scaling.IconSize) Change(s => s.Scaling.IconSize = value);
                if (box == TraySizeBox && value != Settings.Scaling.TraySize) Change(s => s.Scaling.TraySize = value);
            }
            catch (FormatException ex)
            {
                AppearanceProblem.Text = ex.Message;
            }
        }

        private void SnapToPixelsBox_Click(object sender, RoutedEventArgs e) => Change(s => s.Scaling.SnapToPixels = SnapToPixelsBox.IsChecked == true);

        private void IconScalingBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SelectedTag(IconScalingBox) is string tag) Change(s => s.Scaling.IconScaling = Enum.Parse<IconScaling>(tag));
        }

        #endregion

        #region Show and hide

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            // Tab and Esc without modifiers keep their usual meaning (next field, close).
            if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Tab or Key.Escape) return;
            e.Handled = true;
            if (HotkeyGesture.Format(Keyboard.Modifiers, key) is not string text) return;
            try
            {
                HotkeyGesture.Parse(text);
            }
            catch (FormatException ex)
            {
                HotkeyStatus.Text = ex.Message;
                return;
            }
            Change(s => s.ShowHide.Hotkey = text);
        }

        private void ClearHotkey_Click(object sender, RoutedEventArgs e) => Change(s => s.ShowHide.Hotkey = "");

        private void AutoHideBox_Click(object sender, RoutedEventArgs e) => Change(s => s.ShowHide.AutoHide = AutoHideBox.IsChecked == true);

        private void HideOnClickAwayBox_Click(object sender, RoutedEventArgs e) => Change(s => s.ShowHide.HideOnClickAway = HideOnClickAwayBox.IsChecked == true);

        private void HideDelayBox_Commit(object sender, RoutedEventArgs e)
        {
            if (TryParseInt(HideDelayBox, 0, 600_000, out int ms) && ms != Settings.ShowHide.HideDelayMs) Change(s => s.ShowHide.HideDelayMs = ms);
        }

        private void HotspotBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HotspotBox.SelectedItem is HotspotLocation h && h != Settings.ShowHide.Hotspot) Change(s => s.ShowHide.Hotspot = h);
        }

        private void HotspotDelayBox_Commit(object sender, RoutedEventArgs e)
        {
            if (TryParseInt(HotspotDelayBox, 0, 10_000, out int ms) && ms != Settings.ShowHide.HotspotDelayMs) Change(s => s.ShowHide.HotspotDelayMs = ms);
        }

        private void AnimationBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AnimationBox.SelectedItem is TrayAnimation a && a != Settings.ShowHide.Animation) Change(s => s.ShowHide.Animation = a);
        }

        private void AnimationMsBox_Commit(object sender, RoutedEventArgs e)
        {
            if (TryParseInt(AnimationMsBox, 0, 2000, out int ms) && ms != Settings.ShowHide.AnimationMs) Change(s => s.ShowHide.AnimationMs = ms);
        }

        #endregion

        #region General

        private void LoadAutostart()
        {
            try
            {
                AutostartBox.IsChecked = _autostart.IsEnabled;
                string exe = Environment.ProcessPath ?? "";
                AutostartStatus.Text = _autostart.Command is not string command ? ""
                    : _autostart.TurnedOffInTaskManager ? "Turned off in Task Manager's Startup tab."
                    : _autostart.StartsExe(exe) ? "" : $"Starts another copy: {command}";
                AutostartStatus.Visibility = AutostartStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                AutostartStatus.Text = "Cannot read the startup settings: " + ex.Message;
                AutostartStatus.Visibility = Visibility.Visible;
            }
        }

        private void AutostartBox_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (AutostartBox.IsChecked == true) _autostart.Enable(Autostart.CommandFor(Environment.ProcessPath!, _tray.SettingsPath));
                else _autostart.Disable();
                AppLog.Write($"Autostart {(AutostartBox.IsChecked == true ? $"on: {_autostart.Command}" : "off")}");
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                AutostartStatus.Text = "Could not change it: " + ex.Message;
                AutostartStatus.Visibility = Visibility.Visible;
                AppLog.Write("Changing autostart failed: " + ex.Message);
                return;
            }
            LoadAutostart();
        }

        private void AppIconBox_Click(object sender, RoutedEventArgs e) => Change(s => s.AppIcon = AppIconBox.IsChecked == true);

        private void RestartAfterCrashBox_Click(object sender, RoutedEventArgs e) => Change(s => s.RestartAfterCrash = RestartAfterCrashBox.IsChecked == true);

        private void LockBox_Click(object sender, RoutedEventArgs e) => Change(s => s.LockPosition = LockBox.IsChecked == true);

        private void SnapBox_Click(object sender, RoutedEventArgs e) => Change(s => s.SnapToEdges = SnapBox.IsChecked == true);

        private void OpenSettingsFile_Click(object sender, RoutedEventArgs e)
        {
            string path = _tray.SettingsPath;
            if (!File.Exists(path)) Change(_ => { }); // writes the file
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Win32Exception)
            {
                // No program is set to open .json files.
                Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path } });
            }
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Path.GetDirectoryName(AppLog.FilePath) is string dir) OpenInExplorer(dir);
        }

        private static void OpenInExplorer(string folder) =>
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } });

        #endregion
    }

    /// <summary>A rule's placement as a choice in the settings window's list.</summary>
    public sealed record PlacementChoice(IconPlacement? Placement, string Text)
    {
        public static readonly IReadOnlyList<PlacementChoice> All = new[]
        {
            new PlacementChoice(null, "Automatic"),
            new PlacementChoice(IconPlacement.Tray, "Always in the tray"),
            new PlacementChoice(IconPlacement.Overflow, "In the hidden icons"),
            new PlacementChoice(IconPlacement.Hidden, "Hidden")
        };

        public static PlacementChoice For(IconPlacement? placement) => All.First(c => c.Placement == placement);
    }

    /// <summary>One row of the settings window's icon list: an icon, or a rule for icons that are not running.</summary>
    public sealed class IconRow : INotifyPropertyChanged
    {
        private readonly Action<IconRow> _choiceChanged;
        private PlacementChoice _choice = PlacementChoice.All[0];

        internal IconRow(string key, Action<IconRow> choiceChanged)
        {
            Key = key;
            _choiceChanged = choiceChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>What rules name it: the icon's key, or the rule's match.</summary>
        public string Key { get; }

        public string Name { get; private set; } = "";
        public string App { get; private set; } = "";
        public string Status { get; private set; } = "";
        public ImageSource? Image { get; private set; }
        public bool IsRunning { get; private set; }
        public double ImageOpacity => IsRunning ? 1 : 0.4;
        public string? Label { get; private set; }
        public string? Tooltip { get; private set; }
        public IReadOnlyList<PlacementChoice> Choices => PlacementChoice.All;

        public PlacementChoice Choice
        {
            get => _choice;
            set
            {
                if (value == null || value == _choice) return;
                _choice = value;
                Raise(nameof(Choice));
                // After the binding has finished: saving reloads the list.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(() => _choiceChanged(this));
            }
        }

        internal void Update(string name, string app, string status, ImageSource? image, bool running, EffectiveRule rule)
        {
            Name = name;
            App = app;
            Status = status;
            Image = image;
            IsRunning = running;
            Label = rule.Label;
            Tooltip = rule.Tooltip;
            _choice = PlacementChoice.For(rule.Placement);
            foreach (string p in new[] { nameof(Name), nameof(App), nameof(Status), nameof(Image), nameof(IsRunning), nameof(ImageOpacity), nameof(Label), nameof(Tooltip), nameof(Choice) })
                Raise(p);
        }

        private void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
