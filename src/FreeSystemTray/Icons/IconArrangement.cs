using System;
using System.Collections.Generic;
using System.Linq;
using FreeSystemTray.TrayHost;

namespace FreeSystemTray.Icons
{
    /// <summary>What the arrangement needs to know about one icon.</summary>
    /// <param name="AppHidden">The app has hidden the icon itself (NIS_HIDDEN).</param>
    /// <param name="Explorer">Where Explorer's own tray shows it.</param>
    public sealed record IconFacts(IconIdentity Identity, bool AppHidden = false, ExplorerPlacement Explorer = ExplorerPlacement.Unknown);

    /// <summary>The rule values that apply to one icon, merged from every rule that names it.</summary>
    public sealed record EffectiveRule(IconPlacement? Placement, string? Label, string? Tooltip)
    {
        public static readonly EffectiveRule None = new(null, null, null);

        /// <summary>For each value, the one from the rule that names the icon most exactly; among equals, the first.</summary>
        public static EffectiveRule For(IconIdentity icon, IReadOnlyList<IconRule> rules)
        {
            IconPlacement? placement = null;
            string? label = null, tooltip = null;
            int placementStrength = 0, labelStrength = 0, tooltipStrength = 0;
            foreach (IconRule rule in rules)
            {
                int strength = icon.MatchStrength(rule.Match);
                if (strength == 0) continue;
                if (rule.Placement != null && strength > placementStrength) (placement, placementStrength) = (rule.Placement, strength);
                if (rule.Label != null && strength > labelStrength) (label, labelStrength) = (rule.Label, strength);
                if (rule.Tooltip != null && strength > tooltipStrength) (tooltip, tooltipStrength) = (rule.Tooltip, strength);
            }
            return placement == null && label == null && tooltip == null ? None : new EffectiveRule(placement, label, tooltip);
        }

        /// <summary>The tooltip to show: the rule's, with "{tip}" replaced by the app's, or the app's. Null for none.</summary>
        public string? ToolTipFor(string? appToolTip)
        {
            string? tip = Tooltip == null ? appToolTip : Tooltip.Replace("{tip}", appToolTip ?? "", StringComparison.OrdinalIgnoreCase);
            return string.IsNullOrEmpty(tip) ? null : tip;
        }
    }

    /// <param name="Index">The icon's index in the list that was arranged.</param>
    /// <param name="Spilled">In the flyout only because the tray is full (<see cref="IconSettings.MaxTrayIcons"/>).</param>
    public readonly record struct ArrangedIcon(int Index, IconPlacement Placement, EffectiveRule Rule, bool Spilled = false);

    /// <summary>Where each icon goes and in what order. Pure logic: the same icons and settings always give the same result.</summary>
    public static class IconArrangement
    {
        /// <summary>
        /// Every icon, in display order: the tray's first, then the flyout's, then the hidden ones.
        /// Each group follows <see cref="IconSettings.Order"/>; icons not in it come after, in the
        /// order they are listed in <paramref name="icons"/> (the order they arrived).
        /// </summary>
        public static IReadOnlyList<ArrangedIcon> Arrange(IReadOnlyList<IconFacts> icons, IconSettings settings)
        {
            Dictionary<string, int> order = OrderIndex(settings.Order);
            var placed = new List<(ArrangedIcon Icon, int SortKey)>(icons.Count);
            for (int i = 0; i < icons.Count; i++)
            {
                IconFacts f = icons[i];
                EffectiveRule rule = EffectiveRule.For(f.Identity, settings.Rules);
                int sortKey = order.TryGetValue(f.Identity.Key, out int at) ? at : order.Count + i;
                placed.Add((new ArrangedIcon(i, PlacementOf(f, rule, settings), rule), sortKey));
            }

            var tray = placed.Where(p => p.Icon.Placement == IconPlacement.Tray).OrderBy(p => p.SortKey).ToList();
            var spilled = new List<(ArrangedIcon Icon, int SortKey)>();
            int max = settings.MaxTrayIcons;
            if (max > 0 && tray.Count > max)
            {
                // Icons a rule puts in the tray keep their place before those that are there by default.
                var kept = new HashSet<int>(tray.Where(p => p.Icon.Rule.Placement == IconPlacement.Tray).Take(max).Select(p => p.Icon.Index));
                foreach (var p in tray)
                {
                    if (kept.Count >= max) break;
                    kept.Add(p.Icon.Index);
                }
                spilled = tray.Where(p => !kept.Contains(p.Icon.Index))
                              .Select(p => (p.Icon with { Placement = IconPlacement.Overflow, Spilled = true }, p.SortKey)).ToList();
                tray = tray.Where(p => kept.Contains(p.Icon.Index)).ToList();
            }
            var overflow = placed.Where(p => p.Icon.Placement == IconPlacement.Overflow).Concat(spilled).OrderBy(p => p.SortKey);
            var hidden = placed.Where(p => p.Icon.Placement == IconPlacement.Hidden).OrderBy(p => p.SortKey);
            return tray.Concat(overflow).Concat(hidden).Select(p => p.Icon).ToList();
        }

        /// <summary>
        /// Where one icon goes before the tray's capacity is applied. An icon its app has hidden is
        /// hidden; otherwise a rule decides; otherwise an icon Windows hides is hidden (if the
        /// setting says so); otherwise the default does.
        /// </summary>
        public static IconPlacement PlacementOf(IconFacts f, EffectiveRule rule, IconSettings settings)
        {
            if (f.AppHidden) return IconPlacement.Hidden;
            if (rule.Placement is IconPlacement p) return p;
            if (settings.HideWindowsHidden && f.Explorer == ExplorerPlacement.Hidden) return IconPlacement.Hidden;
            return settings.Default switch
            {
                DefaultPlacement.Overflow => IconPlacement.Overflow,
                DefaultPlacement.Windows => f.Explorer == ExplorerPlacement.Overflow ? IconPlacement.Overflow : IconPlacement.Tray,
                _ => IconPlacement.Tray
            };
        }

        private static Dictionary<string, int> OrderIndex(IReadOnlyList<string> order)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in order)
            {
                if (!string.IsNullOrWhiteSpace(key)) index.TryAdd(key.Trim(), index.Count);
            }
            return index;
        }
    }

    /// <summary>Changes to the saved icon order. Pure logic.</summary>
    public static class IconOrder
    {
        /// <summary>
        /// The saved order after the icon <paramref name="key"/> is put at <paramref name="index"/> of
        /// <paramref name="group"/> (the tray's or the flyout's icons as shown, keys in display order;
        /// it may contain <paramref name="key"/> already). Every icon in <paramref name="shown"/> (all
        /// icons, in display order) is written into the order first, so the arrangement stays as it
        /// was apart from the move; icons that are not running keep their place among the others.
        /// </summary>
        public static List<string> Move(IReadOnlyList<string> order, IReadOnlyList<string> shown, IReadOnlyList<string> group, string key, int index)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in order.Concat(shown).Concat(group).Append(key))
            {
                if (!string.IsNullOrWhiteSpace(k) && seen.Add(k.Trim())) result.Add(k.Trim());
            }

            var g = group.Where(k => !Same(k, key)).ToList();
            index = Math.Clamp(index, 0, g.Count);
            g.Insert(index, key);

            result.RemoveAll(k => Same(k, key));
            if (index > 0) result.Insert(result.FindIndex(k => Same(k, g[index - 1])) + 1, key);
            else if (g.Count > 1) result.Insert(result.FindIndex(k => Same(k, g[1])), key);
            else result.Add(key);
            return result;
        }

        private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
