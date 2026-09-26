using System;

namespace FreeSystemTray.TrayHost
{
    /// <summary>
    /// Switches for the workarounds in <see cref="ManagedShellTrayHost"/>. All are on by default;
    /// turning one off gives ManagedShell's behaviour as shipped, for comparison tests.
    /// </summary>
    public sealed class TrayHostOptions
    {
        /// <summary>Send every icon message on to Explorer's tray too, so it stays current (FINDINGS.md, "Mirroring").</summary>
        public bool MirrorToExplorer { get; init; } = true;

        /// <summary>Give Explorer's taskbar back the always-on-top flag ManagedShell removes when it takes the role.</summary>
        public bool KeepExplorerTaskbarTopmost { get; init; } = true;

        /// <summary>Let Explorer answer ABM_GETSTATE and ABM_GETAUTOHIDEBAR(EX), so apps see its real auto-hide setting.</summary>
        public bool ForwardAutoHideQueries { get; init; } = true;

        /// <summary>
        /// Answer icon-position and taskbar-position queries with the tray window only for interactions
        /// that start there; otherwise Explorer answers, so clicks in its own tray open flyouts at the taskbar.
        /// </summary>
        public bool RoutePositionQueries { get; init; } = true;

        /// <summary>
        /// Keep <see cref="ITrayIcon.ExplorerPlacement"/> up to date by reading Explorer's tray after
        /// icons change and every few seconds (on a background thread).
        /// </summary>
        public bool ReadExplorerTray { get; init; } = true;

        /// <summary>
        /// Import Explorer's icons at start by reading its three tray toolbars, instead of ManagedShell's
        /// import, which switches off "hide inactive icons" for a moment and so reverses the order of
        /// Explorer's hidden-icons flyout.
        /// </summary>
        public bool ImportWithoutToggling { get; init; } = true;

        /// <summary>
        /// Raise our Shell_TrayWnd again as soon as Explorer's taskbar goes above it (Z-order events),
        /// rather than at ManagedShell's next 100 ms check.
        /// </summary>
        public bool KeepHostRole { get; init; } = true;

        /// <summary>
        /// Keep our icons and Explorer's tray in step where mirroring cannot: catch up on icon changes
        /// that went only to Explorer while its taskbar held the role, give a restarted Explorer the icons
        /// it lacks, and remove icons whose window no longer exists from both trays. Needs
        /// <see cref="ReadExplorerTray"/>.
        /// </summary>
        public bool SyncWithExplorer { get; init; } = true;

        /// <summary>Log every icon message and position query (verbose, for diagnostics).</summary>
        public bool TraceMessages { get; init; }

        /// <summary>Receives the host's log lines. Called on the UI thread; must not block or wait.</summary>
        public Action<string>? Log { get; init; }
    }
}
