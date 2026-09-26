using System;
using System.IO;
using Microsoft.Win32;

namespace FreeSystemTray
{
    /// <summary>
    /// Starting FreeSystemTray when the user signs in: a value in the user's Run key, the place Task
    /// Manager's Startup tab lists. Task Manager turns an entry off without removing it, in the
    /// StartupApproved key (the first byte of the value is odd while it is off), so both are read,
    /// and turning autostart on here also turns it back on there. The registry is the only record:
    /// settings.json does not store it.
    /// </summary>
    internal sealed class Autostart
    {
        public const string ValueName = "FreeSystemTray";
        public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string DefaultApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>What Task Manager writes for an entry that is on.</summary>
        private static readonly byte[] Approved = { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        private readonly string _runKey;
        private readonly string _approvedKey;

        /// <param name="runKey">Under HKEY_CURRENT_USER. Tests use a key of their own.</param>
        public Autostart(string runKey = DefaultRunKey, string approvedKey = DefaultApprovedKey)
        {
            _runKey = runKey;
            _approvedKey = approvedKey;
        }

        /// <summary>The command Windows runs at sign-in, or null.</summary>
        public string? Command
        {
            get
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runKey);
                return key?.GetValue(ValueName) as string;
            }
        }

        /// <summary>Turned off in Task Manager's Startup tab.</summary>
        public bool TurnedOffInTaskManager
        {
            get
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(_approvedKey);
                return key?.GetValue(ValueName) is byte[] { Length: > 0 } state && (state[0] & 1) != 0;
            }
        }

        /// <summary>Whether FreeSystemTray starts at sign-in.</summary>
        public bool IsEnabled => Command != null && !TurnedOffInTaskManager;

        /// <summary>Whether the command starts this exe (an older build elsewhere may be registered).</summary>
        public bool StartsExe(string exePath) =>
            Command is string command && command.Contains(Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase);

        /// <summary>Starts <paramref name="command"/> at sign-in, also if Task Manager had turned it off.</summary>
        public void Enable(string command)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_runKey)) key.SetValue(ValueName, command, RegistryValueKind.String);
            if (!TurnedOffInTaskManager) return;
            using RegistryKey approved = Registry.CurrentUser.CreateSubKey(_approvedKey);
            approved.SetValue(ValueName, Approved, RegistryValueKind.Binary);
        }

        public void Disable()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(_runKey, writable: true)) key?.DeleteValue(ValueName, throwOnMissingValue: false);
            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        /// <summary>
        /// The command for this exe: marked as started at sign-in, and with the settings file if it is
        /// not the default one.
        /// </summary>
        public static string CommandFor(string exePath, string settingsPath)
        {
            string command = $"\"{Path.GetFullPath(exePath)}\" --autostart";
            if (!string.Equals(Path.GetFullPath(settingsPath), Path.GetFullPath(Settings.SettingsStore.DefaultPath), StringComparison.OrdinalIgnoreCase))
                command += $" --settings \"{Path.GetFullPath(settingsPath)}\"";
            return command;
        }
    }
}
