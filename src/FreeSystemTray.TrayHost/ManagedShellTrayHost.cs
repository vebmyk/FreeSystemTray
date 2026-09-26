using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FreeSystemTray.TrayHost.Interop;
using FreeSystemTray.TrayHost.Protocol;
using ManagedShell;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using MsNative = ManagedShell.Interop.NativeMethods;

namespace FreeSystemTray.TrayHost
{
    /// <summary>
    /// ITrayHost on ManagedShell's tray service, plus the workarounds FINDINGS.md found necessary
    /// when running next to Explorer. They all live in a subclass of ManagedShell's Shell_TrayWnd:
    /// <list type="bullet">
    /// <item>Mirroring: every icon message ManagedShell handles is sent on to Explorer's tray. A mirror
    /// Explorer rejects becomes a modify (it already had the icon) or a full add built from
    /// ManagedShell's state (it did not, for example after it restarted).</item>
    /// <item>ABM_GETSTATE and ABM_GETAUTOHIDEBAR(EX) are answered by Explorer.</item>
    /// <item>Icon- and taskbar-position queries are answered with our window only for interactions that
    /// start in it; otherwise Explorer answers, so its own tray keeps opening flyouts at the taskbar.</item>
    /// <item>Explorer's taskbar gets back the always-on-top flag ManagedShell takes away.</item>
    /// </list>
    /// It also reads Explorer's own tray now and then, to tell where Explorer shows each icon
    /// (<see cref="ITrayIcon.ExplorerPlacement"/>).
    /// Threading: icon messages are sent messages, so any wait inside ManagedShell's handlers lets
    /// another one run in the middle and ManagedShell drops it (FINDINGS.md, "Other findings" 1).
    /// ManagedShell's collection changes therefore reach our collection through BeginInvoke, and
    /// icons are compared by reference, never with NotifyIcon.Equals.
    /// </summary>
    public sealed class ManagedShellTrayHost : ITrayHost
    {
        private const uint ExplorerTimeoutMs = 1000;

        /// <summary>How often Explorer's tray is read when nothing else asks for it: Explorer moves icons by itself.</summary>
        private const int ExplorerReadEveryTicks = 6; // of the 500 ms monitor

        private readonly TrayHostOptions _options;
        private readonly ShellManager _shell;
        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<ITrayIcon> _icons = new();
        private readonly DispatcherTimer _monitor;
        private NativeMethods.WndProc? _hook;
        private IntPtr _hookPointer;
        private IntPtr _originalWndProc;
        private IntPtr _trayHwnd;
        private bool _started;
        private bool _disposed;
        private bool _syncQueued;
        private PixelRect _trayBounds;
        private PixelRect _overflowBounds;
        private ScreenEdge _trayEdge = ScreenEdge.Bottom;
        private long _lastInteraction;
        private bool? _lastHoldsRole;
        private IntPtr _lastExplorerTray;
        private bool? _lastExplorerTopmost;
        private bool _explorerTopmostRestored;
        private readonly DispatcherTimer _explorerReadDelay;
        private bool _explorerReadRunning;
        private bool _explorerReadAgain;
        private int _ticksSinceExplorerRead;
        private bool? _explorerReadable;

        public ManagedShellTrayHost(TrayHostOptions? options = null)
        {
            _options = options ?? new TrayHostOptions();
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Explorer stays the shell; we only take the tray host role. ManagedShell reads this to
            // decide, among other things, whether to broadcast TaskbarCreated when it is disposed.
            EnvironmentHelper.IsAppRunningAsShell = false;

            ShellConfig config = ShellManager.DefaultShellConfig;
            config.EnableTasksService = false;
            config.AutoStartTasksService = false;
            config.EnableTrayService = true;
            config.AutoStartTrayService = false;
            config.PinnedNotifyIcons = Array.Empty<string>();
            _shell = new ShellManager(config);

            // Without the tasks service, FullScreenHelper polls the foreground window every 100 ms.
            // Nothing here uses it.
            _shell.FullScreenHelper.Dispose();

            Icons = new ReadOnlyObservableCollection<ITrayIcon>(_icons);
            _monitor = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _monitor.Tick += Monitor_Tick;
            // Explorer handles a mirrored message before we get to update our icons; a short pause
            // also lets a burst of icon changes (TaskbarCreated) end in one read.
            _explorerReadDelay = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _explorerReadDelay.Tick += (_, _) =>
            {
                _explorerReadDelay.Stop();
                StartExplorerRead();
            };
        }

        public ReadOnlyObservableCollection<ITrayIcon> Icons { get; }

        public event EventHandler<TrayBalloonEventArgs>? BalloonReceived;

        /// <summary>Our Shell_TrayWnd (diagnostics).</summary>
        public IntPtr TrayWindowHandle => _trayHwnd;

        public bool HoldsTrayRole => _trayHwnd != IntPtr.Zero && ExplorerTaskbar.CurrentHost() == _trayHwnd;

        public bool TrayWindowVisible { get; set; } = true;

        public void Start()
        {
            if (_started) throw new InvalidOperationException("The tray host is already started.");
            _started = true;

            NotificationArea area = _shell.NotificationArea;
            area.TrayIcons.CollectionChanged += TrayIcons_CollectionChanged;
            area.NotificationBalloonShown += Area_NotificationBalloonShown;

            // Initialize() imports Explorer's icons, creates our Shell_TrayWnd above Explorer's and
            // broadcasts TaskbarCreated. Tray messages already reach the window inside it: ManagedShell
            // forwards TaskbarCreated to Explorer with SendMessage, and sent messages run during that
            // wait. So subclass the window the moment it is created, with a CBT hook on this thread.
            HookTrayWindowOnCreation(area.Initialize);
            if (area.IsFailed || area.Handle == IntPtr.Zero)
                throw new InvalidOperationException("ManagedShell could not start the tray service.");
            if (_originalWndProc == IntPtr.Zero)
            {
                // The CBT hook could not be set; subclass now (a few early messages go unmirrored).
                _trayHwnd = area.Handle;
                InstallHook();
            }
            Log($"Tray host started: our Shell_TrayWnd is 0x{_trayHwnd.ToInt64():X}");

            // The hidden window was created after the bounds were first set; move it there now.
            if (!_trayBounds.IsEmpty) SetTrayWindowBounds(_trayBounds, _trayEdge);
            if (_options.KeepExplorerTaskbarTopmost) RestoreExplorerTopmost();
            _monitor.Start();

            // Icons imported from Explorer were added before anyone could observe them.
            SyncIcons();
        }

        public void SetTrayWindowBounds(PixelRect bounds, ScreenEdge edge)
        {
            _trayBounds = bounds;
            _trayEdge = edge;
            if (_disposed) return;
            // Answers ABM_GETTASKBARPOS for our interactions. Once its Shell_TrayWnd exists, ManagedShell
            // also moves that (hidden) window to this rectangle.
            _shell.NotificationArea.SetTrayHostSizeData(new TrayHostSizeData
            {
                edge = (MsNative.ABEdge)(int)edge,
                rc = new MsNative.Rect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
            });
        }

        public void SetOverflowBounds(PixelRect bounds) => _overflowBounds = bounds;

        /// <summary>Called by icons when a mouse button is pressed or released on them in the tray window.</summary>
        internal void NoteInteraction() => _lastInteraction = Stopwatch.GetTimestamp();

        private void Log(string message) => _options.Log?.Invoke(message);

        #region Subclass of our Shell_TrayWnd

        private void HookTrayWindowOnCreation(Action create)
        {
            NativeMethods.HookProc cbt = (code, wParam, lParam) =>
            {
                if (code == NativeMethods.HCBT_CREATEWND && _originalWndProc == IntPtr.Zero && ClassNameOf(wParam) == "Shell_TrayWnd")
                {
                    try
                    {
                        _trayHwnd = wParam;
                        InstallHook();
                    }
                    catch (Exception ex)
                    {
                        _trayHwnd = IntPtr.Zero;
                        Log("Could not subclass the tray window on creation: " + ex.Message);
                    }
                }
                return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            };
            IntPtr hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_CBT, cbt, IntPtr.Zero, NativeMethods.GetCurrentThreadId());
            if (hook == IntPtr.Zero) Log($"SetWindowsHookEx(WH_CBT) failed ({Marshal.GetLastWin32Error()})");
            try
            {
                create();
            }
            finally
            {
                if (hook != IntPtr.Zero) NativeMethods.UnhookWindowsHookEx(hook);
                GC.KeepAlive(cbt);
            }
        }

        private static string ClassNameOf(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(64);
            return NativeMethods.GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        private void InstallHook()
        {
            _hook = TrayWndProc;
            _hookPointer = Marshal.GetFunctionPointerForDelegate(_hook);
            _originalWndProc = NativeMethods.SetWindowLongPtr(_trayHwnd, NativeMethods.GWLP_WNDPROC, _hookPointer);
            if (_originalWndProc == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not subclass the tray window.");
        }

        private void RemoveHook()
        {
            if (_originalWndProc == IntPtr.Zero) return;
            // Only undo it if nobody subclassed the window after us.
            if (NativeMethods.GetWindowLongPtr(_trayHwnd, NativeMethods.GWLP_WNDPROC) == _hookPointer)
                NativeMethods.SetWindowLongPtr(_trayHwnd, NativeMethods.GWLP_WNDPROC, _originalWndProc);
        }

        private IntPtr TrayWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_COPYDATA && lParam != IntPtr.Zero)
            {
                var cds = Marshal.PtrToStructure<NativeMethods.COPYDATASTRUCT>(lParam);
                switch ((long)cds.dwData)
                {
                    case TrayCopyData.NotifyIcon:
                        return OnNotifyIconMessage(hWnd, msg, wParam, lParam, cds);
                    case TrayCopyData.AppBar:
                        if (TryAnswerAppBarMessage(wParam, lParam, cds, out IntPtr appBarResult)) return appBarResult;
                        break;
                    case TrayCopyData.IconRect:
                        if (TryAnswerIconRectQuery(wParam, lParam, cds, out IntPtr rectResult)) return rectResult;
                        break;
                }
            }
            return NativeMethods.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// ManagedShell handles the message first, so it holds the icon's new state if Explorer then
        /// needs a full add. Messages ManagedShell rejected it has already forwarded to Explorer; those
        /// arrive there twice, which add, modify and delete tolerate.
        /// </summary>
        private IntPtr OnNotifyIconMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, NativeMethods.COPYDATASTRUCT cds)
        {
            IntPtr ours = NativeMethods.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
            if (!_options.MirrorToExplorer) return ours;
            try
            {
                byte[] payload = ReadPayload(cds);
                if (!NotifyIconMessage.TryParse(payload, out NotifyIconMessage m)) return ours;
                IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
                if (explorer == IntPtr.Zero)
                {
                    if (_options.TraceMessages) Log($"MIRROR {m}: Explorer's taskbar not found");
                    return ours;
                }

                // Same COPYDATASTRUCT: the app's icon handle is still valid while it waits for us.
                IntPtr? theirs = Send(explorer, wParam, lParam);
                if (_options.TraceMessages) Log($"MIRROR {m} ours={ours} explorer={Describe(theirs)} ({m.Sizes}, wParam=0x{wParam.ToInt64():X}, exe='{NotifyIconMessage.ReadExePath(payload)}')");
                if (_options.TraceMessages && m.Message == Nim.Add) CheckExePathForm(m, payload);
                if (theirs == IntPtr.Zero) FixUpRejectedMirror(explorer, wParam, m, payload);
                else if (theirs == null) Log($"MIRROR {m}: Explorer did not answer within {ExplorerTimeoutMs} ms");
            }
            catch (Exception ex)
            {
                Log("Mirroring an icon message failed: " + ex);
            }
            return ours;
        }

        private void FixUpRejectedMirror(IntPtr explorer, IntPtr wParam, NotifyIconMessage m, byte[] payload)
        {
            NotifyIcon? icon = FindHostIcon(m);
            switch (MirrorPolicy.ForRejected(m.Message, icon != null))
            {
                case MirrorFixup.ResendAsModify:
                {
                    IntPtr? r = SendCopyData(explorer, wParam, TrayCopyData.NotifyIcon, NotifyIconMessage.WithMessage(payload, Nim.Modify));
                    if (_options.TraceMessages) Log($"MIRROR fix-up: Explorer already had {m}; sent as MODIFY -> {Describe(r)}");
                    break;
                }
                case MirrorFixup.SendFullAdd:
                    SendFullAdd(explorer, wParam, m, payload, icon!);
                    break;
            }
        }

        /// <summary>Explorer does not know the icon: add it from ManagedShell's state, then set its version.</summary>
        private void SendFullAdd(IntPtr explorer, IntPtr wParam, NotifyIconMessage m, byte[] payload, NotifyIcon icon)
        {
            IntPtr created = IntPtr.Zero;
            try
            {
                uint hIcon = m.Has(Nif.Icon) ? m.HIcon : 0;
                if (hIcon == 0 && icon.Icon is BitmapSource bitmap)
                {
                    created = IconHandles.CreateFromBitmap(bitmap);
                    hIcon = (uint)created.ToInt64();
                }
                var state = new IconState((uint)icon.HWnd.ToInt64(), icon.UID, icon.GUID, icon.CallbackMessage, icon.Title, icon.IsHidden, icon.Version);
                string? exePath = icon.Path == null ? null : ShellPaths.ToNotifyIconForm(icon.Path);
                byte[]? add = NotifyIconMessage.BuildFullAdd(payload, state, hIcon, exePath);
                if (add == null)
                {
                    Log($"MIRROR fix-up: cannot rebuild {m}; the message is only {payload.Length} bytes");
                    return;
                }
                IntPtr? added = SendCopyData(explorer, wParam, TrayCopyData.NotifyIcon, add);
                IntPtr? versioned = null;
                if (state.Version != 0 && added != IntPtr.Zero && NotifyIconMessage.BuildSetVersion(payload, state) is byte[] setVersion)
                    versioned = SendCopyData(explorer, wParam, TrayCopyData.NotifyIcon, setVersion);
                Log($"MIRROR fix-up: Explorer did not know '{icon.Title}' ({m}); full ADD for {exePath} -> {Describe(added)}" +
                    (versioned != null ? $", SETVERSION {state.Version} -> {Describe(versioned)}" : "") +
                    (created != IntPtr.Zero ? " (icon rebuilt from our image)" : ""));
            }
            finally
            {
                // Explorer copied the icon while it handled the add.
                if (created != IntPtr.Zero) NativeMethods.DestroyIcon(created);
            }
        }

        /// <summary>
        /// A full add must carry the exe path exactly as shell32 writes it (<see cref="ShellPaths"/>).
        /// Real adds show shell32's form: compare ours with it and log any difference.
        /// </summary>
        private void CheckExePathForm(NotifyIconMessage m, byte[] payload)
        {
            string? shell32 = NotifyIconMessage.ReadExePath(payload);
            if (string.IsNullOrEmpty(shell32) || FindHostIcon(m)?.Path is not string path) return;
            string ours = ShellPaths.ToNotifyIconForm(path);
            if (!string.Equals(ours, shell32, StringComparison.OrdinalIgnoreCase))
                Log($"EXE PATH FORM differs for {m}: shell32 '{shell32}', ours '{ours}'");
        }

        private NotifyIcon? FindHostIcon(NotifyIconMessage m)
        {
            foreach (NotifyIcon ni in _shell.NotificationArea.TrayIcons)
            {
                if (m.UsesGuid ? ni.GUID == m.Guid : ni.HWnd == (IntPtr)m.HWnd && ni.UID == m.Uid)
                    return ni;
            }
            return null;
        }

        private bool TryAnswerAppBarMessage(IntPtr wParam, IntPtr lParam, NativeMethods.COPYDATASTRUCT cds, out IntPtr result)
        {
            result = IntPtr.Zero;
            try
            {
                if (cds.cbData != AppBarMessage.Size) return false;
                byte[] payload = ReadPayload(cds);
                if (!AppBarMessage.TryParse(payload, out AppBarMessage m)) return false;
                switch (m.Message)
                {
                    case Abm.GetState:
                    case Abm.GetAutoHideBar:
                    case Abm.GetAutoHideBarEx:
                    {
                        // ManagedShell answers these from its own (empty) list of appbars: "no
                        // auto-hide". Explorer's answer comes back in the return value; no shared memory.
                        if (!_options.ForwardAutoHideQueries) return false;
                        IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
                        if (explorer == IntPtr.Zero) return false;
                        IntPtr? r = Send(explorer, wParam, lParam);
                        if (_options.TraceMessages) Log($"QUERY {m} -> Explorer: {Describe(r)}");
                        if (r is not IntPtr value) return false;
                        result = value;
                        return true;
                    }
                    case Abm.GetTaskbarPos:
                    {
                        if (!_options.RoutePositionQueries) return false;
                        // Explorer asks where its own taskbar is (hWnd = its taskbar) when it lays out
                        // its tray, for example when an icon is added. Answered with our window, the
                        // new icon was accepted but never shown in Explorer's tray.
                        string why = "Explorer asking about its own taskbar";
                        bool ours = !ExplorerTaskbar.IsExplorerTaskbar((IntPtr)m.HWnd, _trayHwnd) && AnswerWithTrayWindow(out why);
                        if (ours)
                        {
                            if (_options.TraceMessages) Log($"QUERY {m} -> tray window {_trayBounds} edge={_trayEdge} ({why})");
                            return false; // ManagedShell answers with the tray window's bounds and edge
                        }
                        IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
                        if (explorer == IntPtr.Zero) return false;
                        bool forwarded = TryForwardTaskbarPos(explorer, wParam, payload, m, out result, out string answer);
                        if (_options.TraceMessages) Log($"QUERY {m} -> Explorer ({why}): {(forwarded ? answer : "failed; tray window answers")}");
                        return forwarded;
                    }
                }
            }
            catch (Exception ex)
            {
                Log("Answering an appbar message failed: " + ex);
            }
            return false;
        }

        /// <summary>
        /// ABM_GETTASKBARPOS answers through a shared-memory block owned by the caller. Explorer does
        /// not read blocks forwarded as they are (ManagedShell's AppBarManager notes the same for
        /// QUERYPOS/SETPOS), so copy it into one allocated for Explorer and copy the answer back.
        /// </summary>
        private static bool TryForwardTaskbarPos(IntPtr explorer, IntPtr wParam, byte[] payload, AppBarMessage m, out IntPtr result, out string answer)
        {
            result = IntPtr.Zero;
            answer = "";
            NativeMethods.GetWindowThreadProcessId(explorer, out uint explorerPid);
            IntPtr callerData = NativeMethods.SHLockShared((IntPtr)m.SharedMemory, m.SourceProcessId);
            if (callerData == IntPtr.Zero) return false;
            try
            {
                IntPtr shared = NativeMethods.SHAllocShared(IntPtr.Zero, AppBarMessage.AppBarDataSize, explorerPid);
                if (shared == IntPtr.Zero) return false;
                try
                {
                    var block = new byte[AppBarMessage.AppBarDataSize];
                    Marshal.Copy(callerData, block, 0, block.Length);
                    if (!CopyToShared(shared, explorerPid, block)) return false;

                    byte[] forwarded = AppBarMessage.WithSharedMemory(payload, shared.ToInt64(), explorerPid);
                    IntPtr? r = SendCopyData(explorer, wParam, TrayCopyData.AppBar, forwarded);
                    if (r is not IntPtr value || value == IntPtr.Zero) return false;

                    IntPtr explorerData = NativeMethods.SHLockShared(shared, explorerPid);
                    if (explorerData == IntPtr.Zero) return false;
                    Marshal.Copy(explorerData, block, 0, block.Length);
                    NativeMethods.SHUnlockShared(explorerData);
                    Marshal.Copy(block, 0, callerData, block.Length);

                    (PixelRect rect, uint edge) = AppBarMessage.ReadAppBarData(block);
                    answer = $"{rect} edge={(ScreenEdge)edge}";
                    result = value;
                    return true;
                }
                finally
                {
                    NativeMethods.SHFreeShared(shared, explorerPid);
                }
            }
            finally
            {
                NativeMethods.SHUnlockShared(callerData);
            }
        }

        private static bool CopyToShared(IntPtr shared, uint processId, byte[] block)
        {
            IntPtr data = NativeMethods.SHLockShared(shared, processId);
            if (data == IntPtr.Zero) return false;
            Marshal.Copy(block, 0, data, block.Length);
            NativeMethods.SHUnlockShared(data);
            return true;
        }

        private bool TryAnswerIconRectQuery(IntPtr wParam, IntPtr lParam, NativeMethods.COPYDATASTRUCT cds, out IntPtr result)
        {
            result = IntPtr.Zero;
            if (!_options.RoutePositionQueries) return false;
            try
            {
                IconRectQuery.TryParse(ReadPayload(cds), out IconRectQuery q);
                if (q.Guid == ExplorerTaskbar.ChevronGuid && TryAnswerChevronQuery(q, out result)) return true;
                bool ours = AnswerWithTrayWindow(out string why);
                if (ours)
                {
                    if (_options.TraceMessages) Log($"QUERY icon {q} -> tray window ({why})");
                    return false; // ManagedShell answers from the icon's placement in our window
                }
                IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
                if (explorer == IntPtr.Zero) return false;
                IntPtr? r = Send(explorer, wParam, lParam);
                bool answered = r is IntPtr v && v != IntPtr.Zero;
                // An icon Explorer does not have gets our position rather than none, unless our window
                // is hidden: then there is no icon of ours to point at, and Explorer's answer stands.
                bool stands = answered || (r != null && !TrayWindowVisible);
                if (_options.TraceMessages)
                {
                    string text = answered ? IconRectQuery.DecodeResult(r!.Value).ToString()
                        : stands ? "no answer, which stands: tray window hidden" : "no answer; tray window answers";
                    Log($"QUERY icon {q} -> Explorer ({why}): {text}");
                }
                if (!stands) return false;
                result = r!.Value;
                return true;
            }
            catch (Exception ex)
            {
                Log("Answering an icon position query failed: " + ex);
                return false;
            }
        }

        /// <summary>
        /// Explorer places its hidden-icons flyout by asking the tray host where its chevron is. It
        /// does not answer that question itself while it is the one asking (the forwarded query
        /// returns 0), and ManagedShell does not know the chevron, so without this the flyout opens
        /// at the top-left corner of the screen.
        /// </summary>
        private bool TryAnswerChevronQuery(IconRectQuery q, out IntPtr result)
        {
            result = IntPtr.Zero;
            IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
            if (explorer == IntPtr.Zero || !ExplorerTaskbar.TryGetChevronRect(explorer, out PixelRect chevron)) return false;
            result = q.Message == 1 ? IconRectQuery.EncodeResult(chevron.Left, chevron.Top) : IconRectQuery.EncodeResult(chevron.Width, chevron.Height);
            if (_options.TraceMessages) Log($"QUERY icon {q} -> Explorer's chevron {chevron}");
            return true;
        }

        private bool AnswerWithTrayWindow(out string why)
        {
            NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
            var cursor = new PixelPoint(pt.X, pt.Y);
            bool overExplorer = ExplorerTaskbar.IsOverExplorerTray(cursor, _trayHwnd);
            TimeSpan? since = _lastInteraction == 0 ? null : Stopwatch.GetElapsedTime(_lastInteraction);
            bool ours = PositionQueryRouting.AnswerWithTrayWindow(TrayWindowVisible, _trayBounds, _overflowBounds, cursor, overExplorer, since);
            why = $"cursor {cursor.X},{cursor.Y}{(overExplorer ? " over Explorer's tray" : "")}, last tray input " +
                  (since is TimeSpan t ? $"{t.TotalMilliseconds:0} ms ago" : "never");
            return ours;
        }

        private static byte[] ReadPayload(NativeMethods.COPYDATASTRUCT cds)
        {
            if (cds.lpData == IntPtr.Zero || cds.cbData <= 0) return Array.Empty<byte>();
            var data = new byte[cds.cbData];
            Marshal.Copy(cds.lpData, data, 0, cds.cbData);
            return data;
        }

        /// <summary>SendMessageTimeout of WM_COPYDATA. Null if Explorer did not answer (hung or timed out).</summary>
        private static IntPtr? Send(IntPtr target, IntPtr wParam, IntPtr copyDataStruct) =>
            NativeMethods.SendMessageTimeout(target, NativeMethods.WM_COPYDATA, wParam, copyDataStruct,
                NativeMethods.SMTO_ABORTIFHUNG, ExplorerTimeoutMs, out IntPtr result) == IntPtr.Zero ? null : result;

        private static IntPtr? SendCopyData(IntPtr target, IntPtr wParam, long dwData, byte[] payload)
        {
            IntPtr data = Marshal.AllocHGlobal(payload.Length);
            IntPtr cds = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.COPYDATASTRUCT>());
            try
            {
                Marshal.Copy(payload, 0, data, payload.Length);
                Marshal.StructureToPtr(new NativeMethods.COPYDATASTRUCT { dwData = (IntPtr)dwData, cbData = payload.Length, lpData = data }, cds, false);
                return Send(target, wParam, cds);
            }
            finally
            {
                Marshal.FreeHGlobal(cds);
                Marshal.FreeHGlobal(data);
            }
        }

        private static string Describe(IntPtr? result) => result is IntPtr v ? v.ToInt64().ToString() : "timeout";

        #endregion

        #region Explorer's taskbar

        /// <summary>
        /// ManagedShell pushes Explorer's taskbar to the bottom of the Z-order when it takes the role,
        /// which removes its always-on-top flag for good. Undo that, once. Explorer itself drops the flag
        /// while a full-screen app is in front and restores it afterwards, so it is not enforced later.
        /// </summary>
        private void RestoreExplorerTopmost()
        {
            IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
            if (explorer == IntPtr.Zero) return;
            if (ExplorerTaskbar.IsTopmost(explorer))
            {
                _explorerTopmostRestored = true;
                return;
            }
            _explorerTopmostRestored = ExplorerTaskbar.MakeTopmostBelow(explorer, _trayHwnd);
            Log($"Explorer's taskbar 0x{explorer.ToInt64():X} made always-on-top again after ManagedShell demoted it: " +
                $"{(_explorerTopmostRestored ? "done" : "FAILED")}, we hold the role: {HoldsTrayRole}");
        }

        private void Monitor_Tick(object? sender, EventArgs e)
        {
            bool holds = HoldsTrayRole;
            if (_lastHoldsRole != holds)
            {
                Log(holds ? "Holding the tray host role" : $"Lost the tray host role to 0x{ExplorerTaskbar.CurrentHost().ToInt64():X}");
                _lastHoldsRole = holds;
            }

            IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
            if (explorer != _lastExplorerTray)
            {
                Log(explorer == IntPtr.Zero ? "Explorer's taskbar is gone" : $"Explorer's taskbar is 0x{explorer.ToInt64():X}");
                _lastExplorerTray = explorer;
            }

            bool? topmost = explorer == IntPtr.Zero ? null : ExplorerTaskbar.IsTopmost(explorer);
            if (topmost != _lastExplorerTopmost)
            {
                if (topmost == true) Log("Explorer's taskbar is always-on-top");
                else if (topmost == false) Log("Explorer's taskbar is not always-on-top (Explorer does this while a full-screen app is in front)");
                _lastExplorerTopmost = topmost;
            }

            if (++_ticksSinceExplorerRead >= ExplorerReadEveryTicks) StartExplorerRead();
        }

        #endregion

        #region Explorer's own tray: where it shows each icon

        /// <summary>Reads Explorer's tray soon. Never waits: it can be called while ManagedShell handles a tray message.</summary>
        internal void RequestExplorerRead()
        {
            if (!_options.ReadExplorerTray || _disposed) return;
            _explorerReadDelay.Stop();
            _explorerReadDelay.Start();
        }

        /// <summary>
        /// Reading Explorer's tray sends messages to Explorer and waits for them, so it runs on a
        /// thread-pool thread; tray messages would otherwise run in the middle of the wait.
        /// </summary>
        private void StartExplorerRead()
        {
            if (!_options.ReadExplorerTray || _disposed || !_started) return;
            _ticksSinceExplorerRead = 0;
            if (_explorerReadRunning)
            {
                _explorerReadAgain = true;
                return;
            }
            _explorerReadRunning = true;
            IntPtr explorer = ExplorerTaskbar.Find(_trayHwnd);
            Task.Run(() =>
            {
                List<ExplorerTrayButton>? buttons = null;
                try
                {
                    buttons = ExplorerTrayIcons.Read(explorer);
                }
                catch (Exception ex)
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, () => Log("Reading Explorer's tray failed: " + ex.Message));
                }
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () => ApplyExplorerRead(buttons));
            });
        }

        private void ApplyExplorerRead(List<ExplorerTrayButton>? buttons)
        {
            _explorerReadRunning = false;
            if (_disposed) return;
            if (_explorerReadable != (buttons != null))
            {
                _explorerReadable = buttons != null;
                Log(buttons != null ? $"Reading Explorer's tray: {buttons.Count} buttons, {buttons.Count(b => b.Hidden)} of them hidden"
                    : "Explorer's tray cannot be read; where it shows icons is unknown");
            }
            foreach (ManagedShellTrayIcon icon in _icons.OfType<ManagedShellTrayIcon>())
            {
                NotifyIcon ni = icon.Source;
                ExplorerPlacement placement = buttons == null ? ExplorerPlacement.Unknown : ExplorerTrayIcons.PlacementOf(buttons, ni.HWnd, ni.UID, ni.GUID);
                if (placement != icon.ExplorerPlacement && _options.TraceMessages)
                    Log($"EXPLORER shows '{ni.Title}' ({icon.Id}): {placement}");
                icon.SetExplorerPlacement(placement);
            }
            if (_explorerReadAgain)
            {
                _explorerReadAgain = false;
                StartExplorerRead();
            }
        }

        #endregion

        #region ManagedShell's icon collection -> ours

        private void TrayIcons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Runs inside ManagedShell's WndProc: queue the work, never wait here.
            if (_syncQueued) return;
            _syncQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, SyncIcons);
        }

        /// <summary>Brings our collection in line with ManagedShell's, in the same order.</summary>
        private void SyncIcons()
        {
            _syncQueued = false;
            if (_disposed) return;

            // A snapshot: updating our collection runs UI code, and anything in it that waits lets a
            // tray message change ManagedShell's collection while we iterate. That queues another sync.
            List<NotifyIcon> source = _shell.NotificationArea.TrayIcons.ToList();

            for (int i = _icons.Count - 1; i >= 0; i--)
            {
                var icon = (ManagedShellTrayIcon)_icons[i];
                if (!source.Any(s => ReferenceEquals(s, icon.Source)))
                {
                    icon.Detach();
                    _icons.RemoveAt(i);
                }
            }

            for (int i = 0; i < source.Count; i++)
            {
                int at = IndexOf(source[i]);
                if (at == i) continue;
                if (at >= 0) _icons.Move(at, i);
                else _icons.Insert(i, new ManagedShellTrayIcon(source[i], this));
            }
            RequestExplorerRead();
        }

        private int IndexOf(NotifyIcon ni)
        {
            for (int i = 0; i < _icons.Count; i++)
            {
                if (ReferenceEquals(((ManagedShellTrayIcon)_icons[i]).Source, ni)) return i;
            }
            return -1;
        }

        private void Area_NotificationBalloonShown(object? sender, NotificationBalloonEventArgs e)
        {
            // Windows shows the balloon as a toast through the mirrored message. Handled stops
            // ManagedShell from keeping every balloon in NotifyIcon.MissedNotifications.
            e.Handled = _options.MirrorToExplorer;
            NotificationBalloon balloon = e.Balloon;
            _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                ITrayIcon? icon = _icons.OfType<ManagedShellTrayIcon>().FirstOrDefault(i => ReferenceEquals(i.Source, balloon.NotifyIcon));
                BalloonReceived?.Invoke(this, new TrayBalloonEventArgs(icon, balloon.Title ?? "", balloon.Info ?? ""));
            });
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _monitor.Stop();
            _explorerReadDelay.Stop();

            if (_started)
            {
                _shell.NotificationArea.TrayIcons.CollectionChanged -= TrayIcons_CollectionChanged;
                _shell.NotificationArea.NotificationBalloonShown -= Area_NotificationBalloonShown;
                RemoveHook();
            }
            foreach (ManagedShellTrayIcon icon in _icons.OfType<ManagedShellTrayIcon>()) icon.Detach();

            try
            {
                // Destroys our Shell_TrayWnd and broadcasts TaskbarCreated, so apps re-register with Explorer.
                _shell.Dispose();
            }
            catch (Exception ex)
            {
                Log("Disposing ManagedShell failed: " + ex);
            }

            // Only if the fix at start did not take: otherwise a missing flag is Explorer's own doing.
            if (_started && _options.KeepExplorerTaskbarTopmost && !_explorerTopmostRestored)
                Log("On exit: " + TrayHandBack.RestoreExplorerTopmost());
            Log("Tray host stopped");
        }
    }
}
