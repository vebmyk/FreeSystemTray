# FreeSystemTray

A free-standing system tray (notification area) for Windows: a small borderless window you can put
anywhere, showing the tray icons of running apps. Clicks, menus, tooltips and flyouts work as they do
in Explorer's tray, and Explorer's own tray keeps working next to it.

Status: Phase 1 of [PLAN.md](PLAN.md) (tray host, window and placement), tested on Windows 10 22H2.
Show/hide, scaling and themes come in later phases. Findings from testing are in
[FINDINGS.md](FINDINGS.md).

## Build and run

Requires Windows 10 and the .NET 10 SDK. `nuget.config` adds nuget.org for this repository.

```bash
dotnet build FreeSystemTray.sln
dotnet test tests/FreeSystemTray.Tests/FreeSystemTray.Tests.csproj
src/FreeSystemTray/bin/Debug/net10.0-windows/FreeSystemTray.exe
```

The tray appears above the bottom-right corner of the primary screen.

- **Move it** by dragging the grip on its left edge. It snaps to screen and work-area edges when
  an edge comes within 12 px (at 100% scaling). The position is saved as the corner nearest a screen
  corner, so a tray at the right edge grows to the left when icons are added.
- **Right-click the grip** to lock the position, turn snapping on or off, or exit.
- Only one instance runs at a time. `FreeSystemTray.exe --exit` closes the running one.

Settings are in `%LOCALAPPDATA%\FreeSystemTray\settings.json` and logs in
`%LOCALAPPDATA%\FreeSystemTray\logs`. A second copy of the exe runs as a watchdog: if the tray process
crashes or is killed, the watchdog hands the icons back to Explorer.

### Command line

| Option | Effect |
|---|---|
| `--settings FILE` | Use another settings file. |
| `--log-dir DIR` | Write logs to DIR. |
| `--verbose` | Log every tray message and position query, and ManagedShell's debug output. |
| `--diag DIR` | Write `state.json` (host status, window and icon rectangles) to DIR every 250 ms for the smoke test. Implies `--log-dir DIR --verbose`. |
| `--exit` | Close the running instance and wait for it to exit. |
| `--no-watchdog` | Don't start the watchdog process. |
| `--no-mirror`, `--no-topmost-fix`, `--no-autohide-forward`, `--no-query-routing` | Turn off one workaround (ManagedShell as shipped), for comparison tests. |

## How it works

[ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache-2.0, pinned to 0.0.374) creates a
hidden `Shell_TrayWnd` above Explorer's, so apps send their icon messages to this app. On top of it,
`ManagedShellTrayHost` subclasses that window to make running next to Explorer safe:

- every icon message is mirrored to Explorer's tray, with a full add when Explorer has lost an icon;
- auto-hide queries are answered by Explorer;
- icon- and taskbar-position queries are answered with this window only when the user is
  interacting with it, so flyouts opened from Explorer's own tray stay at the taskbar;
- Explorer's taskbar gets its always-on-top flag back after ManagedShell takes it away.

All shell code is behind `ITrayHost` in `src/FreeSystemTray.TrayHost`. FINDINGS.md explains why
each workaround is needed.

## Smoke test

Run it before every ManagedShell upgrade and after changes to the tray host. It builds the solution,
starts three test tray icons and the app, and checks mirroring, the fix-ups, the query answers,
clicks, and the hand-back to Explorer after an exit and after a kill (27 checks).

```bash
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-SmokeTest.ps1
```

It moves the mouse and clicks the test icons for about ten seconds (`-SkipMouse` leaves those checks
out), and shows one Windows notification from the balloon check. FreeSystemTray must not be running.
The exit code is the number of failed checks.

## Layout

| Path | What |
|---|---|
| `src/FreeSystemTray.TrayHost` | `ITrayHost` and its ManagedShell implementation: the only code that talks to the shell. |
| `src/FreeSystemTray` | The WPF app: tray window, placement, settings, logging, watchdog. |
| `tests/FreeSystemTray.Tests` | Unit tests: placement, message formats, routing and fix-up rules, settings. |
| `tests/smoke` | The end-to-end smoke test. |
| `spike` | Phase 0 feasibility spike, the test tray app and the SpikeTool harness (see [spike/README.md](spike/README.md)). |

## Third-party code

ManagedShell is licensed under Apache-2.0. A redistributed build must include its LICENSE and keep
its NOTICE and attributions (packaging is Phase 6).
