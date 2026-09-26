# Findings

Phase 2 (show/hide) comes first, then Phase 1 (production tray host, window and placement). The
Phase 0 feasibility report follows unchanged, apart from notes where Phase 1 corrected it.

## Phase 2: show/hide

Date: 2026-09-26. Same machine and setup as Phases 0 and 1 (Windows 10 Pro 22H2, build 19045.7725,
one 1920×1080 monitor at 100%, ManagedShell 0.0.374 on .NET 10). "Show animations in Windows" was
off at first; you turned it on during testing (finding 5).
How to use it is in [README.md](README.md), "Showing and hiding".

### Phase 2 verdict

**Show/hide works on Windows 10.** Unit tests (128) and the smoke test (46 checks, 19 of them new)
pass. Hiding only hides the window: icons added and changed while it is hidden reach both our host
and Explorer's tray, and while it is hidden Explorer answers the icon-position queries, so menus and
flyouts open at the taskbar.

One change to the window that later phases inherit: **the tray window is now per-pixel transparent**
(WPF `AllowsTransparency`), because WPF does not let any other window fade (finding 1). Phase 4 gets
background opacity and rounded corners from this for free; its blur must be tried on this kind of
window.

### What Phase 2 built

- `ShowHide/` in the app: the controller (hotkey, hotspot, auto-hide, commands), the auto-hide rules
  and the tracking of menus and flyouts opened from the tray (pure logic, unit-tested), a watcher
  thread with a low-level mouse hook and WinEvent hooks, the fade and slide animations, and hotkey
  parsing.
- Two modes. **Always visible** (default): the hotkey or the grip menu's Hide hides the tray until
  the hotkey or the hotspot brings it back. **Auto-hide**: starts hidden; the hotkey or the hotspot
  shows it; a click elsewhere, a switch to another window, or the pointer being away for the hide
  delay hides it. A Win32 menu or a popup that opened within 2 s of a click on one of our icons keeps
  it shown while open. The tray never hides without a working hotkey or a hotspot to bring it back.
- `--toggle`, `--show`, `--hide` for the running instance; the settings file is reloaded when it
  changes (the plan's "live reload", for `settings.json`); grip menu items Auto-hide and Hide.
- `ManagedShellTrayHost`: while the window is hidden, Explorer's answer to an icon-position query
  stands even when it is a failure (before, an icon Explorer did not know got our window's position).
- Smoke test: 19 show/hide checks; before every simulated input it waits for 1.5 s without user
  input (`SpikeTool wait-idle`). SpikeTool also presses any key chord and has a click target of its own.

### Phase 2 results

| Check | Result |
|---|---|
| `--hide`, `--show`, `--toggle`, the grip menu's Hide | ✅ Smoke test and by hand. |
| Icons added and tooltips changed while hidden | ✅ Reach our host and Explorer's tray; the new icon is in place when the tray is shown. |
| Position queries while hidden, pointer where the tray was | ✅ Explorer answers (S_FALSE for an icon in its closed hidden-icons flyout). |
| Hotkey `Ctrl+Alt+Shift+T` | ✅ Registered on this machine. A hotkey held by another program is logged as such; auto-hide then refuses to hide (no way back). |
| Hotspot (left edge in the smoke test; top-left corner by hand) | ✅ Shows the tray after the 300 ms dwell (plus up to 100 ms polling). The pointer at 7,7 shows it, at 20,0 it does not (the corner is 8 px). Shown by the hotspot, it hides the hide delay after the pointer leaves the hotspot. |
| Timer | ✅ Hides after the delay plus up to 200 ms (the check interval) and the animation: measured 1.5 s from outside the app for a 1 s delay and a 150 ms slide. Shown by the hotkey, it waited until the pointer had been over the tray. |
| Click-away and switch-away | ✅ A click on another process's window (low-level mouse hook) and a foreground change to another window both hide it. |
| A menu opened from our tray | ✅ TestTrayApp's Win32 menu opened 120 ms after the right-click, was recognised (`EVENT_SYSTEM_MENUPOPUPSTART`), and kept the tray shown for 3 s with the pointer away; the tray hid after the delay once Esc closed it. |
| Windows 10's volume flyout opened from our tray | ✅ Took the foreground 160 ms after the click and kept the tray shown for 2.5 s with the pointer away. Closing it with Esc hands the foreground to Explorer's taskbar, which counts as a switch away: the tray hid at once (finding 2). |
| Fade and slide | ✅ Screen samples at a 2 s duration show the fade's intermediate colours and the slide's edge moving; icon positions are correct afterwards. |
| Settings file edited while running | ✅ Applied after a 0.3 s pause for further writes; the app's own saves are ignored; a half-written file keeps the current settings. |
| Auto-hide on, but no hotkey and no hotspot | ✅ Stays visible, logs why, refuses `--hide`; removing the hotspot while hidden shows the tray. |

### Phase 2 findings

1. **WPF strips `WS_EX_LAYERED` from windows without `AllowsTransparency`.** The first fade used
   `SetLayeredWindowAttributes` on the normal (DirectX-drawn) tray window. The style never stuck: set
   from inside the app or from another process, `GetWindowLong` showed no `WS_EX_LAYERED` right after,
   and the alpha calls failed. WPF enforces that a window is layered exactly when it was created with
   `AllowsTransparency`. So the tray window is now created with `AllowsTransparency="True"` and a
   transparent window background, and the content draws the background. The fade animates the content's
   opacity; the slide moves the content, and the window clips what moves outside it, so it needs no
   window region and never passes over other windows or monitors. Rendering, clicks, menus and the
   smoke test are unchanged.
2. **Closing a Windows 10 flyout gives the foreground to Explorer's taskbar.** The volume flyout is a
   `Windows.UI.Core.CoreWindow` of ShellExperienceHost that takes the foreground when it opens. When it
   closes (Esc, or a click elsewhere), the foreground goes to Explorer's `Shell_TrayWnd`, so an
   auto-hiding tray with click-away on hides at once rather than after the delay. That seems right
   (the interaction is over), but it is a choice.
3. **What keeps the tray shown.** Apps' Win32 menus are seen from any process through
   `EVENT_SYSTEM_MENUPOPUPSTART`/`END`, and flyouts through the foreground change they cause. Both
   count only when they start within 2 s of a click on one of our icons. A flyout counts while it is
   in the foreground and is a visible, always-on-top window; this keeps an ordinary app window opened
   by a click (Discord's main window, say) from holding the tray open. Menus that are not Win32 menus
   (Electron, Qt) are only covered if they take the foreground; not tested with a real app.
4. **A process's first window, created visible, takes the foreground even with `WS_EX_NOACTIVATE`.**
   Seen with SpikeTool's first click target: the tray hid on that foreground change, before the click.
   The target is now created hidden and shown with `SW_SHOWNOACTIVATE`, so the check exercises the
   click itself. For users this means an app that pops up a window also hides an auto-hiding tray.
5. **Animations follow "Show animations in Windows".** The setting was off when testing began, so
   show/hide was instant, as designed (together with finding 1, this first made the fade look
   broken). You turned it on during testing, and the animation results above were taken with it on.
   The app logs the animation it will use at startup ("Show/hide: ..., animation ...").

### Phase 2 follow-up: full-screen apps

Built after Phase 2, as decided on 2026-09-26 (PLAN.md, "Decisions"). Before it, the tray stayed above
full-screen windows: `SpikeTool fullscreen-probe` showed the tray's pixels on top of a full-screen
window while Explorer's taskbar had stepped back.

**What it does.** While a full-screen window is the last window activated on the tray's monitor, the
tray drops its always-on-top flag and sits directly below that window. Switching to another window
on that monitor, or the full-screen window closing or leaving full screen, makes it always-on-top
again; working on another monitor does not. The hotkey (or `--show`) brings it up over the
full-screen window; the hotkey again, a click elsewhere, or activating the full-screen window sends
it back behind (an auto-hiding tray hides instead). While it is covered, Explorer answers position
queries, and the hotspot does not fire.

| Check | Result |
|---|---|
| A new full-screen window (a game or video player) | ✅ The tray steps behind it within 10 ms of Windows' notification; the screen shows the full-screen window where the tray was. Always-on-top again when it closes. |
| A normal window turning full screen without a change of foreground (F11) | ✅ Steps behind it, and back when it leaves full screen. |
| Hotkey while behind | ✅ The tray shows over the full-screen window; the hotkey again or a click on the full-screen window puts it back behind. |
| Smoke test | ✅ 6 new checks (52 in all). |

Findings:

1. **Windows tells shell hook windows about full screen.** A window registered with
   `RegisterShellHookWindow` gets `FULLSCREENENTER` (53) and `FULLSCREENEXIT` (54) with the window's
   handle, in both cases above, about 20 ms before Explorer sends its appbars `ABN_FULLSCREENAPP` and
   drops its taskbar's always-on-top flag. `ABN_FULLSCREENAPP` also reached an appbar registered
   through our tray host, but it names no window or monitor, so the tray uses the shell hook. The
   shell hook's `RUDEAPPACTIVATED` is not a full-screen signal on Windows 10: it came for ordinary
   activations too.
2. **`HWND_TOP` cannot lift the tray over the foreground full-screen window.** From our (background)
   process, `SetWindowPos(HWND_TOP)` left the Z-order unchanged. Making the tray always-on-top again
   does lift it, so that is how the hotkey brings it up. Clicking the already-active full-screen
   window activates nothing, so the click itself is what sends the tray back (the click-away hook).
3. **Test pitfall:** a console program started hidden (`Start-Process -WindowStyle Hidden`) gets its
   first normal window created hidden, and a window made visible later by changing its style is never
   reported as activated. SpikeTool now shows its test windows explicitly; the tray also counts a
   window entering full screen as activated if it is the foreground window.

Not handled: a window that was already full screen when FreeSystemTray started is only noticed once
it enters full screen again. Not tested: multiple monitors, real games and video players.

### Open points

- **Hiding under the pointer.** If the hotkey hides the tray while the pointer is on an icon, that
  app does not get the mouse-leave (`NIN_POPUPCLOSE` for version 4 icons) until the pointer moves
  over the tray again.

### Not tested in Phase 2

- Multiple monitors and mixed DPI (the hotspot is on the tray's monitor; the zone maths is unit-tested
  with an offset monitor at 150%).
- Keyboard focus in the tray, touch and pen.
- Auto-hide with a real app's non-Win32 menu (Discord, Qt apps), and the network and power flyouts.
- A long run in auto-hide mode.
- Windows 11.

### Phase 2: side effects on this machine

- Simulated input, always after at least 1.5 s without user input: the pointer moved to the left
  screen edge, the top-left corner and elsewhere; `Ctrl+Alt+Shift+T` (taken by the tray while it ran)
  and Esc pressed; right-clicks on test icons and their menus closed with Esc; the grip menu opened
  and used with its access keys; clicks on SpikeTool's own target window; the volume flyout opened
  from the tray and closed with Esc (volume unchanged).
- Windows' notification icon history has two more "TestTrayApp" entries (P2Hidden, SmokeD), and Action
  Center one more "Balloon from SmokeA" notification per smoke-test run.
- `Ctrl+Alt+Shift+T` was registered by a PowerShell session for a few seconds to test a taken hotkey,
  then released.
- Full-screen follow-up: SpikeTool covered the primary screen with a black (dark grey) window of its
  own for 2 to 9 s at a time, about ten times, and registered a temporary appbar and shell hook window.
- No Explorer restarts. All runs used settings and logs in temporary folders;
  `%LOCALAPPDATA%\FreeSystemTray` still does not exist. Explorer is the tray host, always-on-top.

## Phase 1: production tray host

Date: 2026-09-25 and 26. Same machine and setup as Phase 0 (Windows 10 Pro 22H2, build 19045.7725,
one 1920×1080 monitor at 100%, ManagedShell 0.0.374 on .NET 10). The code is in `src/`; how to build,
run and test it is in [README.md](README.md).

### Phase 1 verdict

**The production tray host works on Windows 10.** Unit tests (72) and the smoke test (27 checks,
`tests/smoke`) pass. Both things the plan asked me to verify hold, but each needed fixes that the
spike did not have:

- **Clicking an icon in Explorer's own tray still opens its flyout or popup at the taskbar.**
  Explorer's volume, network and power flyouts do. Third-party apps ask the tray host (us) where their
  icon is, so our window answers only when the click started in it. Two more fixes were needed to
  keep Explorer's own tray working: Explorer asks the tray host about its own taskbar and about
  its hidden-icons chevron (findings 1 to 3).
- **Apps see Explorer's auto-hide setting.** `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR(EX)` are now
  answered by Explorer. I checked this with a temporary auto-hide appbar, not with the taskbar itself
  set to auto-hide, because that means changing a Windows setting. How to finish the check is under
  [Checking auto-hide with the real taskbar](#checking-auto-hide-with-the-real-taskbar).

One plan item does not do what it was meant to: **reporting the nearest screen edge does not make
Windows 10's own flyouts open beside the tray.** They ignore the edge, center on the tray and are then
pushed inside the work area, so they overlap a free-floating window wherever it is (finding 4).
Options are listed there; the decision is yours.

The full add, the part of mirroring Phase 0 had not built, needed one more discovery: Explorer only
accepts it for a GUID icon when it carries the app's executable path in shell32's format
(finding 5). With that, Windows Update and Windows Security, the two icons that went missing from
Explorer's tray after an Explorer restart in Phase 0, are restored while we host.

### What Phase 1 built

- `FreeSystemTray.TrayHost`: `ITrayHost` and `ManagedShellTrayHost`, on ManagedShell 0.0.374 unmodified.
  All workarounds hook in through a subclass of ManagedShell's `Shell_TrayWnd`:
  mirroring with fix-ups (a rejected add is re-sent as a modify; a rejected modify becomes a full add
  from ManagedShell's state), auto-hide queries answered by Explorer, position queries routed by where
  the user is interacting, Explorer's chevron and own-taskbar queries answered for Explorer, Explorer's
  topmost flag restored once, and ManagedShell's changes applied to the UI with `BeginInvoke`.
- `FreeSystemTray`: the borderless, topmost, non-activating tray window; dragging by a grip with a
  lock and optional edge snapping; placement saved as an anchor corner and kept on a connected
  monitor; the nearest screen edge reported to the host; JSON settings; a log; a watchdog process
  that hands the tray back to Explorer if the app dies; `--diag` output for the smoke test.
- Tests: unit tests for the placement rules, the three message formats, the fix-up and routing rules,
  shell32's path format and settings; the smoke test built on the Phase 0 test app and harness.

### Phase 1 results

| Check | Result |
|---|---|
| Take the host role, import icons, mirror tooltips, new icons and balloons to Explorer | ✅ Smoke test. Balloons appear as Windows toasts. |
| Volume, network and power clicked in **Explorer's** tray | ✅ Flyouts open at the taskbar. Volume asks the tray host nothing; network asks for the taskbar position; power also asks for its icon's position. Both are answered by Explorer. |
| Third-party icon clicked in **Explorer's** hidden-icons flyout | ✅ TestTrayApp is told its rectangle in Explorer's flyout. ❌ ManagedShell as shipped answers every such query with our window (from its source). |
| Same icon clicked in **our** window | ✅ Told our icon's rectangle. |
| Explorer's hidden-icons flyout while we host | ✅ Opens above the chevron. ❌ As shipped: opens at the top-left corner of the screen (finding 2). |
| Auto-hide queries | ✅ `ABM_GETAUTOHIDEBAR(EX)` returns an auto-hide appbar registered with Explorer. ❌ As shipped: none. |
| Explorer's taskbar always-on-top | ✅ Restored after ManagedShell demotes it. ❌ As shipped: demoted. |
| Explorer forgets an icon while we host | ✅ The next modify becomes a full add (TestTrayApp, Windows Update, Windows Security). |
| Explorer restart while we host | ✅ We kept the role; Explorer's new tray had 27 of 29 icons at once. The two missing, Windows Update and Windows Security, re-register with `NIM_MODIFY`, and Explorer rejected our full add because it lacked the exe path (finding 5). With that fixed, the same situation (Explorer forgets the icon, the app re-registers with a modify) restores both. I did not restart Explorer a second time. |
| Graceful exit, and kill (watchdog) | ✅ Explorer is host again, topmost, with every icon and its latest tooltip. |
| Drag, snap, lock, position after restart | ✅ Including snapping into both corners and the lock blocking drags. |
| Windows 10 flyouts opened from our window | ⚠️ Open next to the icon but overlap the tray window at every position tried (finding 4). |
| Long run | ✅ About 2 hours of normal use (23:29 to 01:24), including window moves and what looked like a full-screen app: Explorer's tray stayed current, and the only errors came from the topmost enforcement that finding 6 removed. |

### Phase 1 findings

1. **Explorer asks the tray host about its own taskbar.** When it lays out its tray, for example on
   every icon add, Explorer sends `ABM_GETTASKBARPOS` with `hWnd` set to its own taskbar, and that goes
   to the tray host. ManagedShell as shipped answers with the host's window; so did the first version
   of our routing whenever the cursor was over our window. Explorer's own taskbar must always get
   Explorer's answer. (While chasing this I also saw new icons that seemed never to appear in
   Explorer's tray; that turned out to be finding 9.)
2. **Explorer asks the tray host where its hidden-icons chevron is.** To place the flyout, Explorer
   calls `Shell_NotifyIconGetRect` with GUID `964b6542-bbad-44ee-848a-3a95d85951ea` (hWnd 0, uID -1).
   ManagedShell does not know that GUID, and Explorer answers 0 if the query is forwarded back to it
   while it is the one asking, so the flyout opened at (0,0). Fix: answer from the rectangle of the
   chevron button (the visible `Button` in Explorer's `TrayNotifyWnd`). Discord asks for the same GUID
   every 250 ms.
3. **Who answers a position query.** `Shell_NotifyIconGetRect` and `ABM_GETTASKBARPOS` carry no hint of
   which tray was clicked. Rule used: our window answers if the cursor is on it; otherwise Explorer
   answers if the cursor is on its taskbar or hidden-icons flyout; otherwise our window answers only
   within 5 s of a click in it. Explorer answers -1 for an icon in its hidden-icons flyout while that
   flyout is closed, which the app receives as `S_FALSE`.
4. **Windows 10's own flyouts ignore the reported edge.** Measured from our window at four positions:

   | Tray window (x, y, 402×30) | Reported edge | Network (360×582) | Volume (360×100) | Power (360×255) |
   |---|---|---|---|---|
   | 795, 85 (top middle) | Top | 1002, 0 | 1002, 50 | 905, 0 |
   | 795, 685 (lower middle) | Bottom | 1002, 409 | 1002, 650 | 905, 573 |
   | 1495, 485 (right) | Right | 1560, 209 | 1560, 450 | 1560, 373 |
   | 184, 485 (left) | Left | 391, 209 | 391, 450 | 294, 373 |

   Every flyout overlaps the tray window. The rule that fits: network and volume center horizontally
   on the tray rectangle's right edge minus 15 px (the anchor moved with the right edge when I made the
   window one icon narrower), power centers on its icon's rectangle, all center vertically on the tray,
   and then Windows moves them inside the work area. On a real taskbar this lands them just above it
   only because the taskbar is outside the work area. The edge still helps third-party apps that read it.
   Options:
   - **AppBar mode** (already an optional later addition in the plan): a tray docked to an edge that
     reserves space is outside the work area, so the flyouts land next to it as they do next to the taskbar.
   - **Steer them** for a floating tray by answering with shifted rectangles, so they land beside the
     window. This depends on the measured offsets and the 360 px width, so it is fragile.
   - **Accept** the overlap for Windows' own flyouts.

   My recommendation: accept it for the floating window and treat AppBar mode as the fix for users
   who dock the tray. Try steering only if the overlap bothers you in practice.
5. **`NIM_ADD` carries the app's executable, and Explorer checks it.** Shell32's message is 1484 bytes: a signature and the
   `NIM_*` message, the 956-byte `NOTIFYICONDATA` with 32-bit handles, then the calling app's executable
   path (`WCHAR[260]`), filled in for `NIM_ADD` only. Paths under Windows, System32, Program Files and Program Files (x86) are written as
   `{FOLDERID}\rest` (for example `{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\SecurityHealthSystray.exe`),
   the same form Explorer keeps in its `IconStreams` registry value; other paths stay plain. A full add
   built from a modify therefore had no path, and Explorer rejects that for an app's GUID icon. With a
   plain path it still rejected Windows Security; with shell32's form it accepted it. (Windows Update,
   whose GUID is one of the shell's own `7820ae…` family, was accepted with the plain path.) The host
   now writes the path in shell32's form, and in `--verbose` mode it checks its encoding against every
   real add; no difference was seen. The SysWOW64 (`FOLDERID_SystemX86`) case is assumed, not observed.
6. **Explorer drops its taskbar's always-on-top flag while a full-screen app is in front**, and restores
   it afterwards. Forcing it back fails. So the host restores the flag once, after ManagedShell has
   demoted it, and never enforces it. The watchdog leaves it alone if a full-screen window is in front.
7. **Tray messages arrive while ManagedShell starts.** `NotificationArea.Initialize` forwards
   `TaskbarCreated` to Explorer with `SendMessage`, and apps' sent messages run during that wait, before
   `Initialize` returns. Subclassing afterwards missed them, so the host subclasses the window as it is
   created (a `WH_CBT` hook on its own thread).
8. **The host role is lost while the Start menu or a system flyout is open**, for up to about 2 s in the
   long run: ManagedShell tries to raise our window every 100 ms, but Explorer keeps its taskbar above it
   meanwhile. Icon messages sent then reach only Explorer (Risk 2; the re-read is planned for Phase 6).
9. **New icons first appear on the taskbar.** Windows 10 shows a newly added icon in a third toolbar
   (`Systemforfremmet varslingsområde`, the system-promoted area, beside the chevron) and moves it to
   the hidden icons later. Phase 0's SpikeTool did not read that toolbar, so such icons looked missing;
   Phase 0's item 9 was most likely this. SpikeTool reads all three toolbars now. It also explains
   why Explorer asks for its taskbar position twice on some adds (it widens its tray for the icon).
10. **`ABM_GETSTATE` is 0 when the taskbar is not auto-hidden.** `ABS_ALWAYSONTOP` has not been reported
    since Windows 7, so Explorer's answer equals ManagedShell's "no auto-hide" until auto-hide is on.
11. **The tray window's own menu needs the foreground for its keyboard.** Being non-activating, the
    window got no keys, so Esc did not close the grip menu. It now takes the foreground while the menu
    is open, as Explorer's taskbar menus do.

### Checking auto-hide with the real taskbar

Not done by me, because it means changing a Windows setting. To check it yourself:

1. Turn on Settings › Personalization › Taskbar › "Automatically hide the taskbar in desktop mode".
2. Start FreeSystemTray, then run `spike\SpikeTool\bin\Debug\net10.0-windows\SpikeTool.exe appbar`.
3. Expected: `ABM_GETSTATE -> 1 (autohide=True, …)`, the same as the "sent to Explorer's taskbar
   directly" line, and `ABM_GETAUTOHIDEBAR bottom` → Explorer's `Shell_TrayWnd`. With ManagedShell as
   shipped (`--no-autohide-forward`) both say no auto-hide.
4. Turn the setting off again.

### Not tested in Phase 1

- **Multiple monitors and mixed DPI.** One monitor at 100%. The placement rules are unit-tested with two
  monitors (keeping the window on screen, a disconnected monitor, snapping per monitor).
- **Auto-hide with the real taskbar**, see above.
- **Windows 11, and elevated tray apps** (still Phase 6).
- **Keyboard navigation of the tray, and the mouse wheel.**

### Phase 1: side effects on this machine

- Explorer was restarted once (2026-09-26 01:29) to test the restart case; no folder windows were open.
  Its tray has the same 15 icons as before Phase 1, and the taskbar is always-on-top.
- Simulated clicks and drags opened and closed (with Esc) the volume, network and power flyouts, the
  hidden-icons flyout, the test apps' menus and the tray window's menu.
- Windows Update's and Windows Security's icons were removed from Explorer's tray several times on
  purpose, to test the full add, and are back.
- Windows' notification icon history (Taskbar settings › Select which icons appear) has more
  "TestTrayApp" entries, one per test icon ID used in Phase 1.
- Two Windows notifications "Balloon from SmokeA" from the smoke test are in Action Center.
- Discord's tray tooltip is blank again (it re-adds its icon without one after each `TaskbarCreated`).
- All test runs used settings and logs in a temporary folder; `%LOCALAPPDATA%\FreeSystemTray` was never
  created. Someone moved the tray window during the long run (23:30 to 23:36); those moves were saved
  in that temporary settings file.
- The unit tests restored xUnit 2.9.3 and Microsoft.NET.Test.Sdk 17.14.1 from nuget.org.

## Phase 0: tray host feasibility

Date: 2026-09-25. Tested on Windows 10 only (no Windows 11 machine available). Phase 1 corrected or extended some of these results; those places point to the Phase 1 section.

| | |
|---|---|
| OS | Windows 10 Pro 22H2, build 19045.7725, Norwegian UI |
| Hardware | Laptop, one 1920×1080 monitor at 100% (96 DPI) |
| Session | Normal user, not elevated. Explorer is the shell, with the Open-Shell start button. "Always show all icons" is off (`EnableAutoTray=1`). |
| Library | ManagedShell 0.0.374 from NuGet, the same commit as GitHub `master` (`8552220`, 2026-09-17), on .NET 10 |
| Method | Spike app plus a logging test tray app plus a harness that simulates mouse input and reads Explorer's tray. See [spike/README.md](spike/README.md). Logs are in `spike/phase0-logs/`. |

### Verdict (decision gate)

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

### ManagedShell and RetroBar maintenance

| Repo | Last push | Stars | Open issues | License |
|---|---|---|---|---|
| cairoshell/ManagedShell | 2026-09-17 (several commits per month) | 415 | 10 | **Apache-2.0** (the plan says MIT) |
| dremin/RetroBar | 2026-09-09 (still uses ManagedShell 0.0.358) | 4.4k | 479 | Apache-2.0 |
| cairoshell/cairoshell | 2026-09-09 | 3.4k | 245 | Apache-2.0 |

ManagedShell targets net480, net6.0-windows and net10.0-windows. It publishes to NuGet on every merge (0.0.x versions) and has no release tags. Apache-2.0 is permissive, but when we redistribute we must ship the license and keep its NOTICE and attributions.

### How ManagedShell takes the tray

Read from the source and confirmed in the logs.

1. **Takes the role.** It creates its own hidden, topmost `Shell_TrayWnd`. It pushes Explorer's taskbar to the bottom of the Z-order, which removes Explorer's always-on-top flag. A 100 ms timer checks `FindWindow("Shell_TrayWnd")` and raises its window again whenever Explorer's is found first. `Shell_NotifyIcon` sends to whichever window `FindWindow` returns.
2. **Imports existing icons.** It reads Explorer's tray toolbar out of Explorer's memory (`ReadProcessMemory`). To reach hidden icons it briefly switches off "hide inactive icons" through the undocumented `ITrayNotify` COM interface.
3. **Asks apps to re-register.** It broadcasts `TaskbarCreated` so running apps register their icons again.
4. **Does not forward what it handles.** Icon messages that ManagedShell handles are not forwarded to Explorer. Only the ones it rejects are forwarded. This is why Explorer's tray goes stale.
5. **Answers some taskbar queries itself.** `ABM_GETTASKBARPOS`, `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR` are answered from its own state, not Explorer's. Other appbar messages are forwarded. Seen during testing: 25 `GETTASKBARPOS` and 5 `GETSTATE` handled, 8 `ABM_NEW` forwarded.
6. **On exit.** Graceful exit (`Dispose`) destroys the window and broadcasts `TaskbarCreated`. After a crash, nothing is sent.

### Results on Windows 10 22H2

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

#### Built-in icons

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

#### Real apps tested

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

### Exit and crash

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

#### Mirroring

The spike subclasses its own `Shell_TrayWnd`. After ManagedShell handles an icon message, the spike sends the same message on to Explorer's `Shell_TrayWnd` with `SendMessageTimeout`. No ManagedShell patch is needed.

This keeps Explorer's tray a live copy, so most hand-back problems go away. Messages that ManagedShell already forwarded arrive at Explorer twice; add, modify and delete tolerate that.

Remaining gaps:
- **Failed modifies.** When Explorer rejects a mirrored `NIM_MODIFY` for an icon it doesn't know, the host must send a full `NIM_ADD` built from the state it has stored.
- **One unexplained miss.** In 1 of 2 mirror runs, Discord's delete-then-add left it missing from Explorer's tray. With per-message logging in the next run, Explorer accepted both messages and it showed.
- **Messages during a lost-host gap.** Messages that go only to Explorer while we are not the host are not mirrored back to us. We would need to re-read Explorer's tray after regaining the role.

### Other findings

1. **Re-entrancy can drop icon messages.** Apps send icon messages with `SendMessage` (`WM_COPYDATA`). Any wait on the UI thread that processes window messages lets these messages run in the middle of another handler. Examples: `Process.GetProcessById`, a contended `lock` on an STA thread, cross-process `SendMessage`, COM calls. If this happens inside a handler of ManagedShell's `TrayIcons.CollectionChanged`, ManagedShell hits "Cannot change ObservableCollection during a CollectionChanged event" and **drops the message**.
   - In run 2 this lost 4 adds and 1 delete. Afterwards the spike was missing Explorer's network, power and volume icons.
   - In run 3 the spike crashed on startup.
   - In the spike, my own diagnostic code (`Process.GetProcessById`) triggered it; the captured stack is in `spike/phase0-logs/run3b`.
   - Fix used from run 4 on: apply ManagedShell changes to our UI with `Dispatcher.BeginInvoke`, and never wait inside its handlers. Result: 0 errors in runs 4 to 7, which included two Explorer restarts.
2. **Icon equality.** ManagedShell treats two `NotifyIcon` objects as equal if either their window and ID or their GUID match. Adapter code must compare object references, or replaced icons show up twice (this happened once).
3. **Explorer's taskbar loses always-on-top** when ManagedShell starts. Explorer sometimes restores it when its own flyouts open, but after our exit it stayed demoted. Fix tested: set Explorer's taskbar back to topmost, then raise our hidden window above it (`--fix-topmost`). Phase 1: do this once, when ManagedShell has demoted it. Explorer also drops the flag itself while a full-screen app is in front, and refuses to have it forced back (Phase 1, finding 6).
4. **Explorer's hidden-icon order.** The import step switches "hide inactive icons" off and on, and that reversed the order of Explorer's hidden-icons flyout. An Explorer restart mostly restored it.
5. **Taskbar queries.** `ABM_GETTASKBARPOS` answered with our window's rectangle is what makes flyouts and app popups open next to our tray; we can keep that on purpose. Phase 1: only for interactions that start in our window, and never when Explorer asks about its own taskbar (Phase 1, findings 1 to 3). `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR` are answered with "no auto-hide" regardless of Explorer's setting. That would break apps that make room for an auto-hidden taskbar (for example browsers leaving a gap when maximised). These two should be forwarded to Explorer.
6. **Icon source sizes** (affects requirement 3 and Phase 3). An app hands over a single `HICON`, and that is all we get. Observed: 16×16 for most apps, 32×32 for PowerToys, Command Palette and Google Drive, and a 48×48 test icon arrived intact as 48×48. There is no larger source in the protocol. The app's .exe icon is larger but is a different image and loses status badges. At large sizes, expect upscaled 16 px art.
7. **Window layer.** A borderless, topmost window with `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` works as planned. It stays out of Alt+Tab, never takes focus, and still lets apps take the foreground for their menus, because our process received the last input.
8. **Tooltip differences from Explorer.** ManagedShell sends `NIN_POPUPOPEN` as soon as the mouse enters; Explorer waits for the hover delay. Our WPF tooltip also shows for version 4 icons that did not ask for the standard tooltip (`NIF_SHOWTIP`), which Explorer does not do. Both are minor.
9. **An Explorer quirk.** When several icons from the same .exe register within a few milliseconds, Explorer can accept one (`NIM_ADD` returns success) and never display it until it is deleted and re-added. Seen twice with my multi-instance test app, once with the spike not running. Real apps are rarely affected, but it can make hand-back look broken.
   **Phase 1 correction:** most likely a blind spot of the harness, not an Explorer quirk. Windows 10 first shows a newly added icon in a third toolbar on the taskbar ("system-promoted"), which SpikeTool did not read, and moves it to the hidden icons later (Phase 1, finding 9).

### Recommended changes to the plan

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

### Not tested

- **Windows 11.** No machine.
- **Elevated tray apps (UIPI, risk 3).** This needs an admin-level app, and I couldn't clear a UAC prompt or close an elevated window afterwards. Easiest manual test: run the spike, open Task Manager (it auto-elevates and shows a CPU icon in the tray), then check that the icon appears and that right-click opens its menu.
- **Mixed DPI and multiple monitors.** Only one 96-DPI monitor.
- **Left-click and double-click on real third-party apps.** Right-click only; see [Real apps tested](#real-apps-tested).
- **Mouse wheel on the volume icon, and keyboard navigation of the tray.**
- **Long-running stability.** Each spike run lasted 1 to 8 minutes.

### Side effects on this machine

- Explorer was restarted 5 times, and the taskbar blinked each time. The end state matches the starting point: the same 15 tray icons, and the taskbar always-on-top.
- Discord's tray tooltip is now blank. It re-adds its icon without a tooltip after every `TaskbarCreated`, as a normal Explorer restart would cause too. It comes back when Discord updates its tooltip or restarts.
- The order of the hidden-icons flyout may differ slightly from before (see Other findings, item 4).
- The spike window was moved during testing, and 4 automated right-clicks then landed on a Brave page. Each only opened Brave's context menu, which was closed with Esc.
- Left-clicking the Windows Update icon navigated the open Settings window to Windows Update.
- "TestTrayApp" now appears in Windows' notification-area icon history (Taskbar settings › Select which icons appear).
- The .NET SDK's first run installed an ASP.NET Core HTTPS development certificate (untrusted) in the current-user certificate store.
- Added `spike/nuget.config`, which adds the nuget.org package source for this project only.

### Evidence

Logs are in `spike/phase0-logs/runN/` on the test machine. Each has `spike.log`, `state.json` and test app logs.

| Run | What it covers |
|---|---|
| run1 | Stock ManagedShell. Explorer reference clicks (in the test app logs), click, menu and tooltip tests, real apps, system flyouts, Explorer restart, then crash |
| run2 | Re-entrancy reproduced and instrumented; graceful exit |
| run3 | Crash on startup |
| run3b | Stack trace of the re-entrancy |
| run4 | Fixed spike; clean Explorer restart; graceful exit |
| run5 | Crash from the normal state; watchdog-style recovery |
| run6, run7 | Mirroring: balloon toast, Explorer restart, crash |

The logs contain local details (Wi-Fi name, user paths), so they are kept out of git (`.gitignore`).
