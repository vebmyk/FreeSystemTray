using System;
using System.Collections.Generic;
using FreeSystemTray.TrayHost.Interop;

namespace FreeSystemTray.TrayHost.Protocol
{
    internal enum SyncKind
    {
        /// <summary>The icon was added while Explorer held the role: add it to ours.</summary>
        AddToHost,

        /// <summary>The icon changed while Explorer held the role: apply the changed fields to ours.</summary>
        UpdateHost,

        /// <summary>The icon was deleted while Explorer held the role: delete ours.</summary>
        RemoveFromHost
    }

    /// <param name="Changed">For <see cref="SyncKind.UpdateHost"/>: the NIF_* fields that changed (tip, icon, state, callback message).</param>
    /// <param name="VersionChanged">For <see cref="SyncKind.UpdateHost"/>: the icon's version changed.</param>
    internal readonly record struct SyncAction(SyncKind Kind, ExplorerTrayButton Button, uint Changed = 0, bool VersionChanged = false);

    /// <summary>
    /// While Explorer's taskbar is above our Shell_TrayWnd (a gap: Explorer got the foreground, or it
    /// restarted), apps' icon messages go to Explorer only. Afterwards Explorer's tray is compared with a
    /// read from before the gap: what changed in Explorer's tray since then, and disagrees with ours,
    /// happened during the gap. Changes made through us before the gap were mirrored to Explorer, so they
    /// show up too, but agree with ours and change nothing.
    /// </summary>
    internal static class GapSync
    {
        /// <param name="before">Explorer's tray read before the gap began; null if there is none.</param>
        /// <param name="after">Explorer's tray read after the gap ended.</param>
        /// <param name="sameExplorer">
        /// Both reads are of the same Explorer taskbar. If Explorer restarted, a missing button says
        /// nothing about the app, and nothing is removed from ours.
        /// </param>
        /// <param name="hostHas">Whether ManagedShell holds the icon (by window and uID, or by GUID).</param>
        /// <param name="windowAlive">Whether the icon's window still exists (Explorer keeps icons of dead windows).</param>
        public static List<SyncAction> AfterGap(IReadOnlyList<ExplorerTrayButton>? before, IReadOnlyList<ExplorerTrayButton> after,
            bool sameExplorer, Func<ExplorerTrayButton, bool> hostHas, Func<IntPtr, bool> windowAlive)
        {
            var actions = new List<SyncAction>();
            bool compare = before != null && sameExplorer;
            foreach (ExplorerTrayButton b in ExplorerTrayIcons.OnePerIcon(after))
            {
                ExplorerTrayButton? previous = compare ? ExplorerTrayIcons.Find(before!, b) : null;
                if (!hostHas(b))
                {
                    // Explorer had it before the gap too, yet we do not: not the gap's doing (for example
                    // an app's delete that did not reach Explorer). Leave it.
                    if (previous == null && b.Window != IntPtr.Zero && windowAlive(b.Window))
                        actions.Add(new SyncAction(SyncKind.AddToHost, b));
                    continue;
                }
                if (previous is not ExplorerTrayButton p) continue;
                uint changed = 0;
                if (p.Tip != b.Tip) changed |= Nif.Tip;
                if (p.Icon != b.Icon) changed |= Nif.Icon;
                if (p.StateHidden != b.StateHidden) changed |= Nif.State;
                if (p.CallbackMessage != b.CallbackMessage) changed |= Nif.Message;
                bool version = p.Version != b.Version;
                if (changed != 0 || version) actions.Add(new SyncAction(SyncKind.UpdateHost, b, changed, version));
            }
            if (compare)
            {
                foreach (ExplorerTrayButton p in ExplorerTrayIcons.OnePerIcon(before!))
                {
                    if (ExplorerTrayIcons.Find(after, p) == null && hostHas(p)) actions.Add(new SyncAction(SyncKind.RemoveFromHost, p));
                }
            }
            return actions;
        }
    }
}
