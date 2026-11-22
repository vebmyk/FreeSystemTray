# Developing FreeSystemTray

How to build, test and package FreeSystemTray, and how it works inside. How to use it is in
[README.md](README.md); the architecture and the decisions behind it are in [PLAN.md](PLAN.md), and
the findings from testing (why each shell workaround exists, and a checklist of the real apps
tested) in [FINDINGS.md](FINDINGS.md).

Status: all phases of [PLAN.md](PLAN.md) are done (tray host, window and placement; show/hide;
scaling; themes; management: icon rules, hidden icons, a settings window, autostart; robustness and
a portable build), tested on Windows 10 22H2.

## Build and run

Requires Windows 10 and the .NET 10 SDK. `nuget.config` adds nuget.org for this repository.

```bash
dotnet build FreeSystemTray.sln
dotnet test tests/FreeSystemTray.Tests/FreeSystemTray.Tests.csproj
src/FreeSystemTray/bin/Debug/net10.0-windows/FreeSystemTray.exe
```

Only one instance runs at a time; `FreeSystemTray.exe --exit` closes the running one (do that
before running the smoke tests).

## Portable build

```bash
powershell -ExecutionPolicy Bypass -File tools/Publish.ps1
```

This makes two builds, each a folder in `dist\` and a zip of it:

- `FreeSystemTray-0.1.2-win-x64`: one exe with the .NET runtime inside (about 73 MB; nothing to
  install).
- `FreeSystemTray-0.1.2-win-x64-framework-dependent`: a small exe that needs the .NET 10 Desktop
  Runtime (x64) installed, with a `REQUIRES-DOTNET.txt` that says so.

Next to each exe are the built-in themes, README.md, the third-party licenses, and `portable.txt`,
which makes the copy keep its settings, themes and logs in a `data` folder beside the exe.
`-Only SelfContained` or `-Only FrameworkDependent` builds just one. Before a release, run the
smoke test against the published exes (`-AppPath`).

## Diagnostic command-line options

Besides the options in [README.md](README.md#command-line):

| Option | Effect |
|---|---|
| `--verbose` | Log every tray message and position query, every click and window switch auto-hide sees, and ManagedShell's debug output. |
| `--diag DIR` | Write `state.json` (host status, window visibility, window, icon and hotspot rectangles) to DIR every 250 ms for the smoke test. Implies `--log-dir DIR --verbose`. |
| `--no-watchdog` | Don't start the watchdog process. |
| `--no-mirror`, `--no-topmost-fix`, `--no-autohide-forward`, `--no-query-routing`, `--no-own-import`, `--no-role-keeper`, `--no-explorer-sync` | Turn off one workaround (ManagedShell as shipped), for comparison tests. |
| `--debug-crash`, `--debug-hang SECONDS` | Make the running instance crash, or stop responding for SECONDS, to test the watchdog. Only an instance started with `--diag` does. |

## How it works

[ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache-2.0, pinned to 0.0.374) creates a
hidden `Shell_TrayWnd` above Explorer's, so apps send their icon messages to this app. On top of it,
`ManagedShellTrayHost` subclasses that window to make running next to Explorer safe:

- every icon message is mirrored to Explorer's tray, with a full add when Explorer has lost an icon;
- auto-hide queries are answered by Explorer;
- icon- and taskbar-position queries are answered with this window only when the user is
  interacting with it, so flyouts opened from Explorer's own tray stay at the taskbar;
- Explorer's taskbar gets its always-on-top flag back after ManagedShell takes it away;
- Explorer's own tray is read now and then (on a background thread) to learn where Explorer shows
  each icon, and which ones it hides;
- Explorer's icons are imported at start by reading its tray, without switching off its "hide
  inactive icons" (which reversed the order of its hidden icons);
- when Explorer's taskbar goes above this app's `Shell_TrayWnd` (whenever it gets the foreground),
  a Z-order event raises ours again within about a millisecond, and icon changes that reached only
  Explorer meanwhile are caught up on;
- after Explorer restarts, its new tray gets the icons it lacks, and icons of windows that no longer
  exist are removed from both trays;
- hovering sends apps what Explorer sends them.

All shell code is behind `ITrayHost` in `src/FreeSystemTray.TrayHost`. FINDINGS.md explains why
each workaround is needed.

A second copy of the exe runs as a watchdog (`Watchdog.cs`). It hands the icons back to Explorer if
the tray process dies, restarts it after a crash (not after a deliberate kill, while Windows signs
out, or after three crashes in a row soon after starting), and ends the tray if it stops responding
for about 25 s.

## Smoke test

Run it before every ManagedShell upgrade and after changes to the tray host, show/hide, scaling,
themes or icon management. It builds the solution, starts five test tray apps and the app, and
checks mirroring, the fix-ups, the query answers, the import, taking the host role back and catching
up afterwards, removing icons of dead apps, clicks, tooltips and popups, showing and hiding
(commands, the hidden state, a settings edit, hotspot, hotkey, click-away, the timer, a menu keeping
the tray shown), stepping behind full-screen windows, icon and tray sizes, themes, icon rules and the
hidden-icons flyout (rules, an icon Windows hides, order, dragging, the icon menu), the tray's own
menus staying open, the app's own icon, the settings window, the hand-back to Explorer after an exit
and after a kill, and the watchdog after a real crash and a hang (107 checks).

```bash
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-SmokeTest.ps1
```

For about four minutes it moves the mouse, clicks and drags the test icons, clicks windows of its
own, presses the hotkey, Shift and Esc, and twice covers the primary screen with a full-screen window
of its own for a few seconds; before each step it waits until mouse and keyboard have been left alone
for 1.5 s. `-SkipMouse` leaves those checks out. It also shows one Windows notification from the
balloon check, tells Explorer to hide one test icon for a few seconds, puts Explorer's taskbar above
the tray's for a moment, near the end opens the settings window (which takes the keyboard focus), and
lets a test tray hang for about 25 s. FreeSystemTray must not be running, and no elevated window may
be in the foreground (Windows drops simulated input then). `-AppPath` tests a published exe instead of
the build. The exit code is the number of failed checks.

The Explorer restart test restarts Explorer twice while FreeSystemTray hosts the tray, then exits it
once and kills it once, and checks that Explorer's tray has every icon again each time (20 checks).
It refuses to run while a File Explorer window is open; the taskbar disappears for a few seconds each
time. Run it after changes to the tray host.

```bash
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-ExplorerRestartTest.ps1
```

## Source layout

| Path | What |
|---|---|
| `src/FreeSystemTray.TrayHost` | `ITrayHost` and its ManagedShell implementation: the only code that talks to the shell. |
| `src/FreeSystemTray` | The WPF app: tray window, placement, show/hide (`ShowHide/`), scaling (`Scaling/`), themes (`Theming/`, and the built-in ones in `Themes/`), icon rules and arrangement (`Icons/`), the hidden-icons flyout, dragging icons, the settings window, the app's own icon, autostart, settings, logging, watchdog. |
| `tests/FreeSystemTray.Tests` | Unit tests: placement, message formats, routing and fix-up rules, settings, hotkeys, hotspots, auto-hide rules, full-screen tracking, sizes and icon resampling, themes, the icon grid, icon rules, arrangement and order, where Explorer shows an icon, flyout placement, autostart, catching up with Explorer's tray after a gap, building tray messages, the crash policy. |
| `tests/smoke` | The end-to-end smoke test and the Explorer restart test. |
| `tools/Make-AppIcon.ps1` | Draws `src/FreeSystemTray/FreeSystemTray.ico`. |
| `tools/Publish.ps1` | The portable build. |
| `licenses` | Third-party notices and ManagedShell's license, copied into the portable build. |
| `spike` | Phase 0 feasibility spike, the test tray app and the SpikeTool harness (see [spike/README.md](spike/README.md)). |
