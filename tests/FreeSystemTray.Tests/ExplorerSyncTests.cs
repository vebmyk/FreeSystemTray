using System;
using System.Collections.Generic;
using System.Linq;
using FreeSystemTray.TrayHost;
using FreeSystemTray.TrayHost.Interop;
using FreeSystemTray.TrayHost.Protocol;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class GapSyncTests
    {
        private static readonly IntPtr W1 = new(0x100), W2 = new(0x200), W3 = new(0x300), Dead = new(0x999);
        private static readonly Guid G = new("7820ae73-23e3-4229-82c1-e41cb67d5b9c");

        private static ExplorerTrayButton B(IntPtr w, uint uid, string tip, Guid guid = default, IntPtr icon = default, uint state = 0,
            ExplorerPlacement toolbar = ExplorerPlacement.Overflow, bool hidden = false, uint version = 0) =>
            new(w, uid, guid, toolbar, hidden) { Tip = tip, Icon = icon, State = state, Version = version, CallbackMessage = 0x400 };

        private static List<SyncAction> Sync(IEnumerable<ExplorerTrayButton>? before, IEnumerable<ExplorerTrayButton> after,
            IEnumerable<ExplorerTrayButton> host, bool sameExplorer = true) =>
            GapSync.AfterGap(before?.ToList(), after.ToList(), sameExplorer, b => host.Any(h => h.SameIcon(b)), w => w != Dead);

        [Fact]
        public void Nothing_changed_nothing_to_do()
        {
            var tray = new[] { B(W1, 1, "a"), B(W2, 2, "b", G) };
            Assert.Empty(Sync(tray, tray, tray));
        }

        [Fact]
        public void An_icon_added_during_the_gap_is_added_to_ours()
        {
            var before = new[] { B(W1, 1, "a") };
            var after = new[] { B(W1, 1, "a"), B(W2, 5, "new") };
            SyncAction a = Assert.Single(Sync(before, after, before));
            Assert.Equal(SyncKind.AddToHost, a.Kind);
            Assert.Equal("new", a.Button.Tip);
        }

        [Fact]
        public void An_icon_of_a_dead_window_is_not_added()
        {
            Assert.Empty(Sync(new[] { B(W1, 1, "a") }, new[] { B(W1, 1, "a"), B(Dead, 5, "gone") }, new[] { B(W1, 1, "a") }));
        }

        [Fact]
        public void An_icon_Explorer_already_had_before_the_gap_is_left_alone_if_we_do_not_have_it()
        {
            // For example an app's delete that reached us but not Explorer: the gap did not do that.
            var tray = new[] { B(W1, 1, "a"), B(W2, 2, "stale") };
            Assert.Empty(Sync(tray, tray, new[] { B(W1, 1, "a") }));
        }

        [Fact]
        public void An_icon_deleted_during_the_gap_is_removed_from_ours()
        {
            var before = new[] { B(W1, 1, "a"), B(W2, 2, "b") };
            SyncAction a = Assert.Single(Sync(before, new[] { B(W1, 1, "a") }, before));
            Assert.Equal(SyncKind.RemoveFromHost, a.Kind);
            Assert.Equal(W2, a.Button.Window);
        }

        [Fact]
        public void After_an_Explorer_restart_missing_buttons_remove_nothing_but_new_ones_are_added()
        {
            var before = new[] { B(W1, 1, "a"), B(W2, 2, "b") };
            var after = new[] { B(W3, 7, "registered with the new Explorer only") };
            SyncAction a = Assert.Single(Sync(before, after, before, sameExplorer: false));
            Assert.Equal(SyncKind.AddToHost, a.Kind);
            Assert.Equal(W3, a.Button.Window);
        }

        [Fact]
        public void Without_a_read_from_before_only_icons_we_lack_are_added()
        {
            var ours = new[] { B(W1, 1, "old tip") };
            SyncAction a = Assert.Single(Sync(null, new[] { B(W1, 1, "new tip"), B(W2, 2, "b") }, ours));
            Assert.Equal(SyncKind.AddToHost, a.Kind);
            Assert.Equal(W2, a.Button.Window);
        }

        [Fact]
        public void Changed_fields_are_applied_to_ours()
        {
            var before = new[] { B(W1, 1, "a", icon: new IntPtr(10)), B(W2, 2, "b", G, version: 0) };
            var after = new[] { B(W1, 1, "a2", icon: new IntPtr(11), state: 1), B(W2, 2, "b", G, version: 4) };
            List<SyncAction> actions = Sync(before, after, before);
            Assert.Equal(2, actions.Count);
            Assert.All(actions, a => Assert.Equal(SyncKind.UpdateHost, a.Kind));
            Assert.Equal(Nif.Tip | Nif.Icon | Nif.State, actions[0].Changed);
            Assert.False(actions[0].VersionChanged);
            Assert.Equal(0u, actions[1].Changed);
            Assert.True(actions[1].VersionChanged);
        }

        [Fact]
        public void A_GUID_icon_is_the_same_icon_when_its_window_changed()
        {
            // Explorer's own icons get new windows when Explorer restarts; their GUID stays.
            var before = new[] { B(W1, 100, "Speakers", G) };
            var after = new[] { B(W2, 100, "Speakers", G) };
            Assert.Empty(Sync(before, after, before));
        }

        [Fact]
        public void An_icon_listed_twice_counts_once_by_its_visible_button()
        {
            var before = new[] { B(W1, 1, "a") };
            var after = new[] { B(W1, 1, "a", hidden: true), B(W1, 1, "a", toolbar: ExplorerPlacement.Taskbar), B(W2, 2, "b"), B(W2, 2, "b", hidden: true) };
            SyncAction a = Assert.Single(Sync(before, after, before));
            Assert.Equal(SyncKind.AddToHost, a.Kind);
            Assert.False(a.Button.Hidden);
        }
    }

    public class ExplorerTrayButtonTests
    {
        private static readonly IntPtr W1 = new(0x100), W2 = new(0x200);
        private static readonly Guid G = new("7820ae74-23e3-4229-82c1-e41cb67d5b9c");

        [Fact]
        public void Icons_are_the_same_by_GUID_or_else_by_window_and_uID()
        {
            var a = new ExplorerTrayButton(W1, 1, G, ExplorerPlacement.Taskbar, false);
            Assert.True(a.SameIcon(new ExplorerTrayButton(W2, 9, G, ExplorerPlacement.Overflow, true)));
            Assert.False(a.SameIcon(new ExplorerTrayButton(W1, 1, Guid.Empty, ExplorerPlacement.Taskbar, false)));
            var b = new ExplorerTrayButton(W1, 1, Guid.Empty, ExplorerPlacement.Taskbar, false);
            Assert.True(b.SameIcon(new ExplorerTrayButton(W1, 1, Guid.Empty, ExplorerPlacement.Overflow, false)));
            Assert.False(b.SameIcon(new ExplorerTrayButton(W1, 2, Guid.Empty, ExplorerPlacement.Taskbar, false)));
        }

        [Fact]
        public void The_hidden_state_is_bit_0_of_Explorers_state()
        {
            // Seen on Windows 10 22H2: an icon its app hid, and Meet Now, which Windows hides.
            Assert.True(new ExplorerTrayButton(W1, 1, Guid.Empty, ExplorerPlacement.Overflow, true) { State = 0x400001 }.StateHidden);
            Assert.True(new ExplorerTrayButton(W1, 1, G, ExplorerPlacement.Overflow, true) { State = 0x200001 }.StateHidden);
            Assert.False(new ExplorerTrayButton(W1, 1, Guid.Empty, ExplorerPlacement.Overflow, false) { State = 0x600000 }.StateHidden);
        }
    }

    public class NotifyIconMessageCreateTests
    {
        private static readonly Guid G = new("ba82e2dc-f405-47ad-b032-cf0faa0e3933");

        [Fact]
        public void An_add_is_shaped_like_shell32s()
        {
            var state = new IconState(0x10438, 100, G, 0x460, "Windows Security", true, 4);
            byte[] p = NotifyIconMessage.Create(Nim.Add, state, Nif.Message | Nif.Tip | Nif.Icon | Nif.State | Nif.ShowTip, 0x1234,
                @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\SecurityHealthSystray.exe");
            Assert.Equal(1484, p.Length);
            Assert.True(NotifyIconMessage.TryParse(p, out NotifyIconMessage m));
            Assert.Equal(Nim.Add, m.Message);
            Assert.Equal(0x10438u, m.HWnd);
            Assert.Equal(100u, m.Uid);
            Assert.Equal(Nif.Message | Nif.Tip | Nif.Icon | Nif.State | Nif.ShowTip | Nif.Guid, m.Flags);
            Assert.Equal(0x460u, m.CallbackMessage);
            Assert.Equal(0x1234u, m.HIcon);
            Assert.Equal("Windows Security", m.Tip);
            Assert.Equal(NotifyIconMessage.NisHidden, m.State);
            Assert.Equal(NotifyIconMessage.NisHidden, m.StateMask);
            Assert.True(m.UsesGuid);
            Assert.Equal(956, m.CbSize);
            Assert.Equal(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\SecurityHealthSystray.exe", NotifyIconMessage.ReadExePath(p));
        }

        [Fact]
        public void Without_an_icon_handle_there_is_no_NIF_ICON()
        {
            byte[] p = NotifyIconMessage.Create(Nim.Modify, new IconState(1, 2, Guid.Empty, 0, "t", false, 0), Nif.Tip | Nif.Icon);
            Assert.True(NotifyIconMessage.TryParse(p, out NotifyIconMessage m));
            Assert.Equal(Nif.Tip, m.Flags);
            Assert.Equal("", NotifyIconMessage.ReadExePath(p));
        }

        [Fact]
        public void A_state_change_can_show_the_icon_again()
        {
            byte[] p = NotifyIconMessage.Create(Nim.Modify, new IconState(1, 2, Guid.Empty, 0, null, false, 0), Nif.State);
            Assert.True(NotifyIconMessage.TryParse(p, out NotifyIconMessage m));
            Assert.Equal(0u, m.State);
            Assert.Equal(NotifyIconMessage.NisHidden, m.StateMask);
        }

        [Fact]
        public void Set_version_and_delete_carry_only_the_identity()
        {
            var state = new IconState(1, 2, G, 0x400, "t", false, 4);
            Assert.True(NotifyIconMessage.TryParse(NotifyIconMessage.Create(Nim.SetVersion, state, Nif.Tip), out NotifyIconMessage v));
            Assert.Equal(Nif.Guid, v.Flags);
            Assert.Equal(4u, v.Version);
            Assert.True(NotifyIconMessage.TryParse(NotifyIconMessage.Create(Nim.Delete, state with { Guid = Guid.Empty }, Nif.Tip), out NotifyIconMessage d));
            Assert.Equal(0u, d.Flags);
            Assert.Equal(Nim.Delete, d.Message);
        }
    }
}
