using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FreeSystemTray.TrayHost.Interop
{
    /// <summary>
    /// Shell32 appends the calling app's executable to every NIM_ADD. Paths in a few machine-wide
    /// known folders are written as "{FOLDERID}\rest", the same form Explorer keeps in its IconStreams
    /// registry value. Explorer ties a GUID icon to that string and rejects an add for the GUID that
    /// carries the path in any other form (a plain path included).
    /// </summary>
    internal static class ShellPaths
    {
        // Seen on Windows 10 22H2, in shell32's messages and in IconStreams: Windows, System,
        // ProgramFilesX64 and ProgramFilesX86; the deepest folder wins, and paths elsewhere (the user
        // profile included) stay plain. SystemX86 is assumed by symmetry with ProgramFilesX86.
        private static readonly Guid[] KnownFolders =
        {
            new("F38BF404-1D43-42F2-9305-67DE0B28FC23"), // FOLDERID_Windows
            new("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"), // FOLDERID_System
            new("D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27"), // FOLDERID_SystemX86
            new("6D809377-6AF0-444B-8957-A3773F02200E"), // FOLDERID_ProgramFilesX64
            new("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E")  // FOLDERID_ProgramFilesX86
        };

        private static IReadOnlyList<(Guid Id, string Path)>? _folders;

        /// <summary>The executable path as shell32 writes it into NIM_ADD.</summary>
        public static string ToNotifyIconForm(string exePath) => ToNotifyIconForm(exePath, _folders ??= Resolve());

        internal static string ToNotifyIconForm(string exePath, IReadOnlyList<(Guid Id, string Path)> folders)
        {
            Guid bestId = Guid.Empty;
            string? bestPath = null;
            foreach ((Guid id, string path) in folders)
            {
                string folder = path.TrimEnd('\\');
                if (exePath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase) && (bestPath == null || folder.Length > bestPath.Length))
                {
                    bestId = id;
                    bestPath = folder;
                }
            }
            return bestPath == null ? exePath : bestId.ToString("B").ToUpperInvariant() + exePath.Substring(bestPath.Length);
        }

        private static IReadOnlyList<(Guid Id, string Path)> Resolve()
        {
            var folders = new List<(Guid, string)>();
            foreach (Guid id in KnownFolders)
            {
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out IntPtr p) == 0)
                    folders.Add((id, Marshal.PtrToStringUni(p) ?? ""));
                Marshal.FreeCoTaskMem(p);
            }
            return folders;
        }

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    }
}
