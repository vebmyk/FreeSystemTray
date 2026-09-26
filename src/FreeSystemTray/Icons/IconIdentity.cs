using System;
using System.Globalization;
using System.IO;

namespace FreeSystemTray.Icons
{
    /// <summary>
    /// Who an icon is, as Shell_NotifyIcon identifies it: a GUID, or the app's window and a number
    /// (uID). Rules and the saved order name icons by <see cref="Key"/>.
    /// </summary>
    /// <param name="Id">The host's id: the GUID, or "exe path:uID".</param>
    /// <param name="ExePath">The app's executable, if known.</param>
    public sealed record IconIdentity(string Id, Guid Guid, string? ExePath, uint Uid)
    {
        /// <summary>
        /// The name saved in the settings: the GUID if the app gave one, else "exe file name:uID".
        /// Not the full path, which changes with every update of apps that install into versioned
        /// folders (app-1.0.9258\Discord.exe).
        /// </summary>
        public string Key => Guid != Guid.Empty ? Guid.ToString()
            : ExePath is { Length: > 0 } path ? $"{Path.GetFileName(path)}:{Uid.ToString(CultureInfo.InvariantCulture)}"
            : Id;

        /// <summary>The executable's file name, or null.</summary>
        public string? ExeName => string.IsNullOrEmpty(ExePath) ? null : Path.GetFileName(ExePath);

        /// <summary>
        /// Whether a rule's <see cref="IconRule.Match"/> names this icon, and how exactly: 3 for this
        /// one icon (its GUID, key or id, or an exe with ":uID"), 2 for its exe's full path, 1 for its
        /// exe's file name, 0 for another icon. Case does not matter.
        /// </summary>
        public int MatchStrength(string? match)
        {
            string m = match?.Trim() ?? "";
            if (m.Length == 0) return 0;
            if (Guid.TryParse(m, out Guid g)) return Guid != Guid.Empty && g == Guid ? 3 : 0;
            if (Same(m, Id) || Same(m, Key)) return 3;

            // "exe:uID" or "full path:uID"; a drive letter's colon ("C:\...") is not one.
            int colon = m.LastIndexOf(':');
            if (colon > 1 && uint.TryParse(m.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out uint uid))
                return uid == Uid && MatchesExe(m[..colon]) > 0 ? 3 : 0;
            return MatchesExe(m);
        }

        private int MatchesExe(string m)
        {
            if (string.IsNullOrEmpty(ExePath)) return 0;
            if (m.Contains('\\') || m.Contains('/')) return Same(m.Replace('/', '\\'), ExePath) ? 2 : 0;
            return Same(m, Path.GetFileName(ExePath)) ? 1 : 0;
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
