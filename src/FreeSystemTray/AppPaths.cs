using System;
using System.IO;

namespace FreeSystemTray
{
    /// <summary>
    /// Where FreeSystemTray keeps its settings, themes and logs: <c>%LOCALAPPDATA%\FreeSystemTray</c>, or,
    /// in a portable copy (a file named <see cref="PortableMarker"/> next to the exe, as the portable
    /// build has), a <c>data</c> folder next to the exe. <c>--settings</c> and <c>--log-dir</c> override both.
    /// </summary>
    public static class AppPaths
    {
        public const string PortableMarker = "portable.txt";

        public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarker));

        public static string DataDirectory => IsPortable
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSystemTray");
    }
}
