using System;
using System.Buffers.Binary;
using System.Text;

namespace FreeSystemTray.TrayHost.Protocol
{
    /// <summary>
    /// COPYDATASTRUCT.dwData values shell32 uses when it sends WM_COPYDATA to Shell_TrayWnd.
    /// Handles inside the payloads are 32-bit on every architecture. References: ReactOS and Wine
    /// systray, ManagedShell's NativeMethods.Shell32.
    /// </summary>
    internal static class TrayCopyData
    {
        public const long AppBar = 0;      // SHAppBarMessage: APPBARMSGDATAV3
        public const long NotifyIcon = 1;  // Shell_NotifyIcon: SHELLTRAYDATA
        public const long IconRect = 3;    // Shell_NotifyIconGetRect: WINNOTIFYICONIDENTIFIER
    }

    internal static class Nim
    {
        public const uint Add = 0, Modify = 1, Delete = 2, SetFocus = 3, SetVersion = 4;

        public static string Name(uint nim) => nim switch
        {
            Add => "ADD",
            Modify => "MODIFY",
            Delete => "DELETE",
            SetFocus => "SETFOCUS",
            SetVersion => "SETVERSION",
            _ => "NIM" + nim
        };
    }

    internal static class Nif
    {
        public const uint Message = 0x01, Icon = 0x02, Tip = 0x04, State = 0x08, Info = 0x10,
            Guid = 0x20, Realtime = 0x40, ShowTip = 0x80;
    }

    internal static class Abm
    {
        public const int New = 0, Remove = 1, QueryPos = 2, SetPos = 3, GetState = 4, GetTaskbarPos = 5,
            Activate = 6, GetAutoHideBar = 7, SetAutoHideBar = 8, WindowPosChanged = 9, SetState = 10,
            GetAutoHideBarEx = 11;

        public static string Name(int abm) => abm switch
        {
            New => "ABM_NEW",
            Remove => "ABM_REMOVE",
            QueryPos => "ABM_QUERYPOS",
            SetPos => "ABM_SETPOS",
            GetState => "ABM_GETSTATE",
            GetTaskbarPos => "ABM_GETTASKBARPOS",
            Activate => "ABM_ACTIVATE",
            GetAutoHideBar => "ABM_GETAUTOHIDEBAR",
            SetAutoHideBar => "ABM_SETAUTOHIDEBAR",
            WindowPosChanged => "ABM_WINDOWPOSCHANGED",
            SetState => "ABM_SETSTATE",
            GetAutoHideBarEx => "ABM_GETAUTOHIDEBAREX",
            _ => "ABM" + abm
        };
    }

    /// <summary>What we know about an icon when we rebuild it for Explorer.</summary>
    internal readonly record struct IconState(
        uint HWnd, uint Uid, Guid Guid, uint CallbackMessage, string? Tip, bool Hidden, uint Version);

    /// <summary>
    /// Shell_NotifyIcon's payload (SHELLTRAYDATA): a signature, the NIM_* message, then a
    /// NOTIFYICONDATAW whose handles are 32-bit.
    /// </summary>
    internal readonly record struct NotifyIconMessage(
        uint Message, uint HWnd, uint Uid, uint Flags, uint CallbackMessage, uint HIcon,
        string? Tip, uint State, uint StateMask, uint Version, Guid Guid, int CbSize, int PayloadSize)
    {
        private const int MessageOffset = 4;
        private const int Data = 8;                   // NOTIFYICONDATAW starts here
        private const int HWndOffset = Data + 4;
        private const int UidOffset = Data + 8;
        private const int FlagsOffset = Data + 12;
        private const int CallbackOffset = Data + 16;
        private const int IconOffset = Data + 20;
        private const int TipOffset = Data + 24;       // WCHAR szTip[128]
        private const int TipChars = 128;
        private const int StateOffset = Data + 280;
        private const int StateMaskOffset = Data + 284;
        private const int VersionOffset = Data + 800;  // uTimeoutOrVersion, after WCHAR szInfo[256]
        private const int GuidOffset = Data + 936;     // after WCHAR szInfoTitle[64] and dwInfoFlags

        /// <summary>Up to and including hIcon: enough to identify the icon.</summary>
        public const int MinimumSize = IconOffset + 4;

        /// <summary>SHELLTRAYDATA with the full NOTIFYICONDATAW (through hBalloonIcon).</summary>
        public const int FullSize = Data + 956;

        // Windows 10 shell32 sends 1484 bytes: FullSize plus WCHAR[260] with the caller's executable
        // (NIM_ADD only; empty in other messages). Paths under known folders are written as
        // "{FOLDERID}\rest", for example "{6D809377-6AF0-444B-8957-A3773F02200E}\Vendor\app.exe".
        private const int ExePathOffset = FullSize;
        private const int ExePathChars = 260;

        public const uint Signature = 0x34753423;
        public const uint NisHidden = 0x1;

        public bool Has(uint nif) => (Flags & nif) != 0;

        /// <summary>The GUID identifies the icon only when NIF_GUID is set.</summary>
        public bool UsesGuid => Has(Nif.Guid) && Guid != Guid.Empty;

        public static bool TryParse(ReadOnlySpan<byte> payload, out NotifyIconMessage message)
        {
            message = default;
            if (payload.Length < MinimumSize) return false;

            string? tip = payload.Length >= TipOffset + TipChars * 2 ? ReadString(payload, TipOffset, TipChars) : null;
            message = new NotifyIconMessage(
                U32(payload, MessageOffset), U32(payload, HWndOffset), U32(payload, UidOffset),
                U32(payload, FlagsOffset), U32(payload, CallbackOffset), U32(payload, IconOffset),
                tip,
                payload.Length >= StateMaskOffset + 4 ? U32(payload, StateOffset) : 0,
                payload.Length >= StateMaskOffset + 4 ? U32(payload, StateMaskOffset) : 0,
                payload.Length >= VersionOffset + 4 ? U32(payload, VersionOffset) : 0,
                payload.Length >= GuidOffset + 16 ? new Guid(payload.Slice(GuidOffset, 16)) : Guid.Empty,
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(Data)),
                payload.Length);
            return true;
        }

        /// <summary>A copy of <paramref name="payload"/> with a different NIM_* message.</summary>
        public static byte[] WithMessage(ReadOnlySpan<byte> payload, uint nim)
        {
            byte[] copy = payload.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(MessageOffset), nim);
            return copy;
        }

        /// <summary>
        /// A NIM_ADD for an icon Explorer does not know, built from <paramref name="template"/> (the
        /// message Explorer rejected) and the icon's stored state. Balloon fields and NIF_SHOWTIP /
        /// NIF_REALTIME are kept from the template. Returns null if the template is too short.
        /// </summary>
        /// <param name="exePath">
        /// The app's executable. Shell32 appends it to NIM_ADD only (not to modifies), and Explorer
        /// rejects an add for a GUID icon without it: the GUID is tied to the app's path.
        /// </param>
        public static byte[]? BuildFullAdd(ReadOnlySpan<byte> template, IconState icon, uint hIcon, string? exePath)
        {
            if (template.Length < FullSize) return null;
            byte[] add = template.ToArray();
            Span<byte> s = add;

            uint flags = U32(template, FlagsOffset) & (Nif.Info | Nif.ShowTip | Nif.Realtime);
            flags |= Nif.Message | Nif.Tip;
            if (hIcon != 0) flags |= Nif.Icon;
            if (icon.Guid != Guid.Empty) flags |= Nif.Guid;
            if (icon.Hidden) flags |= Nif.State;

            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(MessageOffset), Nim.Add);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(HWndOffset), icon.HWnd);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(UidOffset), icon.Uid);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(FlagsOffset), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(CallbackOffset), icon.CallbackMessage);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(IconOffset), hIcon);
            WriteString(s, TipOffset, TipChars, icon.Tip);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(StateOffset), icon.Hidden ? NisHidden : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(StateMaskOffset), icon.Hidden ? NisHidden : 0);
            icon.Guid.TryWriteBytes(s.Slice(GuidOffset, 16));
            if (exePath != null && s.Length >= ExePathOffset + ExePathChars * 2) WriteString(s, ExePathOffset, ExePathChars, exePath);
            return add;
        }

        /// <summary>A NIM_SETVERSION for the icon, built from <paramref name="template"/>. Null if it is too short.</summary>
        public static byte[]? BuildSetVersion(ReadOnlySpan<byte> template, IconState icon)
        {
            if (template.Length < FullSize) return null;
            byte[] msg = template.ToArray();
            Span<byte> s = msg;
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(MessageOffset), Nim.SetVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(HWndOffset), icon.HWnd);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(UidOffset), icon.Uid);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(FlagsOffset), icon.Guid != Guid.Empty ? Nif.Guid : 0);
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(VersionOffset), icon.Version);
            icon.Guid.TryWriteBytes(s.Slice(GuidOffset, 16));
            return msg;
        }

        public override string ToString() =>
            $"{Nim.Name(Message)} hwnd=0x{HWnd:X} uid={Uid} nif=0x{Flags:X}{(UsesGuid ? " guid=" + Guid : "")}";

        /// <summary>Sizes, for diagnosing the message format.</summary>
        public string Sizes => $"payload={PayloadSize} cbSize={CbSize}";

        /// <summary>The executable path shell32 appends to NIM_ADD (empty in other messages; null if absent).</summary>
        public static string? ReadExePath(ReadOnlySpan<byte> payload) =>
            payload.Length >= ExePathOffset + ExePathChars * 2 ? ReadString(payload, ExePathOffset, ExePathChars) : null;

        internal static uint U32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(offset));

        internal static string ReadString(ReadOnlySpan<byte> s, int offset, int maxChars)
        {
            ReadOnlySpan<byte> bytes = s.Slice(offset, maxChars * 2);
            int length = 0;
            while (length < maxChars && (bytes[length * 2] != 0 || bytes[length * 2 + 1] != 0)) length++;
            return Encoding.Unicode.GetString(bytes.Slice(0, length * 2));
        }

        internal static void WriteString(Span<byte> s, int offset, int maxChars, string? value)
        {
            Span<byte> field = s.Slice(offset, maxChars * 2);
            field.Clear();
            if (string.IsNullOrEmpty(value)) return;
            // Keep room for the terminating null.
            string text = value.Length >= maxChars ? value.Substring(0, maxChars - 1) : value;
            Encoding.Unicode.GetBytes(text, field);
        }
    }

    /// <summary>SHAppBarMessage's payload (APPBARMSGDATAV3): an APPBARDATA with 32-bit handles plus routing fields.</summary>
    internal readonly record struct AppBarMessage(int Message, uint HWnd, uint Edge, PixelRect Rect, long SharedMemory, uint SourceProcessId)
    {
        public const int Size = 64;
        public const int AppBarDataSize = 40;
        private const int HWndOffset = 4;
        private const int EdgeOffset = 12;
        private const int RectOffset = 16;
        private const int MessageOffset = 40;
        private const int SharedMemoryOffset = 48;
        private const int ProcessIdOffset = 56;

        public static bool TryParse(ReadOnlySpan<byte> payload, out AppBarMessage message)
        {
            message = default;
            if (payload.Length != Size || BinaryPrimitives.ReadInt32LittleEndian(payload) != AppBarDataSize) return false;
            message = new AppBarMessage(
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(MessageOffset)),
                NotifyIconMessage.U32(payload, HWndOffset),
                NotifyIconMessage.U32(payload, EdgeOffset),
                ReadRect(payload, RectOffset),
                BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(SharedMemoryOffset)),
                NotifyIconMessage.U32(payload, ProcessIdOffset));
            return true;
        }

        /// <summary>A copy of <paramref name="payload"/> pointing at different shared memory.</summary>
        public static byte[] WithSharedMemory(ReadOnlySpan<byte> payload, long sharedMemory, uint processId)
        {
            byte[] copy = payload.ToArray();
            BinaryPrimitives.WriteInt64LittleEndian(copy.AsSpan(SharedMemoryOffset), sharedMemory);
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(ProcessIdOffset), processId);
            return copy;
        }

        /// <summary>Reads the rc and uEdge fields of an APPBARDATA (the shared-memory block of ABM_GETTASKBARPOS).</summary>
        public static (PixelRect Rect, uint Edge) ReadAppBarData(ReadOnlySpan<byte> appBarData) =>
            (ReadRect(appBarData, RectOffset), NotifyIconMessage.U32(appBarData, EdgeOffset));

        private static PixelRect ReadRect(ReadOnlySpan<byte> s, int offset) => new(
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset)),
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset + 4)),
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset + 8)),
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(offset + 12)));

        public override string ToString() => $"{Abm.Name(Message)} hwnd=0x{HWnd:X} edge={Edge}";
    }

    /// <summary>
    /// Shell_NotifyIconGetRect's payload (WINNOTIFYICONIDENTIFIER). It asks twice: message 1 for the
    /// icon's top-left corner, message 2 for its size, each answered as MAKELPARAM(x, y).
    /// </summary>
    internal readonly record struct IconRectQuery(int Message, uint HWnd, uint Uid, Guid Guid)
    {
        public const int Size = 40;

        public static bool TryParse(ReadOnlySpan<byte> payload, out IconRectQuery query)
        {
            query = default;
            if (payload.Length < Size) return false;
            query = new IconRectQuery(
                BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(4)),
                NotifyIconMessage.U32(payload, 16),
                NotifyIconMessage.U32(payload, 20),
                new Guid(payload.Slice(24, 16)));
            return true;
        }

        public static (int X, int Y) DecodeResult(IntPtr lresult)
        {
            long v = lresult.ToInt64();
            return ((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        }

        /// <summary>MAKELPARAM(x, y), sign-extended like ManagedShell's MakeLParamIntPtr.</summary>
        public static IntPtr EncodeResult(int x, int y) => new((y << 16) | (x & 0xFFFF));

        public override string ToString() =>
            $"{(Message == 1 ? "top-left" : Message == 2 ? "size" : "msg" + Message)} of hwnd=0x{HWnd:X} uid={Uid}{(Guid != Guid.Empty ? " guid=" + Guid : "")}";
    }
}
