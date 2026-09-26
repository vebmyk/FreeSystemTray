using System;

namespace FreeSystemTray.TrayHost.Protocol
{
    internal enum MirrorFixup
    {
        None,

        /// <summary>Explorer already has the icon (a re-add after TaskbarCreated): update its copy instead.</summary>
        ResendAsModify,

        /// <summary>Explorer does not know the icon (for example after it restarted): add it from our state.</summary>
        SendFullAdd
    }

    internal static class MirrorPolicy
    {
        /// <summary>
        /// What to send when Explorer rejects a mirrored icon message. <paramref name="hostHasIcon"/>
        /// is whether ManagedShell holds the icon after handling the message; without it there is no
        /// state to rebuild from, and ManagedShell has already forwarded the message itself.
        /// </summary>
        public static MirrorFixup ForRejected(uint nim, bool hostHasIcon)
        {
            if (!hostHasIcon) return MirrorFixup.None;
            return nim switch
            {
                Nim.Add => MirrorFixup.ResendAsModify,
                Nim.Modify or Nim.SetVersion => MirrorFixup.SendFullAdd,
                // A delete Explorer rejects is for an icon it no longer has; SETFOCUS has no state.
                _ => MirrorFixup.None
            };
        }
    }

    internal static class PositionQueryRouting
    {
        /// <summary>
        /// How long after the last click in the tray window a query that arrives while the cursor is
        /// elsewhere still counts as coming from it (a flyout that asks late, or the user moving
        /// towards the flyout).
        /// </summary>
        public static readonly TimeSpan InteractionGrace = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Whether an icon-position (Shell_NotifyIconGetRect) or taskbar-position (ABM_GETTASKBARPOS)
        /// query should be answered with the tray window instead of Explorer's taskbar. Apps ask the
        /// tray host both when an icon in our window is clicked and when the same icon is clicked in
        /// Explorer's own tray, so the answer depends on where the user is interacting. The tray's
        /// hidden-icons flyout (<paramref name="overflowBounds"/>, empty while closed) counts as part
        /// of the tray window.
        /// </summary>
        public static bool AnswerWithTrayWindow(
            bool trayVisible, PixelRect trayBounds, PixelRect overflowBounds, PixelPoint cursor, bool cursorOverExplorerTray, TimeSpan? sinceLastInteraction)
        {
            if (!trayVisible || trayBounds.IsEmpty) return false;
            if (trayBounds.Contains(cursor) || (!overflowBounds.IsEmpty && overflowBounds.Contains(cursor))) return true;
            if (cursorOverExplorerTray) return false;
            return sinceLastInteraction is TimeSpan t && t <= InteractionGrace;
        }
    }
}
