# Findings

The newest phase comes first: Phase 6 (robustness and polish), Phase 5 (management), Phase 4
(appearance and themes), Phase 3 (scaling), Phase 2 (show/hide), Phase 1 (production tray host, window
and placement). The Phase 0 feasibility report follows unchanged, apart from notes where Phase 1
corrected it.

## After Phase 6: code audit fixes and CI

Date: 2026-11-04. Windows 10 Pro 22H2 (build 19045.7807), same machine. A code audit fixed failures
that could end the tray or quietly stop part of it (the log writer's thread, the background reads of
Explorer's tray, starting the watchdog, an unreadable settings file), made the settings file accept
comments and trailing commas, refused theme imports whose XAML would land outside the themes folder,
turned the blur off during the first show of a tray that started hidden, and closed the flyout when
the layout becomes a fixed grid. CI (GitHub Actions, `windows-latest`) now builds the solution and runs
the unit tests on every push and pull request.

| Check | Result |
|---|---|
| Unit tests (309, 3 new) on GitHub's `windows-latest` runner | ✅ |
| Smoke test, full (with mouse), first run | 7 failed; none traced to the changes (below). |
| Smoke test again, same build | ✅ All checks passed. |

The first run's failures, from its log:

1. **Shift+right-click on SmokeD opened the menu of FreeSystemTray's own icon.** Dragging SmokeA into
   the hidden icons had just moved the tray's icons along by one, and the test took SmokeD's rectangle
   from `state.json` before it was rewritten. A race in the test, not the app: it now waits until the
   rectangle is the same in two reads 400 ms apart.
2. **A theme edit was never applied** (and so the next check, a broken edit, found the old values).
   The theme folder's watcher reported no change for that write; the following one arrived. Nothing in
   the log said a notification was lost, so the settings and theme watchers now log their error event
   (Windows dropped notifications) and read their files again.
3. **Explorer's own tray did not look as expected:** SmokeD's tooltip change was accepted by Explorer
   (`MIRROR MODIFY … explorer=1`) but the test did not read it back from Explorer's toolbar, and after
   SmokeA was hidden there Explorer still showed a visible button for it in the flyout (likely a second
   one). Explorer's state on this machine at the time, not ours.
4. **exeIcons** failed with the settings applied at once; the log does not say which icon. Explorer
   had an extra icon in this run ("apps are using your microphone").
5. **The icon rules check** (SmokeC in the flyout, SmokeD hidden, SmokeB's tooltip replaced) failed
   although the settings were applied at once. The log does not say which part; not explained.

The rerun passed everything, so 2 to 5 were the environment of the first run, as far as can be told.

Two more runs with the watcher logging and the Shift+right-click fix: each failed one check, a
different one each time, and all checks passed across the runs.

- **"Dragging an icon onto another"** failed with SmokeB in the right place. During a drag the tray
  shows the preview arrangement, so the wait for SmokeB's place could end before the drop saved the
  order, and the saved order was then read too soon. A race in the test: it now waits for the place
  and the saved order together. The check after it (dragging onto the chevron, then reading the saved
  rule) had the same race and was fixed the same way.
- **exeIcons** failed again in one run and passed in the other. The code shows no timing cause, and
  the check said nothing about which icon was off; it now lists them when it fails. Open.

## After Phase 6: layout setting and expanding grid

Date: 2026-10-20. Windows 10 Pro 22H2 (build 19045), same machine. The layout (columns × rows) left the
theme and became a setting of its own; a grid with both sizes set expands in place from its chevron
instead of opening the flyout (PLAN.md, Decisions).

| Check | Result |
|---|---|
| Unit tests (306, new: `IconGridTests`, grid capacity in arrangement, settings migration) | ✅ |
| Smoke test, full (with mouse) | 109 passed, 1 failed: "icons without a rule in the hidden icons". The log shows the settings write was picked up about 5 s late (22:44:59), just after the check's 5 s wait; applied, it was right. Not related to the layout change. |
| Smoke test again, `-SkipMouse` | ✅ 74 of 74, that check included. |
| Layout 1 × 0, and Glass in 4 × 0 | ✅ A column with the anchor corner kept; rows of four. |
| Layout 2 × 1 | ✅ One icon and the chevron; the rest behind it. |
| Chevron in a fixed grid (mouse) | ✅ The tray expands with the other icons in rows of the same width, the chevron stays at the same screen position (tray anchored bottom-right: it grows upwards); a second click collapses it. |

Not tested: dragging icons within an expanded grid, a grid whose own cells are not all used (it
shrinks to fit, so expanding can widen it and move the chevron), the settings window's new boxes by
hand.

## Phase 6: robustness and polish

Date: 2026-09-26. Same machine and setup as before (Windows 10 Pro 22H2, build 19045.7725, one
1920×1080 monitor at 100%, ManagedShell 0.0.374 on .NET 10, Open-Shell's Start menu). Real tray apps
running: PowerToys (and Awake), Command Palette, Launchy, Directory Opus, Listary, Google Drive,
qBittorrent, Windows Security, and Explorer's Bluetooth, battery, network, volume and (hidden) Meet
Now; Task Manager, elevated, for the tests of an elevated app. How to use the new parts is in
[README.md](README.md).

### Phase 6 verdict

**The tray is robust against Explorer, against itself, and against apps.** Unit tests (296), the
smoke test (107 checks, 18 of them new) and a new Explorer restart test (20 checks) pass on the
Release build. In detail:

- **Explorer restarting while we host, then an exit or a crash** (the cases that broke the hand-back
  with ManagedShell as shipped): Explorer's tray has every icon again afterwards, its own included,
  also those of an app that does not register again after TaskbarCreated (we give them to it).
- **The host role** is taken back 1 ms after Explorer's taskbar goes above ours (it was up to
  ManagedShell's 100 ms check, and 215 ms was measured), and icon changes that reached only Explorer
  meanwhile are caught up on.
- **Starting no longer reverses Explorer's hidden-icons order.**
- **Tooltips and popups as in Explorer** (measured first; finding 2).
- **Crash safety:** after a crash the watchdog starts the tray again (at most three times in a row
  while it keeps crashing soon after starting); a hung tray is ended after about 25 s.
- **A portable build** (`tools/Publish.ps1`).
- **Real apps,** including elevated Task Manager, work from the tray (checklist below).

The portable build had one bug the other builds cannot have (finding 6), found by the smoke test
against it and fixed; after the fix I could check it only by hand (a hover, a click, an exit),
because Claude Code's permission check refused to let me run the smoke test against the published
exe. **A full smoke-test run of the portable build is still open**; the command is under
[Checking the portable build](#checking-the-portable-build).

### What Phase 6 built

- **Import without "hide inactive icons".** `ManagedShellTrayHost` reads Explorer's three tray
  toolbars before it takes the role and hands the icons to ManagedShell during its `Initialize()`,
  when ManagedShell creates its `TrayNotifyWnd` child: after our window exists (so ManagedShell
  forwards what it rejects to Explorer, finding 4), before TaskbarCreated (so the imported icons come
  first, in Explorer's order). ManagedShell's own import, which switches the setting off and on, is
  skipped: it runs only while `EnvironmentHelper.IsAppRunningAsShell` is false, and that flag is set
  for the moment between the start of `Initialize()` and the creation of our window.
- **`HostRoleKeeper`**, a thread with a WinEvent hook: on every `EVENT_OBJECT_REORDER` of the desktop
  window it checks which `Shell_TrayWnd` is highest and raises ours (posted, so it never waits for the
  tray thread), checks again every 5 ms until ours is back, and reports the gap. At most 40 raises a
  second, then one every 250 ms, so a shell that insists is not fought in a busy loop.
- **Catching up after a gap (`GapSync`).** Explorer's tray is read once the gap is over and compared
  with a read from before it: an icon added, deleted or changed (tooltip, icon, hidden, callback,
  version) in Explorer's tray since then, where ours disagrees, happened during the gap and is applied
  to ours. Changes made through us before the gap show up too but agree with ours. After an Explorer
  restart only additions count.
- **Explorer restart recovery.** When Explorer's taskbar is new, its tray gets, 4 s later, every icon
  of ours it still lacks, as a full add built from our state (as the mirroring fix-up does).
- **Icons of dead windows** are removed from both trays (read every 3 s); Explorer and ManagedShell
  both remove them only when the pointer passes over them. An icon with a GUID gets 10 s, since its app
  (or Explorer, for its own icons) may register the same GUID again from a new window (finding 7).
- **Hovering as Explorer does:** WM_MOUSEMOVE when the pointer arrives; for version 4 icons without
  NIF_SHOWTIP, NIN_POPUPOPEN after the double-click time and no tooltip of ours, NIN_POPUPCLOSE when
  the pointer leaves, a button goes down, or the tray hides; no WM_MOUSEHOVER or WM_MOUSELEAVE.
  NIF_SHOWTIP is noted from every add and modify. Our tooltips appear after the double-click time too
  (WPF's default was 1 s). A rule's tooltip is always shown.
- **The watchdog:** starts the tray again after a crash (`CrashPolicy`: an unhandled exception, an
  access violation or fail-fast, not a process someone ended; at most 3 times in a row within 10
  minutes of starting; not while Windows ends the session; setting `restartAfterCrash`), with the same
  arguments. It probes the tray thread with WM_NULL every 5 s and ends a tray that does not answer 4
  probes in a row (about 25 s), which then counts as a crash. Not while a debugger is attached.
- **Graceful shutdown and logging:** the session ending is logged (WPF exits the app, and the tray
  is handed back); unobserved task exceptions are logged; restarts are logged by both processes.
- **Portable build:** one self-contained exe (73 MB, 67.5 MB zipped) with the themes, `portable.txt`
  (settings, themes and logs then live in a `data` folder next to the exe), the README and the
  licenses. `licenses/` holds ManagedShell's Apache-2.0 text and the third-party notices.
- **Tests:** the Explorer restart test (`tests/smoke/Invoke-ExplorerRestartTest.ps1`); 18 smoke
  checks; `--debug-crash` and `--debug-hang` for the watchdog checks (only an instance started with
  `--diag` obeys them); `--no-own-import`, `--no-role-keeper`, `--no-explorer-sync` for comparisons;
  TestTrayApp with several icons, hiding, and ignoring TaskbarCreated; SpikeTool `watch-zorder`,
  `raise-explorer`, `watch-tooltips`, `explorer-tip`, `notify`, and `raise` after the `explorer-*`
  commands.

### Phase 6 results

| Check | Result |
|---|---|
| Explorer restarts while we host, then we exit | ✅ Twice. Explorer's new tray had all 18 (later 31) icons again 4 s after the restart, our own too; after our exit every icon it had before, Windows Security included. |
| The same, then we crash (killed) | ✅ Twice. The watchdog handed back; every icon there, with the latest tooltip; our dead icon removed. |
| An app that ignores TaskbarCreated, during an Explorer restart | ✅ Explorer's new tray got it from us ("Gave Explorer's new tray 1 icon it lacked"); without us it would have lost it. |
| Explorer's own icons after its restart | ✅ Updated in place in our tray (their GUIDs came back from new windows within 2 s). Before the grace period they were removed as dead 1.5 s before that and came back at the end of the tray. |
| Explorer's taskbar raised above ours | ✅ Ours on top again 0 to 1 ms later, in every smoke run and both Explorer restarts. |
| A tooltip, an add and a delete sent to Explorer only, then a gap | ✅ All three caught up on within about 5 ms of the gap's end. |
| Starting: Explorer's hidden-icons order | ✅ Unchanged, checked in every smoke run (13 to 15 icons in the flyout). |
| An app killed without deleting its icons | ✅ Gone from both trays within 3 s. |
| Hover: v4 without NIF_SHOWTIP, v4 with it, v0 | ✅ As in Explorer (finding 2): NIN_POPUPOPEN after 500 ms and no tooltip; NIN_POPUPCLOSE on leave, before a click's button-down, and when the tray hides; with NIF_SHOWTIP and v0 our tooltip and no popup messages. |
| A real crash (`--debug-crash`) | ✅ Exit code 0xE0434352, handed back, started again 1 s later. |
| Killed on purpose (`Stop-Process`, exit code −1) | ✅ Handed back, not started again. |
| Hung (`--debug-hang 120`) | ✅ Ended 23 to 39 s after it stopped answering (39 s before the probes were made faster), handed back, started again. |
| Crash loop | ✅ Seen for real: the first portable build crashed 4 times, each soon after starting; the watchdog started it again 3 times, then stopped. |
| Portable build | ⚠️ Crashed on the first hover (finding 6); fixed. After the fix, checked by hand only. |
| Real apps' menus from our tray (13 icons) | ✅ All opened next to their icon and closed with Esc (checklist). |
| Elevated Task Manager | ✅ Its 13 icons reach our tray and Explorer's; its callback message passes UIPI; our import picks it up. ⚠️ A click reached it (it took the foreground), but its minimized window did not restore (finding 9). |

### Phase 6 findings

1. **When Explorer's taskbar gets the foreground, it goes above our `Shell_TrayWnd`.** Measured with
   `SpikeTool watch-zorder`: closing the volume flyout (opened from Explorer's tray) gave the taskbar the
   foreground and the role for 50 ms; opening Open-Shell's Start menu for 215 ms and 14 ms, closing it
   for 50 ms. ManagedShell's 100 ms check ends these gaps; its timer runs at Background priority, which
   is how 215 ms happened. Explorer never kept its taskbar on top once ours was raised; Phase 1's "up
   to about 2 s" was most likely several gaps within one tick of the 500 ms check that logs the role.
   Every change of the top-level Z-order fires `EVENT_OBJECT_REORDER` on the desktop window, in the same
   millisecond, so the tray reacts to that now.
2. **How Explorer's tray treats hovering** (measured with the test app in Explorer's own tray):
   - Version 0: WM_MOUSEMOVE when the pointer arrives (and again as the tooltip appears); the standard
     tooltip after about 0.6 s; nothing when the pointer leaves.
   - Version 4 without NIF_SHOWTIP: WM_MOUSEMOVE, then NIN_POPUPOPEN 500 ms later (the double-click
     time), with the pointer's position; no tooltip (Explorer's tooltip window has the text but is 0×0);
     NIN_POPUPCLOSE, with position 0,0, when the pointer leaves. A button press sends NIN_POPUPCLOSE
     before the button's message, and the popup does not open again until the pointer comes back.
   - Version 4 with NIF_SHOWTIP: the standard tooltip, and no NIN_POPUPOPEN at all.
   - NIF_SHOWTIP counts from each NIM_ADD and NIM_MODIFY: a later tooltip change without it, and even
     an icon change without it, turns the standard tooltip off. NIM_SETVERSION leaves it alone.
   - Explorer sends no WM_MOUSEHOVER or WM_MOUSELEAVE. ManagedShell sent WM_MOUSEHOVER and NIN_POPUPOPEN
     the moment the pointer arrived, and WM_MOUSELEAVE and NIN_POPUPCLOSE when it left, to every icon.
   Every version 4 app on this machine (Windows Security, Google Drive, qBittorrent, Explorer's own
   icons) sets NIF_SHOWTIP, so their tooltips look as before; they no longer get popup messages that
   Explorer never sends them.
3. **Explorer marks hidden icons with bit 0 of its per-icon state**, whether the app hid its icon
   (state 0x400001) or Windows hides it (Meet Now: 0x200001). The import takes that bit as the icon's
   hidden state, as ManagedShell's import did.
4. **ManagedShell forwards the messages it rejects to "the other" `Shell_TrayWnd`**, which it finds by
   skipping its own window. Until `CreateWindowEx` returns, it does not know its own window, and could
   find itself (created topmost, it is already the highest), keep that as the forwarding target and
   forward to itself from then on. So the import waits until ManagedShell creates its child window.
5. **ManagedShell's `Initialize()` runs its import only while `IsAppRunningAsShell` is false**, and
   starts the shell service objects (volume, network, power, in our process) only while it is true.
   Setting the flag for the import and clearing it as our window is created skips one without the other.
6. **ManagedShell uses WinForms, but its NuGet package declares only the WPF framework.** An app on
   the installed .NET Desktop Runtime has WinForms anyway; a self-contained one does not, and crashed
   (`FileNotFoundException: System.Windows.Forms` in `MouseHelper.GetCursorPositionParam`) the first
   time the pointer reached an icon. `UseWindowsForms` in `FreeSystemTray.TrayHost` fixes it (+10 MB).
7. **After an Explorer restart, Explorer's own icons are dead for about 1.5 s** before the new Explorer
   registers them again (same GUIDs, new windows). Removing dead icons at once put them at the end of
   the tray; GUID icons now get 10 s.
8. **SMTO_ABORTIFHUNG does not cut a probe of our hidden `Shell_TrayWnd` short** once the tray thread
   hangs: each probe took its full 5 s timeout. The watchdog probes again 1 s after an unanswered probe.
9. **Task Manager registers 13 icons from one window**: the visible one (uID 0, CPU, memory, disk and
   network in its tooltip) and 12 it hides itself (uIDs −1 to −12). The tray shows the visible one, as
   Explorer does. Elevated, it sends its icon messages to our non-elevated host without trouble, and
   its callback message passes UIPI from a non-elevated process while other messages are refused
   (`SpikeTool notify`: 0x465 went through, 0x8765 gave error 5), so clicks from the tray reach it the
   same way they reach it from Explorer (also not elevated). No option to run elevated is needed for
   this. A click took it to the foreground, but its minimized window did not restore, after a click or
   a double-click; I did not get a clean comparison with Explorer's tray, so whether that is Task
   Manager's behaviour is open.
10. **While an elevated window has the foreground, simulated input from a non-elevated process does
    not arrive** (UIPI). One smoke-test run lost 15 mouse and keyboard checks that way, with Task
    Manager left in the foreground by the test before; the next run passed.
11. **Exit codes the watchdog sees:** an unhandled .NET exception 0xE0434352; Task Manager's End task
    and `taskkill /f` 1; PowerShell's `Stop-Process` −1 (0xFFFFFFFF).
12. **Google Drive's one-item menu opens below the pointer**, over Explorer's taskbar, rather than above
    the icon; it does not look where the taskbar is. Every other app's menu opened above its icon.
13. **ManagedShell ignores a NIM_DELETE that names an icon by its GUID alone** (window 0): its delete
    matches the window and uID only. Seen in its source, not tested; the catch-up's deletes carry both.

### Candidates for ManagedShell upstream

Each would let us delete a workaround: reading Explorer's hidden-icons toolbar directly instead of
switching "hide inactive icons" (finding 5 and Phase 0); declaring the WinForms framework in the
package (finding 6); not forwarding before it knows its own window (finding 4); Explorer's hover
behaviour (finding 2); GUID-only deletes (finding 13); and, from Phase 0, forwarding the messages it
handles (mirroring).

### Checklist of real apps

From the tray's window, on this machine. Phase 0 used the spike; Phase 6 the finished app.

| App | Icon | Phase 0 | Phase 6 |
|---|---|---|---|
| PowerToys | v0 | ✅ appears, right-click menu | ✅ menu above the icon |
| PowerToys Awake | v0 | ✅ appears | ✅ menu |
| Windows Command Palette | v0 | ✅ appears | ✅ menu (WinUI app, Win32 menu); a click and a double-click by mistake toggled its window |
| Listary | v0 | ✅ menu | ✅ menu |
| Launchy | v0 | ✅ Qt menu | ✅ Qt menu |
| Directory Opus | v3, GUID | ✅ custom menu | ✅ its own popup menu |
| qBittorrent | v4, NIF_SHOWTIP | not running | ✅ Qt menu |
| Google Drive | v4, GUID, NIF_SHOWTIP | ✅ appears | ✅ one-item menu, below the pointer (finding 12) |
| Windows Security | v4, GUID, NIF_SHOWTIP | ✅ menu | ✅ menu; restored by the full add after both Explorer restarts |
| Explorer: volume, network, battery, Bluetooth | v4, GUID | ✅ menus and flyouts | ✅ menus; kept their places across Explorer restarts |
| Explorer: Meet Now | v4, GUID | ⚠️ shown | ✅ hidden (Phase 5), imported as hidden |
| Task Manager (elevated) | v0, 13 icons | not tested | ✅ icons, import, callback through UIPI; ⚠️ minimized window not restored by a click (finding 9) |
| Discord | v0, GUID | ✅ Electron menu | not running this time |
| TestTrayApp (v0, v4, v4 with NIF_SHOWTIP, several icons, ignoring TaskbarCreated) | | ✅ | ✅ smoke test and Explorer restart test |

Not available here: Steam, Dropbox, an antivirus other than Windows Security, VPN clients, OEM tools
with tray icons. Balloons were checked with the test app only (mirrored; Windows shows them as
notifications); no real app showed one on demand.

### Checking the portable build

Not done by me after the fix (Claude Code's permission check refused it):

```bash
powershell -ExecutionPolicy Bypass -File tools\Publish.ps1
powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-SmokeTest.ps1 -Configuration Release -AppPath dist\FreeSystemTray-0.1.0-win-x64\FreeSystemTray.exe
```

Expected: all 107 checks pass, as with the Release build.

### Not tested in Phase 6

- The full smoke test on the portable build after the WinForms fix (above).
- The watchdog at a real sign-out or shutdown (it checks `SM_SHUTTINGDOWN` before starting the tray
  again), and a real crash in normal use.
- Windows 11, multiple monitors and mixed DPI (as before).
- Discord, Steam, Dropbox, VPN clients, other antivirus and OEM tray apps (not installed or not running).
- A long run of the finished Phase 6 build.

### Phase 6: side effects on this machine

- **Your running FreeSystemTray** (the Debug build you started at 18:11) was closed with `--exit` at
  18:46 for the tests, and started again with the new Debug build at the end of the session.
- Explorer was restarted 4 times by the Explorer restart test (18:53 and 19:54, twice each), with no
  File Explorer windows open. Each time the taskbar disappeared for a few seconds.
- Explorer's volume flyout and Open-Shell's Start menu were opened once each and closed with Esc.
- Explorer's hidden-icons flyout was opened a few times by the hover probes; Explorer's tray order may
  have changed through its restarts (our own starts leave it alone).
- The right-click menus of 13 real apps were opened from a test tray and closed with Esc. Windows
  Command Palette's tray icon got a click and a double-click by mistake, twice (a position in
  Explorer's closed flyout lies over the tray); its window ended hidden, as before.
- **Task Manager was started, elevated and minimized, and is still open**: a non-elevated process
  cannot close it. Please close it.
- Simulated input as in earlier phases, always after 1.5 s without yours; test trays crashed and hung
  on purpose; up to 14 test icons at a time, and more "TestTrayApp" entries in Windows' notification
  icon history (uIDs 105 to 108, 201 to 204, 301 to 315, 401, 402, 499, 501); "Balloon from SmokeA"
  notifications in Action Center, one per smoke run.
- `dist\` (the portable build) was created in the repository folder; it is in `.gitignore`.
- All test runs used settings and logs in temporary folders. `%LOCALAPPDATA%\FreeSystemTray` is yours
  now; the tests did not write to it (the `--exit` that closed your instance logged elsewhere).

## Phase 5: management features

Date: 2026-09-26. Same machine and setup as before (Windows 10 Pro 22H2, build 19045.7725, one
1920×1080 monitor at 100%, ManagedShell 0.0.374 on .NET 10). Real tray apps running: PowerToys (and
Awake), Command Palette, Launchy, Directory Opus, Listary, Google Drive, Windows Security, Bluetooth,
battery, network, volume. How to use the new features is in [README.md](README.md) ("Arranging
icons", and the list at the top).

### Phase 5 verdict

**Management works.** Unit tests (266) and the smoke test (86 checks, 18 of them new) pass. By hand,
with screenshots: the hidden-icons flyout, the "as in Windows" default, dragging icons (also a real
app's, Listary), the Shift+right-click menu, and the settings window's four tabs, including changing
an icon's rule, renaming and moving an icon, and capturing a hotkey.

Two things were checked only in part, because the full check changes this machine:

- **Meet Now itself.** Explorer registers it with the tray host only when Explorer restarts, and I
  did not restart Explorer this time. The mechanism was checked by making Explorer alone hide a test
  icon, which is what Windows does with Meet Now (finding 2). How to see the real case is under
  [Checking Meet Now](#checking-meet-now).
- **Autostart.** Tested against a registry key of the tests' own; your Run key was not changed. See
  [Checking autostart](#checking-autostart).

### What Phase 5 built

- **Icon rules** in `settings.json`: per icon (or per exe) a placement (the tray, the hidden-icons
  flyout, or nowhere), a name and a replacement tooltip (`{tip}` for the app's own); the most exact
  rule wins per value. A saved order, kept for apps that are not running. A default placement for
  icons without a rule (`Tray`, `Overflow`, or `Windows`: as Explorer has them), a limit on the icons
  in the tray (the rest spill into the flyout), and hiding the icons Windows hides. Icons are named
  by their GUID or by "exe:uID", not by full path, which changes with every update of apps that
  install into versioned folders. The arrangement is pure logic (`Icons/`), unit-tested.
- **Where Explorer shows each icon:** `ExplorerTrayIcons` reads Explorer's three toolbars (taskbar,
  system-promoted, flyout) from Explorer's memory, the way ManagedShell's import does, on a
  thread-pool thread, 300 ms after our icons change and every 3 s. `ITrayIcon.ExplorerPlacement` is
  Taskbar, Overflow, Hidden, or Unknown (Windows 11 has no such toolbars).
- **The hidden-icons flyout** (`OverflowWindow`): a chevron at the end of the tray opens it, next to
  the tray on the side away from the nearest screen edge, not past the tray's ends. It is a
  non-activating tool window like the tray; it closes on a click elsewhere or a switch to another
  window (except in a menu or flyout an icon opened, the rule auto-hide uses), and when the tray hides
  or is moved. The pointer on it counts as on the tray for position queries
  (`ITrayHost.SetOverflowBounds`), and it takes the theme's blur.
- **Dragging icons** (`IconDrag`): a left-button drag moves an icon instead of clicking it: over
  another icon it takes that place, onto the chevron it goes into the flyout (which opens), and back.
  Icons move while dragging (a preview of the settings the drop saves). The app hears no click.
- **Shift+right-click** on an icon opens the tray's own menu for it: automatic, always in the tray,
  in the hidden icons, hidden, and its name and tooltip (the settings window).
- **The settings window**: Icons (every icon with its rule and where it is, name, tooltip, move
  up/down, forget; the defaults), Appearance (theme, sizes, snapping, scaling), Show and hide (a
  hotkey captured from the keyboard, auto-hide, hotspot, animation), General (autostart, the app's
  own icon, position, files). Changes apply and save at once; edits elsewhere show up in it.
- **FreeSystemTray's own icon** (drawn by `tools/Make-AppIcon.ps1`, also the exe's icon), registered
  with `Shell_NotifyIcon` like any app's, so it shows in the tray and, mirrored, in Explorer's: a
  left-click opens the settings, a right-click the app menu, which the grip now shares. The menu offers
  "Show the tray" when it is hidden.
- **Autostart**: the user's Run key, read together with Task Manager's StartupApproved key (an entry
  Task Manager turned off is off; ticking the box turns it back on). `--autostart` waits for
  Explorer's taskbar at sign-in.
- **Single instance**: starting the exe again, or `--open-settings`, shows the running tray and its
  settings.
- **Watchdog**: after a crash it also removes the icons of windows that no longer exist from
  Explorer's tray (FreeSystemTray's own).
- Themes: `foreground`, the chevron's colour (dark on Light).
- Smoke test: 18 checks. SpikeTool: `explorer-hide` (Explorer alone hides or shows an icon) and
  `click X Y KIND shift`.
- **Not built:** balloon notifications in our own style (optional in the plan; Windows shows them
  as notifications through mirroring), and fitting the tray to its monitor automatically (the
  icon limit does it by hand).

### Phase 5 results

| Check | Result |
|---|---|
| Rules from the settings file (flyout, hidden, tooltip, order, limit) | ✅ Applied within about 0.3 s. A hidden icon keeps registering: its tooltip changes reach our host and Explorer's tray. |
| Default "as in Windows" | ✅ Battery, network and volume in the tray (plus an icon with a tray rule), the other 10 in the flyout: Explorer's layout. |
| An icon Windows hides (simulated: Explorer alone hides a test icon) | ✅ Hidden 1.9 s later, back 2.9 s after Explorer showed it again (the 3 s read). Explorer's tray reads as 16 buttons, 1 hidden: Meet Now. |
| Flyout | ✅ Opens above the chevron, flush with the tray's right end. A click on an icon in it reaches the app (`NIN_SELECT`, told the icon's rectangle in the flyout); a click elsewhere closes it; the chevron toggles it. |
| Dragging | ✅ To the front of the tray, onto the chevron (the flyout opens meanwhile, and closes when the icon is dropped on the chevron), out of the flyout into the tray. Order and rules saved; the apps saw only pointer moves. |
| Shift+right-click | ✅ The tray's menu; the app gets no right-click. |
| Settings window | ✅ All four tabs; a rule changed in the list moves the icon and re-sorts the list; a name typed and Enter; move down; hotkey Ctrl+Alt+Shift+Y captured and registered, then T again. |
| FreeSystemTray's own icon | ✅ In the tray and Explorer's; gone from Explorer's after an exit; after a kill the watchdog removed it ("removed 1 icon of windows that no longer exist"). |
| Starting the exe again | ✅ Exit code 0; the running instance showed its settings. |
| Autostart | ✅ Unit tests on a test key: on, off, turned off in Task Manager and back on, the command line. Not on the real Run key. |

### Phase 5 findings

1. **Explorer keeps a hidden button for icons it hides.** Meet Now (turned off on this machine) is
   registered by explorer.exe (GUID `7820ae83-…`) and sits in Explorer's flyout toolbar with the
   toolbar's hidden state. That state is the only general sign of "Windows hides this icon" I found,
   so the tray reads it. ManagedShell's import reads the same bit, but only for the icons it imports
   at start; when Explorer registers Meet Now again after a restart, it arrives without a hidden state
   (Phase 0 logs: `hidden=False`), which is why Phase 0 showed it.
2. **Explorer can hide an icon that the tray host shows.** `NIM_MODIFY` with `NIS_HIDDEN`, sent to
   Explorer's tray alone, hides its button while the host's copy stays visible. The smoke test uses
   this (`SpikeTool explorer-hide`) to stand in for Meet Now.
3. **An app can register its tray icon with itself as the host.** FreeSystemTray's own icon goes to
   the highest `Shell_TrayWnd`, its own (on the same thread, so it is handled at once), and is
   mirrored like any other. After a crash, Explorer's copy belongs to a window that no longer exists;
   Explorer drops such icons only when the pointer passes over them. `NIM_DELETE` with the dead
   window's handle and uID removes it, so the watchdog does that.
4. **The diagnostics writer could crash the app.** Replacing `state.json` fails with "access denied"
   (not an `IOException`) while a reader has the file open; the smoke test hit this once. Fixed; it
   only affected `--diag`.
5. **Dragging into a tray anchored at the right moves the target.** An icon dragged in from the
   flyout makes the tray one cell longer to the left, so the icons under the pointer shift by one
   cell during the drag. The icon lands where the pointer ends, which matches the screen but can be a
   place further than the icon first aimed at.
6. **Some keys have two names** (`OemTilde` and `Oem3`, `PageDown` and `Next`); the settings window
   writes the readable one.

### Phase 5 follow-up: menus that closed at once

Found by you right after Phase 5: the menus of the grip and of FreeSystemTray's own icon closed as
soon as they opened. Fixed the same day; the smoke test now has 89 checks (3 new) and passes, and the
unit tests 267.

1. **A WPF menu closes at once if the foreground is taken, after it opened, with a window it does not
   belong to.** To get the keyboard (Esc, arrows), the rewritten app menu activated its own popup
   window once it was open; the Shift+right-click menu activated the tray window, which is fine in the
   tray but would have closed it in the flyout. A small WPF test app of its own showed the rule: taken
   in `Opened` with the popup's window, or with a window other than the menu's (a hidden one, as for
   FreeSystemTray's icon while the tray is hidden), the menu closed after about 150 ms; taken before the
   menu opens, with any of these windows, it stayed open and took Esc. All of the tray's menus now
   take the foreground with the tray window before they open. The Phase 1 grip menu took it in
   `Opened`, with the window it belonged to, which is why it worked.
2. **An auto-hiding tray shown by the hotkey could stay shown after a quick click.** Its timer starts
   once the pointer has been over the tray at one of its checks (every 200 ms); a right-click on an
   icon and a move away between two checks never counted. A click in the tray now counts as a visit.
   The smoke test showed it once, when its timing happened to fall between two checks.
3. **Windows refuses a test window the foreground while you are working** (the foreground lock, 200 s
   after your last input in another app). One smoke-test run failed its full-screen checks that way,
   while you were typing in another window; the next runs passed.

### Checking Meet Now

Not done by me, because it needs an Explorer restart:

1. Keep Meet Now turned off (Settings › Personalization › Taskbar › Turn system icons on or off).
2. Start FreeSystemTray, then restart Explorer (Task Manager › Windows Explorer › Restart) with no
   folder windows open.
3. Expected: Meet Now is not in the tray; the settings window's Icons tab lists it as "hidden
   (Windows hides it)". Before Phase 5 it appeared (Phase 0).

### Checking autostart

1. Settings › General › tick "Start FreeSystemTray when I sign in". Task Manager's Startup tab lists
   FreeSystemTray, enabled.
2. Sign out and in: the tray appears; its log says "Started at sign-in".
3. Untick the box to remove the entry.

### Not tested in Phase 5

- Meet Now itself and autostart at a real sign-in (above).
- Multiple monitors and mixed DPI for the flyout, dragging and the settings window.
- Windows 11: Explorer's tray has no toolbars to read there, so where Explorer shows an icon is
  unknown, `Windows` places icons like `Tray`, and icons Windows hides are not recognised.
- Apps whose uID changes from run to run: their "exe:uID" rules would not match again (a rule for the
  exe name would).
- Keyboard use of the tray and the flyout; touch.

### Phase 5: side effects on this machine

- Screenshots of the tray, the flyout, the menus and the settings window are in a temporary folder.
- Simulated input, after 1.5 s without yours: clicks on the chevron and on icons in the flyout, drags
  of test icons and of Listary (in a temporary settings file; Listary is unchanged), Shift+right-clicks,
  and keys typed into the settings window (the name "Alfa"; Ctrl+Alt+Shift+Y as the hotkey for a
  moment, then T again). Smoke-test runs as before.
- The settings window opened in front of your work a few times, and took the keyboard focus.
- Explorer was told to hide a test icon for a few seconds, twice, and to show it again.
- The unit tests created and deleted `HKCU\Software\FreeSystemTray.Tests`. The Run and
  StartupApproved keys were not changed: there is no FreeSystemTray entry.
- Windows' notification icon history has a "FreeSystemTray" entry and more "TestTrayApp" ones.
- No Explorer restarts. All runs used settings and logs in temporary folders;
  `%LOCALAPPDATA%\FreeSystemTray` still does not exist. Explorer is the tray host, always-on-top.

## Phase 4: appearance and themes

Date: 2026-09-26. Same machine and setup as before (Windows 10 Pro 22H2, build 19045.7725, one
1920×1080 monitor at 100%, ManagedShell 0.0.374 on .NET 10). Windows' **"Transparency effects" is
off** on this machine (`EnableTransparency = 0`), which matters for the blur (finding 1). Themes are
described in [README.md](README.md), "Themes".

### Phase 4 verdict

**Themes work.** Unit tests (203) and the smoke test (68 checks, 9 of them new) pass, and screenshots
of every built-in theme look as intended, over a plain background and over a striped window placed
behind the tray. **Not seen: a real blur.** With transparency effects off, Windows draws a blur or
acrylic as solid black, so the tray now uses the theme's colour, opaque, instead, as Windows' own
blurred surfaces do. How to see the blur is under [Checking the blur](#checking-the-blur).

### What Phase 4 built

- Theme files (JSON): only the values that differ, with `basedOn` for the rest, down to the built-in
  Dark look. Invalid values are reported (log, `state.json`) and keep the value underneath; numbers
  are clamped to sensible limits; `basedOn` cycles are detected.
- Eight built-in themes (the plan's sample theme set): Dark, Light, Glass, Acrylic, Minimal, Dark
  column, Glass grid, Gradient (XAML). The user's own themes go in `themes` next to `settings.json`
  and replace built-in ones with the same name.
- What a theme sets: background colour and opacity; blur or acrylic (`SetWindowCompositionAttribute`,
  with a rounded window region so the blur keeps the corners); corner radius; border colour and
  thickness; the grip's colour, size and side; icons in a row, a column or a grid of N columns, with
  spacing; hover and pressed colours and their corner radius; the tooltip's colours, border, corner
  radius, font and padding; and a XAML resource dictionary that can replace the brushes, tooltip
  values and the tooltip style. The theme's sizes scale with the tray size.
- Live preview: the grip menu's Theme submenu switches at once, and edits to theme files apply while
  the app runs (a half-saved file keeps the version in use). `--export-theme` and `--import-theme`.
- The blur is turned off during show/hide animations (it would stay at full strength behind a fading
  or sliding tray), and follows Windows' transparency setting, including when it changes.
- Windows 11: the tray asks Windows not to round its corners (`DWMWCP_DONOTROUND`), since it draws
  its own. Not the Windows 11 backdrops (`DWMWA_SYSTEMBACKDROP_TYPE`): as far as I know they need a
  window that is not layered, and the tray must be layered to fade. Untested, like everything on 11.

### Phase 4 results

| Check | Result |
|---|---|
| Dark, Light, Minimal, Gradient (XAML) | ✅ As designed. Minimal's 85% background lets a striped window behind show through. Gradient's colours come from its XAML. |
| Glass and Acrylic | ✅ Rounded, and solid while transparency effects are off. Before the fallback: solid black behind the tray (finding 1). |
| Dark column, Glass grid (4 columns, also at 200%) | ✅ The grip moves to the top of a column; the tray keeps its anchor corner; icon rectangles follow the layout. |
| Hover and pressed colours | ✅ Pixels over Glass: hover #494949, pressed #686868, as the colours predict. One early screenshot did not show the pressed colour; three later checks did, and I could not reproduce it. |
| Tooltip style | ✅ Dark, rounded, bordered tooltip with the theme's font. |
| Theme submenu | ✅ Chosen with the keyboard; applied and saved. |
| Own theme, live edits, broken edits | ✅ Applied within about 0.3 s; a broken file is reported and the version in use stays. |
| Bad colours, `basedOn` cycle, missing theme | ✅ Reported; the value underneath or the defaults are used. |
| `--export-theme`, `--import-theme` | ✅ Export spells out every value and copies the XAML; import copies both and the running tray switches. A file that is not a theme is refused (exit code 1). |
| Fade and slide with themes | ✅ Sampled at a 2 s duration with Glass (fade) and Dark column (slides out to the right, its nearest edge). |

### Phase 4 findings

1. **With Windows' transparency effects off, a blur or acrylic is drawn as solid black.**
   `SetWindowCompositionAttribute` succeeded, and the tray's semi-transparent background then showed
   black behind it (#181818 over a striped window) instead of the stripes. Windows' own blurred
   surfaces turn solid in that case too. So the tray draws the theme's colour, opaque, while the
   setting is off, and rechecks when Windows broadcasts `WM_SETTINGCHANGE` "ImmersiveColorSet"
   (not seen, because I did not change the setting).
2. **A WPF implicit `ToolTip` style in the window's merged dictionaries reaches the icons' tooltips**,
   and rounded tooltip corners render cleanly. A theme's own XAML, merged after it, can replace it.
3. **Windows reports light mode here** (`SystemUsesLightTheme = 1`), yet Windows' own volume, network
   and battery icons arrive white, so they are hard to see on the Light theme. I did not look into why.

### Checking the blur

1. Turn on Settings › Personalization › Colors › "Transparency effects".
2. Choose Glass or Acrylic from the grip menu's Theme submenu (the tray picks up the setting at once).
3. Expected: what is behind the tray is blurred, and the corners stay rounded. Hiding and showing
   the tray fades a plain semi-transparent tray, and the blur comes back when it is fully shown.

### Not tested in Phase 4

- A visible blur or acrylic, and turning transparency effects on or off while the app runs (see above).
- Windows 11, multiple monitors and mixed DPI.
- Long or multi-line tooltips.

### Phase 4: side effects on this machine

- Screenshots of the tray and a margin around it, which caught parts of whatever was on screen at the
  time, are kept in a temporary folder.
- A striped window of my own was shown behind the tray, without activating it, for a few seconds at
  a time, four times.
- Hovering over and pressing a test icon, and choosing a theme from the grip menu with the keyboard,
  in a temporary settings file. Smoke-test runs as before.

## Phase 3: scaling

Date: 2026-09-26. Same machine and setup as before (Windows 10 Pro 22H2, build 19045.7725, one
1920×1080 monitor at 100%, ManagedShell 0.0.374 on .NET 10). The settings are described in
[README.md](README.md), "Size".

### Phase 3 verdict

**Icon size and tray size work, each at any value, and independently.** Unit tests (172) and the
smoke test (59 checks, 7 of them new) pass. Screenshots at 75%, 100%, 167% (snapped and exact), 200%
(smooth and nearest-neighbour) and 300% look as intended: nearest-neighbour at 200% is crisp and
blocky, smooth is soft, and exact sizes are a little softer than snapped ones.

Not tested: **mixed DPI and moving the tray between monitors**, because this machine has one monitor
at 100% and changing Windows' display scaling is a setting I leave alone. How to check it is under
[Checking scaling at other DPIs](#checking-scaling-at-other-dpis).

### What Phase 3 built

- Settings `iconSize` and `traySize` (a percentage, or pixels), `snapToPixels`, `iconScaling`
  (`Smooth` or `NearestNeighbor`) and `exeIcons`, applied while the app runs.
- `TrayLayout`: the sizes of the icon, its cell, the grip, the border and the margins. A tray-size
  percentage scales everything but the icons; tray-size pixels set the cell, and the padding is what
  the icon leaves free. Limits only for sanity: icons from 4 to 512 px, and a grip at least 4 px wide
  so the tray can still be dragged.
- `IconResampler`: bicubic (Catmull-Rom) or nearest-neighbour scaling of the app's image to the exact
  number of screen pixels it covers, on premultiplied pixels (no dark fringe at transparent edges),
  with the filter widened when shrinking. `ScaledIcon` draws it 1:1 when snapped, and scales it again
  when the DPI changes.
- `exeIcons`: the icon of the app's .exe, extracted at the drawn size (`SHDefExtractIcon`).
- `state.json` lists the layout, and for each icon the pixels it was drawn at and the size of its source.

### Phase 3 results

| Check | Result |
|---|---|
| 200%, 24px icons in 40px cells, 75%, 300% with a 50% tray | ✅ Drawn at 32, 24, 12 and 48 screen pixels; cells and the rectangles apps are told match. |
| 167% | ✅ Snapped: drawn at 27 px. Exact: 26.72 px, drawn from a 27 px image. |
| Window size | ✅ Grows and shrinks around its anchor corner (the bottom-right corner stayed put through every change). |
| Clicks at 200% | ✅ Reach the app, which is told the scaled rectangle. |
| `exeIcons` | ✅ Directory Opus, Listary, Launchy, PowerToys and Google Drive drawn from their 48 px .exe icons at 300%, noticeably sharper. An exe without an icon (TestTrayApp) keeps its tray image. |
| Invalid values | ✅ `"big"` is logged and 100% used; a bare number is pixels. |

### Phase 3 findings

1. **Apps hand over 16, 20, 40 and 48 px images here**, not only 16 and 32 as in Phase 0, although
   the system DPI is 96: Listary, Directory Opus and Explorer's Bluetooth icon send 20 px, PowerToys,
   Command Palette and Google Drive 40 px, PowerToys Awake 48 px. ManagedShell converts them at their
   own size. Most likely some apps size their icons from a DPI they saw earlier. So at 100% the tray
   also shrinks icons (40 to 16 px), which is why the resampler widens its filter when shrinking.
2. **WPF has no bicubic scaling**: its "HighQuality" mode is the Fant filter. Hence the tray scales
   the images itself and lets WPF draw them 1:1.
3. **Layout rounding also rounds element sizes**, not only positions: with `snapToPixels`, 167% icons
   (26.72 px) are 27 screen pixels and their cells 35. That is the "slightly different size" the plan
   expected.
4. **`SHDefExtractIcon` returns `S_FALSE` for an exe without an icon resource** (a .NET app without
   `ApplicationIcon`, such as TestTrayApp). The tray then keeps the app's own image.

### Checking scaling at other DPIs

Not done by me, because it means changing a Windows setting or connecting a second monitor:

1. Set Settings › System › Display › "Scale" to 150% (or connect a monitor with another scale).
2. Start FreeSystemTray with `--diag DIR` and `"iconSize": "200%"`.
3. Expected: the tray looks the same size as before in inches; `state.json` shows `drawnPx` 48 (32 × 1.5)
   and cells of 60 screen pixels; the icons stay sharp. Dragging the tray to a monitor with another
   scale resizes it there, and `drawnPx` follows.

### Phase 3: side effects on this machine

- None beyond the smoke-test runs (the same simulated input and full-screen windows as in Phase 2, and
  one more "Balloon from SmokeA" notification in Action Center per run) and screenshots of the tray
  window, kept in a temporary folder. During the `exeIcons` check, Explorer's own tray icons were
  briefly drawn with Explorer's icon in our window.

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
run and test it is in [DEVELOPMENT.md](DEVELOPMENT.md).

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
