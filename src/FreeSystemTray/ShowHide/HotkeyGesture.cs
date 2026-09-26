using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace FreeSystemTray.ShowHide
{
    /// <summary>Same values as RegisterHotKey's MOD_ALT, MOD_CONTROL, MOD_SHIFT and MOD_WIN.</summary>
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 0x1,
        Ctrl = 0x2,
        Shift = 0x4,
        Win = 0x8
    }

    /// <summary>A global hotkey written like "Ctrl+Alt+Shift+T": modifiers, then one key.</summary>
    public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey, string KeyName)
    {
        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Esc"] = nameof(Key.Escape),
            ["Del"] = nameof(Key.Delete),
            ["Ins"] = nameof(Key.Insert),
            ["PgUp"] = nameof(Key.PageUp),
            ["PgDn"] = nameof(Key.PageDown),
            ["Backspace"] = nameof(Key.Back),
            ["Break"] = nameof(Key.Pause),
            ["ScrollLock"] = nameof(Key.Scroll)
        };

        /// <summary>
        /// Parses a gesture. Returns null for an empty string or "none" (no hotkey).
        /// </summary>
        /// <exception cref="FormatException">Not a valid gesture, or one that would take over a key used for typing.</exception>
        public static HotkeyGesture? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
            string[] parts = text.Split('+');
            var modifiers = HotkeyModifiers.None;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                modifiers |= parts[i].Trim().ToLowerInvariant() switch
                {
                    "ctrl" or "control" => HotkeyModifiers.Ctrl,
                    "alt" => HotkeyModifiers.Alt,
                    "shift" => HotkeyModifiers.Shift,
                    "win" or "windows" => HotkeyModifiers.Win,
                    _ => throw new FormatException($"'{parts[i].Trim()}' in hotkey '{text}' is not Ctrl, Alt, Shift or Win")
                };
            }

            string keyText = parts[^1].Trim();
            if (!TryParseKey(keyText, out Key key, out string name))
                throw new FormatException($"'{keyText}' in hotkey '{text}' is not a key name");
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk == 0) throw new FormatException($"'{keyText}' in hotkey '{text}' has no virtual-key code");
            if ((modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0 && !MayStandAlone(vk))
                throw new FormatException($"Hotkey '{text}' needs Ctrl, Alt or Win: on its own, {name} is needed for typing");
            return new HotkeyGesture(modifiers, vk, name);
        }

        /// <summary>
        /// How a pressed key chord is written ("Ctrl+Alt+T"), for <see cref="Parse"/>. Null while only
        /// modifiers are down.
        /// </summary>
        public static string? Format(ModifierKeys modifiers, Key key)
        {
            if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift
                or Key.RightShift or Key.LWin or Key.RWin or Key.System) return null;
            var parts = new List<string>(5);
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString()
                : PreferredNames.TryGetValue(key, out string? name) ? name : key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>Readable names for keys that have several (ToString would give Oem3 for OemTilde, Next for PageDown).</summary>
        private static readonly Dictionary<Key, string> PreferredNames = new()
        {
            [Key.Enter] = nameof(Key.Enter), [Key.PageUp] = nameof(Key.PageUp), [Key.PageDown] = nameof(Key.PageDown),
            [Key.CapsLock] = nameof(Key.CapsLock), [Key.PrintScreen] = nameof(Key.PrintScreen),
            [Key.OemSemicolon] = nameof(Key.OemSemicolon), [Key.OemQuestion] = nameof(Key.OemQuestion), [Key.OemTilde] = nameof(Key.OemTilde),
            [Key.OemOpenBrackets] = nameof(Key.OemOpenBrackets), [Key.OemPipe] = nameof(Key.OemPipe),
            [Key.OemCloseBrackets] = nameof(Key.OemCloseBrackets), [Key.OemQuotes] = nameof(Key.OemQuotes), [Key.OemBackslash] = nameof(Key.OemBackslash)
        };

        public override string ToString()
        {
            var parts = new List<string>(5);
            if ((Modifiers & HotkeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
            if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & HotkeyModifiers.Win) != 0) parts.Add("Win");
            parts.Add(KeyName);
            return string.Join("+", parts);
        }

        private static bool TryParseKey(string text, out Key key, out string name)
        {
            key = Key.None;
            name = text;
            if (text.Length == 0) return false;
            if (text.Length == 1 && char.IsAsciiDigit(text[0]))
            {
                key = Key.D0 + (text[0] - '0');
                return true;
            }
            if (text.Length == 1 && char.IsAsciiLetter(text[0]))
            {
                key = Key.A + (char.ToUpperInvariant(text[0]) - 'A');
                name = text.ToUpperInvariant();
                return true;
            }
            if (Aliases.TryGetValue(text, out string? alias)) text = alias;
            // The defined name, not Enum.TryParse: that also takes numbers ("42" would be a Key value),
            // and Key has several names per value (PageDown and Next), so ToString could print another.
            string? defined = Array.Find(Enum.GetNames<Key>(), n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (defined == null || defined == nameof(Key.None)) return false;
            key = Enum.Parse<Key>(defined);
            name = defined;
            return true;
        }

        /// <summary>Keys that do not type anything, so they may be a hotkey without Ctrl, Alt or Win.</summary>
        private static bool MayStandAlone(int vk) =>
            vk is >= 0x70 and <= 0x87   // F1..F24
            or 0x13 or 0x91             // Pause, Scroll Lock
            or >= 0xA6 and <= 0xB7;     // browser, volume, media and launch keys
    }
}
