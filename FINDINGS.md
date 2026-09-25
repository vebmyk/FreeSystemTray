# Phase 0 findings: tray host feasibility

Date: 2026-09-25. Tested on Windows 10 only (no Windows 11 machine available).

| | |
|---|---|
| OS | Windows 10 Pro 22H2, build 19045.7725, Norwegian UI |
| Hardware | Laptop, one 1920×1080 monitor at 100% (96 DPI) |
| Session | Normal user, not elevated. Explorer is the shell, with the Open-Shell start button. "Always show all icons" is off (`EnableAutoTray=1`). |
| Library | ManagedShell 0.0.374 from NuGet, the same commit as GitHub `master` (`8552220`, 2026-09-17), on .NET 10 |
| Method | Spike app plus a logging test tray app plus a harness that simulates mouse input and reads Explorer's tray. See [spike/README.md](spike/README.md). Logs are in `spike/phase0-logs/`. |

## Verdict (decision gate)

**The core behaviour is reliable on Windows 10. Handing icons back to Explorer is not reliable with ManagedShell as-is.** A fix for the hand-back problem worked in the spike. My recommendation is to go ahead with Phase 1 on Windows 10, with the changes to `ITrayHost` listed under [Recommended changes to the plan](#recommended-changes-to-the-plan). The decision is yours.

What works:
- Taking the tray host role.
- Getting all icons, including volume, network and power.
- Left, right and double clicks, context menus and tooltips, for every app tested.
- System flyouts open next to our window.
- Surviving an Explorer restart.

What does not work with ManagedShell as shipped:
- Explorer's own tray goes stale while we host. After an Explorer restart it is empty.
- Balloon notifications disappear.
- Explorer's taskbar loses its always-on-top flag.
- If Explorer restarted while we were hosting, Explorer's own volume, network and power icons do not come back when we exit.
- A re-entrancy trap can silently drop icon messages.

All of these were fixed or worked around in the spike, except the gaps noted below.

## ManagedShell and RetroBar maintenance

| Repo | Last push | Stars | Open issues | License |
|---|---|---|---|---|
| cairoshell/ManagedShell | 2026-09-17 (several commits per month) | 415 | 10 | **Apache-2.0** (the plan says MIT) |
| dremin/RetroBar | 2026-09-09 (still uses ManagedShell 0.0.358) | 4.4k | 479 | Apache-2.0 |
| cairoshell/cairoshell | 2026-09-09 | 3.4k | 245 | Apache-2.0 |

ManagedShell targets net480, net6.0-windows and net10.0-windows. It publishes to NuGet on every merge (0.0.x versions) and has no release tags. Apache-2.0 is permissive, but when we redistribute we must ship the license and keep its NOTICE and attributions.

## How ManagedShell takes the tray

Read from the source and confirmed in the logs.

1. **Takes the role.** It creates its own hidden, topmost `Shell_TrayWnd`. It pushes Explorer's taskbar to the bottom of the Z-order, which removes Explorer's always-on-top flag. A 100 ms timer checks `FindWindow("Shell_TrayWnd")` and raises its window again whenever Explorer's is found first. `Shell_NotifyIcon` sends to whichever window `FindWindow` returns.
2. **Imports existing icons.** It reads Explorer's tray toolbar out of Explorer's memory (`ReadProcessMemory`). To reach hidden icons it briefly switches off "hide inactive icons" through the undocumented `ITrayNotify` COM interface.
3. **Asks apps to re-register.** It broadcasts `TaskbarCreated` so running apps register their icons again.
4. **Does not forward what it handles.** Icon messages that ManagedShell handles are not forwarded to Explorer. Only the ones it rejects are forwarded. This is why Explorer's tray goes stale.
5. **Answers some taskbar queries itself.** `ABM_GETTASKBARPOS`, `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR` are answered from its own state, not Explorer's. Other appbar messages are forwarded. Seen during testing: 25 `GETTASKBARPOS` and 5 `GETSTATE` handled, 8 `ABM_NEW` forwarded.
6. **On exit.** Graceful exit (`Dispose`) destroys the window and broadcasts `TaskbarCreated`. After a crash, nothing is sent.

## Results on Windows 10 22H2

| Check | Result |
|---|---|
| Take the tray host role with Explorer running | ✅ Every run. |
| Icons from third-party apps appear | ✅ All 15 visible icons came through. Explorer also held a stale Google Drive entry whose window no longer existed; the spike skipped it, and Google Drive re-registered with a live window. Meet Now, which Explorer hides, was not imported at start but registered with the spike after an Explorer restart. |
| Protocol version 0 (legacy) and 4 icons: left, right, double click | ✅ Same message sequences as Explorer: down, up, `NIN_SELECT`, `WM_CONTEXTMENU`, `WM_LBUTTONDBLCLK`. Recorded against Explorer first as a reference. |
| Context menus from a non-activating window (`WS_EX_NOACTIVATE`) | ✅ The app's `SetForegroundWindow` succeeded every time. Menus close on Esc and on click-away. |
| Tooltips | ✅ WPF tooltip shows even though the window never activates. Version 4 apps get `NIN_POPUPOPEN` and `NIN_POPUPCLOSE`. |
| Icon position query (`Shell_NotifyIconGetRect`) | ✅ Returns our icon's rectangle, so popups and flyouts open next to our window. |
| Tooltip and icon updates (`NIM_MODIFY`) | ✅ Reach us. ❌ Explorer's copy goes stale (fixed by mirroring). |
| Balloon notifications (`NIF_INFO`) | ⚠️ We get the event, but **no Windows toast appears** (fixed by mirroring). |
| Apps re-register after `TaskbarCreated` | ✅ Every app tested. Discord deletes and re-adds its icon **without a tooltip**, in Explorer too, so its tooltip goes blank. |
| Explorer restart while hosting | ✅ in 3 of 3 restarts. We lost the host role only until ManagedShell's next 100 ms check. Explorer sent `TaskbarCreated` about 1 s after its taskbar appeared, by which time we were host again, so every app (Explorer's own system icons included) re-registered with us. ❌ Explorer's own tray is left **empty**; mirroring fixes most of this. |
| Graceful exit hands icons back | ⚠️ Partly. See [Exit and crash](#exit-and-crash). |
| Crash hands icons back | ❌ No. Needs mirroring and a watchdog. See [Exit and crash](#exit-and-crash). |
| Windows 11 | Not tested (no machine). |

### Built-in icons

| Icon | Comes through? | Behaviour when clicked from our window |
|---|---|---|
| Volume (`7820ae73…`, owned by explorer.exe) | ✅ | The left-click flyout opens **anchored at our icon**. The right-click menu works. |
| Network (`7820ae74…`) | ✅ | The flyout opens next to our window. It is tall, so it was pushed against the screen edge and overlapped the window. The right-click menu works. |
| Power / battery (`7820ae75…`) | ✅ | The flyout opens next to our window. |
| Windows Update (MusNotifyIcon, `7820ae81…`) | ✅ | The menu works. Left-click opens Settings › Windows Update. |
| Windows Security, Bluetooth | ✅ | Menus work. |
| Meet Now (`7820ae83…`) | ⚠️ | Explorer hides it but still registers it, so we show it. We need to copy Explorer's hide rules. |
| Clock | ❌ Not a tray icon | Never comes through; replicating it is a non-goal. |
| Action Center | ❌ Not a tray icon | `ms-actioncenter:` opens Explorer's panel on the right edge; this works while we host. |

Opening Explorer's flyouts (and closing them with Esc) makes Explorer raise its taskbar above our window. That happened 4 times in run 1. ManagedShell raised our window again each time; one measured loss of the host role lasted about 260 ms. Any icon message sent during such a gap goes only to Explorer, and we miss it.

### Real apps tested

| App | Protocol version | Appears | Right-click menu from our window | Notes |
|---|---|---|---|---|
| PowerToys | 0 | ✅ | ✅ Win32 menu, placed above the icon | Sends a 32×32 icon |
| PowerToys Awake | 0 | ✅ | not clicked | 32×32 |
| Discord | 0 (GUID) | ✅ | ✅ Electron menu | Drops its tooltip after `TaskbarCreated` |
| Directory Opus | 3 (GUID) | ✅ | ✅ custom menu | |
| Listary | 0 | ✅ | ✅ | |
| Launchy | 0 | ✅ | ✅ Qt menu | |
| Windows Security | 4 (GUID) | ✅ | ✅ | |
| Windows Command Palette | 0 | ✅ | not clicked | 32×32 |
| Google Drive | 4 (GUID) | ✅ | not clicked | 32×32 |
| Windows Update, volume, network, power, Bluetooth | 4 (GUID) | ✅ | ✅ | See [Built-in icons](#built-in-icons) |
| TestTrayApp (my test app; runs named Alpha to Foxtrot, versions 0 and 4) | 0 / 4 | ✅ | ✅ | All mouse messages logged and compared with Explorer |

I only left-clicked and double-clicked my test app and the system icons. On other apps those clicks open windows or change settings.

## Exit and crash

The spike logs whether it is the host every 250 ms, and SpikeTool reads Explorer's tray directly, so each row below is measured.

| Scenario | ManagedShell as shipped | With mirroring (spike `--mirror --fix-topmost`) |
|---|---|---|
| Icon added while we host | Only in our tray | In both trays |
| Tooltip or icon change while we host | Explorer's copy is stale | Both current |
| Balloon | Swallowed (no toast) | Windows toast shown; we also get the event |
| Graceful exit | Apps re-add. Icons Explorer never had come back. Icons Explorer already had keep **stale** state, because their re-add is rejected as a duplicate. Explorer's taskbar **stays demoted** from always-on-top. | Nothing to recover |
| Crash, normal case | Explorer keeps its old copies (still clickable). Icons added during our run are **missing** until someone broadcasts `TaskbarCreated`. A watchdog broadcast brought them back. | Explorer keeps every icon, up to date (checked by changing a tooltip just before killing the spike) |
| Explorer restarted while we hosted, then we exit or crash | Explorer's tray is **empty**. A `TaskbarCreated` broadcast brings back the 13 icons owned by other processes, but **not Explorer's own volume, network, power, Bluetooth and Meet Now icons**. Only an Explorer restart restores them (confirmed twice). | Explorer's tray is repopulated during the restart, own system icons included (14 of 16 icons). Missing: Windows Update and Windows Security. They re-register with `NIM_MODIFY`, which succeeds with us and is rejected by Explorer for an icon it doesn't know. |

**Replay test.** I re-registered the lost volume icon into Explorer's tray from the harness, using its owner window, ID, GUID and callback message (`SpikeTool replay-add`). Explorer showed it and clicking it opened the volume flyout. So a host can hand icons back itself, including Explorer's own.

### Mirroring

The spike subclasses its own `Shell_TrayWnd`. After ManagedShell handles an icon message, the spike sends the same message on to Explorer's `Shell_TrayWnd` with `SendMessageTimeout`. No ManagedShell patch is needed.

This keeps Explorer's tray a live copy, so most hand-back problems go away. Messages that ManagedShell already forwarded arrive at Explorer twice; add, modify and delete tolerate that.

Remaining gaps:
- **Failed modifies.** When Explorer rejects a mirrored `NIM_MODIFY` for an icon it doesn't know, the host must send a full `NIM_ADD` built from the state it has stored.
- **One unexplained miss.** In 1 of 2 mirror runs, Discord's delete-then-add left it missing from Explorer's tray. With per-message logging in the next run, Explorer accepted both messages and it showed.
- **Messages during a lost-host gap.** Messages that go only to Explorer while we are not the host are not mirrored back to us. We would need to re-read Explorer's tray after regaining the role.

## Other findings

1. **Re-entrancy can drop icon messages.** Apps send icon messages with `SendMessage` (`WM_COPYDATA`). Any wait on the UI thread that processes window messages lets these messages run in the middle of another handler. Examples: `Process.GetProcessById`, a contended `lock` on an STA thread, cross-process `SendMessage`, COM calls. If this happens inside a handler of ManagedShell's `TrayIcons.CollectionChanged`, ManagedShell hits "Cannot change ObservableCollection during a CollectionChanged event" and **drops the message**.
   - In run 2 this lost 4 adds and 1 delete. Afterwards the spike was missing Explorer's network, power and volume icons.
   - In run 3 the spike crashed on startup.
   - In the spike, my own diagnostic code (`Process.GetProcessById`) triggered it; the captured stack is in `spike/phase0-logs/run3b`.
   - Fix used from run 4 on: apply ManagedShell changes to our UI with `Dispatcher.BeginInvoke`, and never wait inside its handlers. Result: 0 errors in runs 4 to 7, which included two Explorer restarts.
2. **Icon equality.** ManagedShell treats two `NotifyIcon` objects as equal if either their window and ID or their GUID match. Adapter code must compare object references, or replaced icons show up twice (this happened once).
3. **Explorer's taskbar loses always-on-top** when ManagedShell starts. Explorer sometimes restores it when its own flyouts open, but after our exit it stayed demoted. Fix tested: set Explorer's taskbar back to topmost, then raise our hidden window above it (`--fix-topmost`).
4. **Explorer's hidden-icon order.** The import step switches "hide inactive icons" off and on, and that reversed the order of Explorer's hidden-icons flyout. An Explorer restart mostly restored it.
5. **Taskbar queries.** `ABM_GETTASKBARPOS` answered with our window's rectangle is what makes flyouts and app popups open next to our tray; we can keep that on purpose. `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR` are answered with "no auto-hide" regardless of Explorer's setting. That would break apps that make room for an auto-hidden taskbar (for example browsers leaving a gap when maximised). These two should be forwarded to Explorer.
6. **Icon source sizes** (affects requirement 3 and Phase 3). An app hands over a single `HICON`, and that is all we get. Observed: 16×16 for most apps, 32×32 for PowerToys, Command Palette and Google Drive, and a 48×48 test icon arrived intact as 48×48. There is no larger source in the protocol. The app's .exe icon is larger but is a different image and loses status badges. At large sizes, expect upscaled 16 px art.
7. **Window layer.** A borderless, topmost window with `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` works as planned. It stays out of Alt+Tab, never takes focus, and still lets apps take the foreground for their menus, because our process received the last input.
8. **Tooltip differences from Explorer.** ManagedShell sends `NIN_POPUPOPEN` as soon as the mouse enters; Explorer waits for the hover delay. Our WPF tooltip also shows for version 4 icons that did not ask for the standard tooltip (`NIF_SHOWTIP`), which Explorer does not do. Both are minor.
9. **An Explorer quirk.** When several icons from the same .exe register within a few milliseconds, Explorer can accept one (`NIM_ADD` returns success) and never display it until it is deleted and re-added. Seen twice with my multi-instance test app, once with the spike not running. Real apps are rarely affected, but it can make hand-back look broken.

## Recommended changes to the plan

These are now in PLAN.md.

1. **License.** ManagedShell is Apache-2.0, not MIT. Ship its LICENSE and NOTICE.
2. **`ITrayHost` requirements for Phase 1**, all proven in the spike except where noted:
   - Mirror every icon message to Explorer. Turn a rejected mirrored modify into a full add. Not yet built: the full add from stored state.
   - Keep Explorer's taskbar always-on-top, and restore it on exit.
   - Apply ManagedShell changes to the UI via `BeginInvoke`, with no waits in its handlers. Consider running the tray service on its own thread.
   - Forward `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR` to Explorer. Keep answering `ABM_GETTASKBARPOS` with our window and the nearest screen edge, so flyouts follow the window.
   - Not built yet: after regaining the host role, re-read Explorer's tray to pick up anything registered during the gap.
   - Not built yet: a small watchdog process that broadcasts `TaskbarCreated` if the main process dies.
   - Not built yet: copy Explorer's hide rules for system icons (Meet Now, and "Turn system icons on or off").
   - Decided 2026-09-25 (see PLAN.md, "Decisions"): no fork. ManagedShell stays unmodified and these workarounds live in our `ITrayHost` implementation. Offering a "forward handled messages" option upstream is optional.
3. **Phase 3 (scaling).** Change "always request the largest icon source" to "use the app's `HICON` at its native size (16 to 48 px observed); optionally fall back to the .exe icon".
4. **Risk 5 (balloons).** With mirroring, Windows shows balloons as toasts. Our own balloon UI becomes optional.
5. **Explorer's visible tray.** With mirroring, every icon shows in both Explorer's tray and ours. Decided 2026-09-25 (see PLAN.md, "Decisions"): keep Explorer's taskbar and tray as they are.
6. **Phase 6 tests.** Add "Explorer restarts while hosting, then we exit" and "then we crash". Both broke hand-back in stock ManagedShell.
7. **Windows 11.** From ManagedShell's source (not verified here), clicks on the Windows 11 volume icon open Quick Settings, and network and power do the same before 22H2, because sending clicks crashed older builds. Test on real hardware before promising parity.

## Not tested

- **Windows 11.** No machine.
- **Elevated tray apps (UIPI, risk 3).** This needs an admin-level app, and I couldn't clear a UAC prompt or close an elevated window afterwards. Easiest manual test: run the spike, open Task Manager (it auto-elevates and shows a CPU icon in the tray), then check that the icon appears and that right-click opens its menu.
- **Mixed DPI and multiple monitors.** Only one 96-DPI monitor.
- **Left-click and double-click on real third-party apps.** Right-click only; see [Real apps tested](#real-apps-tested).
- **Mouse wheel on the volume icon, and keyboard navigation of the tray.**
- **Long-running stability.** Each spike run lasted 1 to 8 minutes.

## Side effects on this machine

- Explorer was restarted 5 times, and the taskbar blinked each time. The end state matches the starting point: the same 15 tray icons, and the taskbar always-on-top.
- Discord's tray tooltip is now blank. It re-adds its icon without a tooltip after every `TaskbarCreated`, as a normal Explorer restart would cause too. It comes back when Discord updates its tooltip or restarts.
- The order of the hidden-icons flyout may differ slightly from before (see Other findings, item 4).
- The spike window was moved during testing, and 4 automated right-clicks then landed on a Brave page. Each only opened Brave's context menu, which was closed with Esc.
- Left-clicking the Windows Update icon navigated the open Settings window to Windows Update.
- "TestTrayApp" now appears in Windows' notification-area icon history (Taskbar settings › Select which icons appear).
- The .NET SDK's first run installed an ASP.NET Core HTTPS development certificate (untrusted) in the current-user certificate store.
- Added `spike/nuget.config`, which adds the nuget.org package source for this project only.

## Evidence

Logs are in `spike/phase0-logs/runN/`. Each has `spike.log`, `state.json` and test app logs.

| Run | What it covers |
|---|---|
| run1 | Stock ManagedShell. Explorer reference clicks (in the test app logs), click, menu and tooltip tests, real apps, system flyouts, Explorer restart, then crash |
| run2 | Re-entrancy reproduced and instrumented; graceful exit |
| run3 | Crash on startup |
| run3b | Stack trace of the re-entrancy |
| run4 | Fixed spike; clean Explorer restart; graceful exit |
| run5 | Crash from the normal state; watchdog-style recovery |
| run6, run7 | Mirroring: balloon toast, Explorer restart, crash |

The logs contain local details (Wi-Fi name, user paths), so remove them before publishing the repo.
