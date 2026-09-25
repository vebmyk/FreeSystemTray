using System;
using System.Buffers.Binary;
using System.Text;
using FreeSystemTray.TrayHost;
using FreeSystemTray.TrayHost.Protocol;
using Xunit;

namespace FreeSystemTray.Tests
{
    public class NotifyIconMessageTests
    {
        private static readonly Guid SecurityGuid = new("ba82e2dc-f405-47ad-b032-cf0faa0e3933");

        /// <summary>A Shell_NotifyIcon payload shaped like Windows 10 shell32's (1484 bytes).</summary>
        internal static byte[] Payload(uint nim, uint hwnd, uint uid, uint flags, uint callback = 0, uint hIcon = 0,
            string tip = "", uint version = 0, Guid guid = default, string exe = "", string info = "", uint state = 0)
        {
            var p = new byte[1484];
            U32(p, 0, NotifyIconMessage.Signature);
            U32(p, 4, nim);
            U32(p, 8, 956);
            U32(p, 12, hwnd);
            U32(p, 16, uid);
            U32(p, 20, flags);
            U32(p, 24, callback);
            U32(p, 28, hIcon);
            Str(p, 32, tip);
            U32(p, 8 + 280, state);
            U32(p, 8 + 284, state);
            Str(p, 8 + 288, info);
            U32(p, 8 + 800, version);
            guid.TryWriteBytes(p.AsSpan(8 + 936));
            Str(p, 964, exe);
            return p;
        }

        private static void U32(byte[] p, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(offset), value);

        private static void Str(byte[] p, int offset, string value) => Encoding.Unicode.GetBytes(value).CopyTo(p, offset);

        private static string InfoText(byte[] p) => NotifyIconMessage.ReadString(p, 8 + 288, 256);

        [Fact]
        public void TryParse_reads_the_fields_at_shell32s_offsets()
        {
            byte[] p = Payload(Nim.Modify, 0x10438, 100, 0xAE, 0x460, 0x1234, "Windows Security", 4, SecurityGuid);
            Assert.True(NotifyIconMessage.TryParse(p, out NotifyIconMessage m));
            Assert.Equal(Nim.Modify, m.Message);
            Assert.Equal(0x10438u, m.HWnd);
            Assert.Equal(100u, m.Uid);
            Assert.Equal(0xAEu, m.Flags);
            Assert.Equal(0x460u, m.CallbackMessage);
            Assert.Equal(0x1234u, m.HIcon);
            Assert.Equal("Windows Security", m.Tip);
            Assert.Equal(4u, m.Version);
            Assert.Equal(SecurityGuid, m.Guid);
            Assert.True(m.UsesGuid);
            Assert.Equal(956, m.CbSize);
            Assert.Equal(1484, m.PayloadSize);
        }

        [Fact]
        public void The_guid_identifies_the_icon_only_with_NIF_GUID()
        {
            Assert.True(NotifyIconMessage.TryParse(Payload(Nim.Modify, 1, 2, Nif.Tip, guid: SecurityGuid), out NotifyIconMessage m));
            Assert.False(m.UsesGuid);
        }

        [Fact]
        public void TryParse_needs_at_least_the_identifying_fields()
        {
            Assert.False(NotifyIconMessage.TryParse(new byte[NotifyIconMessage.MinimumSize - 1], out _));
            Assert.True(NotifyIconMessage.TryParse(Payload(Nim.Delete, 7, 8, 0).AsSpan(0, NotifyIconMessage.MinimumSize), out NotifyIconMessage m));
            Assert.Equal(7u, m.HWnd);
            Assert.Null(m.Tip);
        }

        [Fact]
        public void The_exe_path_travels_only_with_adds()
        {
            Assert.Equal(@"C:\apps\app.exe", NotifyIconMessage.ReadExePath(Payload(Nim.Add, 1, 2, 7, exe: @"C:\apps\app.exe")));
            Assert.Equal("", NotifyIconMessage.ReadExePath(Payload(Nim.Modify, 1, 2, 7)));
            Assert.Null(NotifyIconMessage.ReadExePath(new byte[NotifyIconMessage.FullSize]));
        }

        [Fact]
        public void WithMessage_changes_only_the_message()
        {
            byte[] add = Payload(Nim.Add, 1, 2, 7, 0x400, 9, "tip");
            byte[] modify = NotifyIconMessage.WithMessage(add, Nim.Modify);
            Assert.Equal(Nim.Modify, BinaryPrimitives.ReadUInt32LittleEndian(modify.AsSpan(4)));
            modify[4] = (byte)Nim.Add;
            Assert.Equal(add, modify);
        }

        [Fact]
        public void BuildFullAdd_turns_a_rejected_modify_into_a_complete_add()
        {
            // Windows Security re-registers with a modify that has no NIF_MESSAGE (0xAE).
            byte[] modify = Payload(Nim.Modify, 0x10438, 100, 0xAE, hIcon: 0x5555, tip: "new tip", guid: SecurityGuid);
            var state = new IconState(0x10438, 100, SecurityGuid, 0x460, "stored tip", false, 4);
            const string exe = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\SecurityHealthSystray.exe";

            byte[]? add = NotifyIconMessage.BuildFullAdd(modify, state, 0x5555, exe);

            Assert.NotNull(add);
            Assert.Equal(modify.Length, add!.Length);
            Assert.True(NotifyIconMessage.TryParse(add, out NotifyIconMessage m));
            Assert.Equal(Nim.Add, m.Message);
            Assert.Equal(Nif.Message | Nif.Icon | Nif.Tip | Nif.Guid | Nif.ShowTip, m.Flags);
            Assert.Equal(0x460u, m.CallbackMessage);
            Assert.Equal(0x5555u, m.HIcon);
            Assert.Equal("stored tip", m.Tip);
            Assert.Equal(SecurityGuid, m.Guid);
            Assert.Equal(956, m.CbSize);
            Assert.Equal(NotifyIconMessage.Signature, BinaryPrimitives.ReadUInt32LittleEndian(add));
            Assert.Equal(exe, NotifyIconMessage.ReadExePath(add));
        }

        [Fact]
        public void BuildFullAdd_keeps_a_balloon_from_the_template()
        {
            byte[] modify = Payload(Nim.Modify, 1, 2, Nif.Info, info: "Download finished");
            byte[] add = NotifyIconMessage.BuildFullAdd(modify, new IconState(1, 2, Guid.Empty, 0x400, "tip", false, 0), 9, null)!;
            Assert.True(NotifyIconMessage.TryParse(add, out NotifyIconMessage m));
            Assert.True(m.Has(Nif.Info));
            Assert.False(m.Has(Nif.Guid));
            Assert.Equal("Download finished", InfoText(add));
        }

        [Fact]
        public void BuildFullAdd_marks_a_hidden_icon_hidden()
        {
            byte[] add = NotifyIconMessage.BuildFullAdd(Payload(Nim.Modify, 1, 2, Nif.Tip), new IconState(1, 2, Guid.Empty, 0x400, "tip", true, 0), 9, null)!;
            Assert.True(NotifyIconMessage.TryParse(add, out NotifyIconMessage m));
            Assert.True(m.Has(Nif.State));
            Assert.Equal(NotifyIconMessage.NisHidden, m.State);
            Assert.Equal(NotifyIconMessage.NisHidden, m.StateMask);
        }

        [Fact]
        public void BuildFullAdd_leaves_room_for_the_tooltips_terminating_null()
        {
            string longTip = new('x', 200);
            byte[] add = NotifyIconMessage.BuildFullAdd(Payload(Nim.Modify, 1, 2, 0), new IconState(1, 2, Guid.Empty, 0, longTip, false, 0), 9, null)!;
            Assert.True(NotifyIconMessage.TryParse(add, out NotifyIconMessage m));
            Assert.Equal(new string('x', 127), m.Tip);
        }

        [Fact]
        public void BuildFullAdd_needs_a_full_template()
        {
            Assert.Null(NotifyIconMessage.BuildFullAdd(new byte[NotifyIconMessage.FullSize - 1], default, 0, null));
        }

        [Fact]
        public void BuildSetVersion_identifies_the_icon_and_carries_the_version()
        {
            byte[] v = NotifyIconMessage.BuildSetVersion(Payload(Nim.Modify, 1, 2, 0xAE), new IconState(0x10438, 100, SecurityGuid, 0x460, "tip", false, 4))!;
            Assert.True(NotifyIconMessage.TryParse(v, out NotifyIconMessage m));
            Assert.Equal(Nim.SetVersion, m.Message);
            Assert.Equal(Nif.Guid, m.Flags);
            Assert.Equal(0x10438u, m.HWnd);
            Assert.Equal(100u, m.Uid);
            Assert.Equal(4u, m.Version);
            Assert.Equal(SecurityGuid, m.Guid);
        }
    }

    public class AppBarMessageTests
    {
        private static byte[] Payload(int abm, uint hwnd, uint edge, PixelRect rc, long shared = 0, uint pid = 0, int cbSize = AppBarMessage.AppBarDataSize)
        {
            var p = new byte[AppBarMessage.Size];
            BinaryPrimitives.WriteInt32LittleEndian(p, cbSize);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), hwnd);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), edge);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(16), rc.Left);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(20), rc.Top);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(24), rc.Right);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(28), rc.Bottom);
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(40), abm);
            BinaryPrimitives.WriteInt64LittleEndian(p.AsSpan(48), shared);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(56), pid);
            return p;
        }

        [Fact]
        public void TryParse_reads_APPBARMSGDATAV3()
        {
            var rc = new PixelRect(0, 1040, 1920, 1080);
            Assert.True(AppBarMessage.TryParse(Payload(Abm.GetTaskbarPos, 0x601DA, 3, rc, 0x1234, 42), out AppBarMessage m));
            Assert.Equal(Abm.GetTaskbarPos, m.Message);
            Assert.Equal(0x601DAu, m.HWnd);
            Assert.Equal(3u, m.Edge);
            Assert.Equal(rc, m.Rect);
            Assert.Equal(0x1234, m.SharedMemory);
            Assert.Equal(42u, m.SourceProcessId);
        }

        [Fact]
        public void TryParse_rejects_other_sizes()
        {
            Assert.False(AppBarMessage.TryParse(new byte[AppBarMessage.Size + 4], out _));
            Assert.False(AppBarMessage.TryParse(Payload(Abm.GetState, 0, 0, default, cbSize: 48), out _));
        }

        [Fact]
        public void WithSharedMemory_changes_only_the_shared_memory_fields()
        {
            byte[] p = Payload(Abm.GetTaskbarPos, 1, 3, new PixelRect(1, 2, 3, 4), 0x1111, 7);
            Assert.True(AppBarMessage.TryParse(AppBarMessage.WithSharedMemory(p, 0x2222, 9), out AppBarMessage m));
            Assert.Equal(0x2222, m.SharedMemory);
            Assert.Equal(9u, m.SourceProcessId);
            Assert.Equal(new PixelRect(1, 2, 3, 4), m.Rect);
        }

        [Fact]
        public void ReadAppBarData_reads_the_answer_block()
        {
            byte[] p = Payload(Abm.GetTaskbarPos, 1, 3, new PixelRect(0, 1040, 1920, 1080));
            (PixelRect rect, uint edge) = AppBarMessage.ReadAppBarData(p.AsSpan(0, AppBarMessage.AppBarDataSize));
            Assert.Equal(new PixelRect(0, 1040, 1920, 1080), rect);
            Assert.Equal(3u, edge);
        }
    }

    public class IconRectQueryTests
    {
        [Fact]
        public void TryParse_reads_WINNOTIFYICONIDENTIFIER()
        {
            var p = new byte[IconRectQuery.Size];
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), 0x80048);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20), 100);
            Guid volume = new("7820ae73-23e3-4229-82c1-e41cb67d5b9c");
            volume.TryWriteBytes(p.AsSpan(24));
            Assert.True(IconRectQuery.TryParse(p, out IconRectQuery q));
            Assert.Equal(2, q.Message);
            Assert.Equal(0x80048u, q.HWnd);
            Assert.Equal(100u, q.Uid);
            Assert.Equal(volume, q.Guid);
        }

        [Theory]
        [InlineData(1649, 1040)]
        [InlineData(24, 40)]
        [InlineData(-1200, 35)]
        [InlineData(300, -20)]
        public void Answers_round_trip_through_MAKELPARAM(int x, int y)
        {
            Assert.Equal((x, y), IconRectQuery.DecodeResult(IconRectQuery.EncodeResult(x, y)));
        }
    }
}
