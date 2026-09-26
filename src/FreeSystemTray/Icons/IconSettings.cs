using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FreeSystemTray.Icons
{
    /// <summary>Where an icon is shown.</summary>
    public enum IconPlacement
    {
        /// <summary>In the tray window.</summary>
        Tray,

        /// <summary>In the hidden-icons flyout, opened with the tray's chevron.</summary>
        Overflow,

        /// <summary>Nowhere (the app keeps running, and its icon stays in Explorer's tray).</summary>
        Hidden
    }

    /// <summary>Where icons go that no rule places.</summary>
    public enum DefaultPlacement
    {
        Tray,
        Overflow,

        /// <summary>As in Explorer's tray: its taskbar icons in the tray, its hidden icons in the flyout.</summary>
        Windows
    }

    /// <summary>Which icons are shown where, in what order, and what they are called.</summary>
    public sealed class IconSettings
    {
        /// <summary>Where icons without a rule go.</summary>
        public DefaultPlacement Default { get; set; } = DefaultPlacement.Tray;

        /// <summary>
        /// Hide icons Explorer registers but does not show, such as Meet Now while it is turned off in
        /// "Turn system icons on or off". A rule that places the icon wins.
        /// </summary>
        public bool HideWindowsHidden { get; set; } = true;

        /// <summary>At most this many icons in the tray; the rest go to the hidden-icons flyout. 0: no limit.</summary>
        public int MaxTrayIcons { get; set; }

        /// <summary>Per-icon rules. For each value, the rule that names the icon most exactly wins (see <see cref="IconIdentity.MatchStrength"/>).</summary>
        public List<IconRule> Rules { get; set; } = new();

        /// <summary>
        /// The order of icons, by key (<see cref="IconIdentity.Key"/>), kept for icons whose app is not
        /// running too. Icons not in it follow, in the order they arrived.
        /// </summary>
        public List<string> Order { get; set; } = new();

        public IconSettings Clone() => new()
        {
            Default = Default,
            HideWindowsHidden = HideWindowsHidden,
            MaxTrayIcons = MaxTrayIcons,
            Rules = Rules.ConvertAll(r => r.Clone()),
            Order = new List<string>(Order)
        };

        /// <summary>
        /// Changes the rule that names exactly <paramref name="key"/> (<see cref="IconIdentity.Key"/>),
        /// adding it if there is none and removing it once it sets nothing.
        /// </summary>
        public void EditRule(string key, System.Action<IconRule> edit)
        {
            IconRule? rule = Rules.Find(r => string.Equals(r.Match.Trim(), key, System.StringComparison.OrdinalIgnoreCase));
            if (rule == null)
            {
                rule = new IconRule { Match = key };
                Rules.Add(rule);
            }
            edit(rule);
            if (rule.IsEmpty) Rules.Remove(rule);
        }
    }

    /// <summary>What to do with the icons a rule names. Unset values (null) leave the choice to other rules or the defaults.</summary>
    public sealed class IconRule
    {
        /// <summary>
        /// Which icons: a GUID, an exe file name ("Discord.exe", all its icons), a full path, or either
        /// of these with ":uID" for one of the exe's icons ("PowerToys.exe:1").
        /// </summary>
        public string Match { get; set; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IconPlacement? Placement { get; set; }

        /// <summary>The icon's name in the settings window and menus.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Label { get; set; }

        /// <summary>Replaces the icon's tooltip; "{tip}" stands for the app's own tooltip. "" shows none.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Tooltip { get; set; }

        /// <summary>Whether the rule still does anything.</summary>
        [JsonIgnore]
        public bool IsEmpty => Placement == null && Label == null && Tooltip == null;

        public IconRule Clone() => new() { Match = Match, Placement = Placement, Label = Label, Tooltip = Tooltip };
    }
}
