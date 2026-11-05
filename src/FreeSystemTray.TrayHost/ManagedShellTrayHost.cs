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
    /// (<see cref="ITrayIcon.ExplorerPlacement"/>), and to keep ManagedShell and Explorer's tray in
    /// step where mirroring cannot (Phase 6): it imports Explorer's icons at start without ManagedShell's
    /// import (which reverses Explorer's hidden-icons order), raises our window as soon as Explorer's
    /// taskbar goes above it (<see cref="HostRoleKeeper"/>) and then catches up on what went only to
    /// Explorer meanwhile (<see cref="GapSync"/>), gives a restarted Explorer the icons it lacks, and
    /// removes icons whose window no longer exists from both trays.
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

        private HostRoleKeeper? _keeper;
        private List<ExplorerTrayButton>? _pendingImport;
        private bool _monitorStarted;

        /// <summary>The last two completed reads of Explorer's tray, newest first.</summary>
        private ExplorerRead? _lastRead, _previousRead;

        /// <summary>Gaps not caught up on yet: a read from before the first, and when the last ended.</summary>
        private bool _gapPending;
        private ExplorerRead? _gapBaseline;
        private long _gapEndedAt;

        /// <summary>Explorer's taskbar is new (Explorer restarted): give it our icons it lacks, from this time on.</summary>
        private IntPtr _recoverFor;
        private long _recoverAt;
        private int _recoverAttempts;

        /// <summary>
        /// Per icon, whether its app's last NIM_ADD or NIM_MODIFY had NIF_SHOWTIP (Explorer takes the flag
        /// from each of them; FINDINGS.md, Phase 6). Unknown for icons imported from Explorer's tray.
        /// </summary>
        private readonly Dictionary<NotifyIcon, bool> _showTip = new(ReferenceEqualityComparer.Instance);

        /// <summary>GUID icons whose window no longer exists, and since when (they get a grace period).</summary>
        private readonly Dictionary<NotifyIcon, long> _deadSince = new(ReferenceEqualityComparer.Instance);

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

            // ManagedShell's import switches Explorer's "hide inactive icons" off and on to reach the
            // hidden icons, which reverses the order of Explorer's hidden-icons flyout (FINDINGS.md,
            // Phase 0). Read Explorer's three toolbars instead, now, while we are not the host yet (so
            // waiting for Explorer is safe); the icons are handed to ManagedShell during Initialize().
            if (_options.ImportWithoutToggling) _pendingImport = ExplorerTrayIcons.Read(ExplorerTaskbar.Find(IntPtr.Zero));

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
            if (_pendingImport is List<ExplorerTrayButton> late)
            {
                // ManagedShell did not create the child window the import waits for: import now, after
                // whatever apps have sent since, without overwriting it.
                _pendingImport = null;
                ImportFromExplorer(late, skipKnown: true);
            }

            // The hidden window was created after the bounds were first set; move it there now.
            if (!_trayBounds.IsEmpty) SetTrayWindowBounds(_trayBounds, _trayEdge);
            if (_options.KeepExplorerTaskbarTopmost) RestoreExplorerTopmost();
            if (_options.KeepHostRole)
            {
                _keeper = new HostRoleKeeper(_trayHwnd,
                    gap => _dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnRoleGap(gap)),
                    message => _dispatcher.BeginInvoke(DispatcherPriority.Background, () => Log(message)));
            }
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

        /// <summary>
        /// Runs ManagedShell's Initialize() with a CBT hook that subclasses our Shell_TrayWnd as it is
        /// created. With our own import pending, ManagedShell's import is kept from running: it only
        /// runs while IsAppRunningAsShell is false, and that flag is set back the moment our window is
        /// created, before anything else in Initialize() reads it (ManagedShell's shell service objects
        /// would load into this process if it stayed set). The import is handed to ManagedShell when it
        /// creates its TrayNotifyWnd child, right after our window: by then ManagedShell knows its
        /// window, so what it rejects goes on to Explorer (before, it could forward to itself), and it
        /// has not yet broadcast TaskbarCreated, so the imported icons come first, in Explorer's order.
        /// </summary>
        private void HookTrayWindowOnCreation(Action create)
        {
            NativeMethods.HookProc cbt = (code, wParam, lParam) =>
            {
                if (code == NativeMethods.HCBT_CREATEWND)
                {
                    string cls = ClassNameOf(wParam);
                    if (cls == "Shell_TrayWnd" && _originalWndProc == IntPtr.Zero)
                    {
                        EnvironmentHelper.IsAppRunningAsShell = false;
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
                    else if (cls == "TrayNotifyWnd" && _originalWndProc != IntPtr.Zero && _pendingImport is List<ExplorerTrayButton> import)
                    {
                        _pendingImport = null;
                        ImportFromExplorer(import, skipKnown: false);
                    }
                }
                return NativeMethods.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
            };
            IntPtr hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_CBT, cbt, IntPtr.Zero, NativeMethods.GetCurrentThreadId());
            if (hook == IntPtr.Zero)
            {
                Log($"SetWindowsHookEx(WH_CBT) failed ({Marshal.GetLastWin32Error()}); ManagedShell imports Explorer's icons itself");
                _pendingImport = null;
            }
            else if (_pendingImport != null)
            {
                EnvironmentHelper.IsAppRunningAsShell = true;
            }
            try
            {
                create();
            }
            finally
            {
                EnvironmentHelper.IsAppRunningAsShell = false;
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
            try
            {
                byte[] payload = ReadPayload(cds);
                if (!NotifyIconMessage.TryParse(payload, out NotifyIconMessage m)) return ours;
                if (m.Message is Nim.Add or Nim.Modify or Nim.SetVersion) NoteToolTipFlags(m);
                if (!_options.MirrorToExplorer) return ours;
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
                // A new taskbar after we started: Explorer restarted, or started after us. Apps re-register
                // when it broadcasts TaskbarCreated (with us, and mirrored to it); once that has settled,
                // it gets the icons it still lacks from us, such as those of apps that do not re-register.
                if (explorer != IntPtr.Zero && _monitorStarted && _options.SyncWithExplorer && _options.MirrorToExplorer && _options.ReadExplorerTray)
                {
                    _recoverFor = explorer;
                    _recoverAt = Stopwatch.GetTimestamp() + Stopwatch.Frequency * RecoverAfterSeconds;
                    _recoverAttempts = 0;
                    Log($"Explorer's taskbar is new (Explorer restarted?); in {RecoverAfterSeconds} s its tray gets our icons it still lacks");
                }
                _lastExplorerTray = explorer;
            }
            _monitorStarted = true;
            if (_recoverAt != 0 && Stopwatch.GetTimestamp() >= _recoverAt)
            {
                _recoverAt = 0;
                StartExplorerRead();
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
            bool removeDead = _options.SyncWithExplorer && _options.MirrorToExplorer;
            Task.Run(() =>
            {
                long started = Stopwatch.GetTimestamp();
                List<ExplorerTrayButton>? buttons = null;
                try
                {
                    buttons = ExplorerTrayIcons.Read(explorer);
                }
                catch (Exception ex)
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, () => Log("Reading Explorer's tray failed: " + ex.Message));
                }
                var read = new ExplorerRead(explorer, buttons, started, Stopwatch.GetTimestamp());
                string? removed = null;
                try
                {
                    if (removeDead && buttons != null) removed = RemoveDeadIconsFromExplorer(explorer, buttons);
                }
                catch (Exception ex)
                {
                    removed = "Removing dead icons from Explorer's tray failed: " + ex.Message;
                }
                // Always applied: it clears _explorerReadRunning, without which no read would start again.
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (removed != null) Log(removed);
                    ApplyExplorerRead(read);
                });
            });
        }

        private void ApplyExplorerRead(ExplorerRead read)
        {
            _explorerReadRunning = false;
            if (_disposed) return;
            List<ExplorerTrayButton>? buttons = read.Buttons;
            _previousRead = _lastRead;
            _lastRead = read;
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
            if (_options.SyncWithExplorer)
            {
                if (_gapPending && read.StartedAt > _gapEndedAt && buttons != null)
                {
                    _gapPending = false;
                    CatchUpAfterGap(_gapBaseline, read);
                    _gapBaseline = null;
                }
                if (_recoverFor != IntPtr.Zero && _recoverAt == 0 && read.Explorer == _recoverFor)
                {
                    if (buttons != null) GiveExplorerMissingIcons(read);
                    if (buttons != null || ++_recoverAttempts >= 5) _recoverFor = IntPtr.Zero;
                }
                RemoveDeadIcons();
            }
            if (_explorerReadAgain)
            {
                _explorerReadAgain = false;
                StartExplorerRead();
            }
        }

        #endregion

        #region Keeping ManagedShell and Explorer's tray in step

        /// <summary>Explorer's taskbar has this long to collect the apps' icons after it (re)starts, before it gets ours.</summary>
        private const int RecoverAfterSeconds = 4;

        /// <summary>How long a GUID icon whose window no longer exists is kept, in case it comes back.</summary>
        private static readonly TimeSpan DeadGuidIconGrace = TimeSpan.FromSeconds(10);

        /// <summary>A read of Explorer's tray: which taskbar, its buttons (null if it could not be read), and when.</summary>
        private sealed record ExplorerRead(IntPtr Explorer, List<ExplorerTrayButton>? Buttons, long StartedAt, long EndedAt);

        /// <summary>
        /// Hands Explorer's icons to ManagedShell as the NIM_ADDs its own import would send, straight to
        /// ManagedShell's window procedure: nothing is mirrored (Explorer has them already).
        /// </summary>
        /// <param name="skipKnown">Leave out icons ManagedShell has already: their apps have told it more recently.</param>
        private void ImportFromExplorer(List<ExplorerTrayButton> buttons, bool skipKnown)
        {
            int imported = 0, skipped = 0;
            foreach (ExplorerTrayButton b in ExplorerTrayIcons.OnePerIcon(buttons))
            {
                // Explorer keeps icons of windows that no longer exist until the pointer passes over them.
                if (b.Window == IntPtr.Zero || !NativeMethods.IsWindow(b.Window) || (skipKnown && FindHostIcon(b) != null))
                {
                    skipped++;
                    continue;
                }
                if (CallManagedShell(Nim.Add, b, Nif.Message | Nif.Tip | Nif.Icon | Nif.State)) imported++;
                if (b.Version != 0) CallManagedShell(Nim.SetVersion, b, 0);
            }
            Log($"Imported {imported} icons from Explorer's tray ({skipped} left out), reading its hidden icons directly");
        }

        /// <summary>
        /// Sends ManagedShell a message built from Explorer's copy of an icon, through its window
        /// procedure but not our subclass: nothing is mirrored to Explorer. True if ManagedShell took it.
        /// </summary>
        private bool CallManagedShell(uint nim, ExplorerTrayButton b, uint flags)
        {
            var state = new IconState((uint)b.Window.ToInt64(), b.Uid, b.Guid, b.CallbackMessage, b.Tip, b.StateHidden, b.Version);
            byte[] payload = NotifyIconMessage.Create(nim, state, flags, (uint)b.Icon.ToInt64());
            IntPtr data = Marshal.AllocHGlobal(payload.Length);
            IntPtr cds = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.COPYDATASTRUCT>());
            try
            {
                Marshal.Copy(payload, 0, data, payload.Length);
                Marshal.StructureToPtr(new NativeMethods.COPYDATASTRUCT { dwData = (IntPtr)TrayCopyData.NotifyIcon, cbData = payload.Length, lpData = data }, cds, false);
                return NativeMethods.CallWindowProc(_originalWndProc, _trayHwnd, NativeMethods.WM_COPYDATA, _trayHwnd, cds) != IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(cds);
                Marshal.FreeHGlobal(data);
            }
        }

        /// <summary>The icon ManagedShell holds for Explorer's button, matched the way ManagedShell does (window and uID, or GUID).</summary>
        private NotifyIcon? FindHostIcon(ExplorerTrayButton b)
        {
            foreach (NotifyIcon ni in _shell.NotificationArea.TrayIcons)
            {
                if ((ni.HWnd == b.Window && ni.UID == b.Uid) || (b.Guid != Guid.Empty && ni.GUID == b.Guid)) return ni;
            }
            return null;
        }

        /// <summary>
        /// Explorer's taskbar was above ours for a moment. Messages sent meanwhile reached Explorer only:
        /// compare its tray, read once the gap is over, with a read from before it.
        /// </summary>
        private void OnRoleGap(RoleGap gap)
        {
            if (_disposed) return;
            if (_options.TraceMessages || gap.Duration > TimeSpan.FromSeconds(1))
                Log($"Explorer's taskbar held the tray host role for {gap.Duration.TotalMilliseconds:0} ms; ours is the highest again ({gap.Raises} raise{(gap.Raises == 1 ? "" : "s")})");
            if (!_options.SyncWithExplorer || !_options.ReadExplorerTray) return;
            if (!_gapPending)
            {
                // The newest read that was over before the gap began: later ones may show part of it.
                _gapBaseline = _lastRead is { } last && last.EndedAt < gap.LostAt ? last
                    : _previousRead is { } previous && previous.EndedAt < gap.LostAt ? previous : null;
                _gapPending = true;
            }
            _gapEndedAt = gap.RegainedAt;
            StartExplorerRead();
        }

        private void CatchUpAfterGap(ExplorerRead? before, ExplorerRead after)
        {
            bool sameExplorer = before != null && before.Explorer == after.Explorer;
            List<SyncAction> actions = GapSync.AfterGap(before?.Buttons, after.Buttons!, sameExplorer,
                b => FindHostIcon(b) != null, NativeMethods.IsWindow);
            var done = new List<string>();
            foreach (SyncAction a in actions)
            {
                ExplorerTrayButton b = a.Button;
                bool ok;
                switch (a.Kind)
                {
                    case SyncKind.AddToHost:
                        ok = CallManagedShell(Nim.Add, b, Nif.Message | Nif.Tip | Nif.Icon | Nif.State);
                        if (ok && b.Version != 0) CallManagedShell(Nim.SetVersion, b, 0);
                        break;
                    case SyncKind.UpdateHost:
                        ok = a.Changed == 0 || CallManagedShell(Nim.Modify, b, a.Changed);
                        if (a.VersionChanged) ok &= CallManagedShell(Nim.SetVersion, b, 0);
                        break;
                    default:
                        ok = CallManagedShell(Nim.Delete, b, 0);
                        break;
                }
                done.Add($"{a.Kind} '{b.Tip}'{(a.Kind == SyncKind.UpdateHost ? $" (nif=0x{a.Changed:X}{(a.VersionChanged ? ", version" : "")})" : "")}{(ok ? "" : " FAILED")}");
            }
            if (done.Count > 0 || _options.TraceMessages)
                Log($"Caught up with Explorer's tray after its taskbar held the role{(before == null ? " (no read from before)" : sameExplorer ? "" : " (Explorer restarted)")}: " +
                    (done.Count > 0 ? string.Join("; ", done) : "nothing had changed"));
        }

        /// <summary>
        /// After Explorer restarted: its new tray gets every icon of ours it lacks, as a full add built
        /// from our state (as the mirroring fix-up does), so it has them after we exit or crash. Apps that
        /// re-registered after TaskbarCreated are there already.
        /// </summary>
        private void GiveExplorerMissingIcons(ExplorerRead read)
        {
            var adds = new List<(string Title, byte[] Add, byte[]? Version)>();
            var created = new List<IntPtr>();
            foreach (NotifyIcon ni in _shell.NotificationArea.TrayIcons.ToList())
            {
                if (!NativeMethods.IsWindow(ni.HWnd)) continue;
                var identity = new ExplorerTrayButton(ni.HWnd, ni.UID, ni.GUID, ExplorerPlacement.Unknown, false);
                if (ExplorerTrayIcons.Find(read.Buttons!, identity) != null) continue;
                IntPtr hIcon = ni.Icon is BitmapSource bitmap ? IconHandles.CreateFromBitmap(bitmap) : IntPtr.Zero;
                if (hIcon != IntPtr.Zero) created.Add(hIcon);
                var state = new IconState((uint)ni.HWnd.ToInt64(), ni.UID, ni.GUID, ni.CallbackMessage, ni.Title, ni.IsHidden, ni.Version);
                uint flags = Nif.Message | Nif.Tip | Nif.Icon | (ni.IsHidden ? Nif.State : 0) | (_showTip.TryGetValue(ni, out bool tip) && tip ? Nif.ShowTip : 0);
                string? exe = ni.Path == null ? null : ShellPaths.ToNotifyIconForm(ni.Path);
                adds.Add((ni.Title ?? "", NotifyIconMessage.Create(Nim.Add, state, flags, (uint)hIcon.ToInt64(), exe),
                    ni.Version != 0 ? NotifyIconMessage.Create(Nim.SetVersion, state, 0) : null));
            }
            if (adds.Count == 0)
            {
                Log($"Explorer's new tray has all {_shell.NotificationArea.TrayIcons.Count} of our icons");
                return;
            }
            IntPtr explorer = read.Explorer, ours = _trayHwnd;
            // Explorer's answers are waited for on a thread-pool thread, not here.
            Task.Run(() =>
            {
                string outcome;
                try
                {
                    var results = adds.Select(a =>
                    {
                        IntPtr? r = SendCopyData(explorer, ours, TrayCopyData.NotifyIcon, a.Add);
                        if (a.Version != null && r is IntPtr v && v != IntPtr.Zero) SendCopyData(explorer, ours, TrayCopyData.NotifyIcon, a.Version);
                        return $"'{a.Title}' -> {Describe(r)}";
                    }).ToList();
                    outcome = $"Gave Explorer's new tray {adds.Count} icon(s) it lacked: {string.Join("; ", results)}";
                }
                catch (Exception ex)
                {
                    outcome = $"Giving Explorer's new tray {adds.Count} icon(s) it lacked failed: {ex.Message}";
                }
                // The icon handles are destroyed whatever happened.
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    foreach (IntPtr h in created) NativeMethods.DestroyIcon(h);
                    Log(outcome);
                });
            });
        }

        /// <summary>
        /// Removes our icons whose window no longer exists (the app crashed or was killed without
        /// deleting its icon). ManagedShell does this only when the pointer passes over one, as Explorer
        /// does. An icon with a GUID gets a grace period: when its app starts again, or Explorer does (its
        /// own icons: volume, network, ...), it registers the same GUID from a new window, and the icon is
        /// updated where it is instead of coming back at the end.
        /// </summary>
        private void RemoveDeadIcons()
        {
            long now = Stopwatch.GetTimestamp();
            var dead = new List<NotifyIcon>();
            foreach (NotifyIcon ni in _shell.NotificationArea.TrayIcons)
            {
                if (NativeMethods.IsWindow(ni.HWnd))
                {
                    _deadSince.Remove(ni);
                    continue;
                }
                if (ni.GUID != Guid.Empty)
                {
                    if (!_deadSince.TryGetValue(ni, out long since)) _deadSince[ni] = since = now;
                    if (Stopwatch.GetElapsedTime(since, now) < DeadGuidIconGrace) continue;
                }
                dead.Add(ni);
            }
            if (dead.Count == 0) return;
            foreach (NotifyIcon ni in dead)
            {
                _deadSince.Remove(ni);
                _shell.NotificationArea.TrayIcons.Remove(ni);
            }
            Log($"Removed {dead.Count} icon(s) whose window no longer exists: {string.Join(", ", dead.Select(ni => $"'{ni.Title}'"))}");
        }

        /// <summary>
        /// Deletes the buttons of windows that no longer exist from Explorer's tray, which keeps them
        /// until the pointer passes over them. Mirroring put most of them there. Thread-pool thread.
        /// </summary>
        private static string? RemoveDeadIconsFromExplorer(IntPtr explorer, List<ExplorerTrayButton> buttons)
        {
            var removed = new List<string>();
            foreach (ExplorerTrayButton b in ExplorerTrayIcons.OnePerIcon(buttons))
            {
                if (b.Window == IntPtr.Zero || NativeMethods.IsWindow(b.Window)) continue;
                var state = new IconState((uint)b.Window.ToInt64(), b.Uid, b.Guid, 0, null, false, 0);
                IntPtr? r = SendCopyData(explorer, IntPtr.Zero, TrayCopyData.NotifyIcon, NotifyIconMessage.Create(Nim.Delete, state, 0));
                removed.Add($"'{b.Tip}' -> {Describe(r)}");
            }
            return removed.Count == 0 ? null : $"Removed {removed.Count} icon(s) of windows that no longer exist from Explorer's tray: {string.Join("; ", removed)}";
        }

        /// <summary>Whether the app's last add or modify asked for the standard tooltip; null while unknown.</summary>
        internal bool? ShowTipOf(NotifyIcon icon) => _showTip.TryGetValue(icon, out bool value) ? value : null;

        /// <summary>
        /// Explorer takes NIF_SHOWTIP from every NIM_ADD and NIM_MODIFY (not only from those that set the
        /// tooltip), and it matters from version 4 on, which NIM_SETVERSION sets. Runs inside the tray
        /// message: it only notes the flag and tells the icon, which raises a property change.
        /// </summary>
        private void NoteToolTipFlags(NotifyIconMessage m)
        {
            NotifyIcon? icon = FindHostIcon(m);
            if (icon == null) return;
            if (m.Message != Nim.SetVersion) _showTip[icon] = m.Has(Nif.ShowTip);
            foreach (ManagedShellTrayIcon ours in _icons.OfType<ManagedShellTrayIcon>())
            {
                if (ReferenceEquals(ours.Source, icon)) ours.RefreshToolTipMode();
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
                    _showTip.Remove(icon.Source);
                    _deadSince.Remove(icon.Source);
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
            _keeper?.Dispose();

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
