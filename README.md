# FreeSystemTray

A free-standing system tray (notification area) for Windows: a small borderless window you can put
anywhere, showing the tray icons of running apps. Clicks, menus, tooltips and flyouts work as they do
in Explorer's tray, and Explorer's own tray keeps working next to it.

Status: Phases 1 and 2 of [PLAN.md](PLAN.md) (tray host, window and placement; show/hide), tested
on Windows 10 22H2. Scaling and themes come in later phases. Findings from testing are in
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
- **Right-click the grip** to lock the position, turn snapping or auto-hide on or off, hide the
  tray, or exit.
- **Ctrl+Alt+Shift+T** hides and shows the tray (configurable, see below).
- Only one instance runs at a time. `FreeSystemTray.exe --exit` closes the running one.

Settings are in `%LOCALAPPDATA%\FreeSystemTray\settings.json` and logs in
`%LOCALAPPDATA%\FreeSystemTray\logs`. Edits to the settings file apply while the app runs. A second
copy of the exe runs as a watchdog: if the tray process crashes or is killed, the watchdog hands the
icons back to Explorer.

### Showing and hiding

Hiding only hides the window: apps keep adding and updating their icons, and while the window is
hidden their menus and flyouts open at Explorer's taskbar.

- **Always visible** (the default): the tray stays on screen. The hotkey or the grip menu's **Hide**
  hides it until the hotkey (or the hotspot) brings it back.
- **Auto-hide** (grip menu, or `"autoHide": true`): the tray starts hidden. The hotkey or the hotspot
  shows it, and it hides again when you click somewhere else or switch to another window, or when
  the pointer has been away from it for the hide delay. It stays while a menu or a Windows flyout
  opened from one of its icons is open. Shown by the hotkey, the timer waits until the pointer has
  been over the tray, so it does not vanish before you get there.
- The **hotspot** is an edge or a corner of the tray's monitor: rest the pointer there for
  `hotspotDelayMs` and the tray appears. It does not fire while a full-screen game or video is in front.
- The tray never hides by itself, and **Hide** is greyed out, when nothing could bring it back (no
  working hotkey and no hotspot).
- Show and hide fade or slide unless "Show animations in Windows" is off.

The show/hide part of `settings.json`, with its defaults:

```json
"showHide": {
  "autoHide": false,
  "hideOnClickAway": true,
  "hideDelayMs": 1500,
  "hotkey": "Ctrl+Alt+Shift+T",
  "hotspot": "None",
  "hotspotDelayMs": 300,
  "animation": "Fade",
  "animationMs": 150
}
```

| Setting | Values |
|---|---|
| `hotkey` | Modifiers (`Ctrl`, `Alt`, `Shift`, `Win`) and one key joined with `+`, for example `Win+Shift+F12` or `Ctrl+Alt+5`. Key names are WPF's (`Space`, `PageDown`, `NumPad7`, `OemTilde`, ...). A key that types something needs Ctrl, Alt or Win. `""` for no hotkey. If another program holds the hotkey, the log says so. |
| `hotspot` | `None`, `Left`, `Top`, `Right`, `Bottom` (a 2 px strip along that edge), `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight` (an 8 px square), of the monitor the tray is on. |
| `hideDelayMs` | Auto-hide: how long after the pointer leaves the tray it hides. `0` turns the timer off. |
| `hideOnClickAway` | Auto-hide: hide on a click outside the tray or a switch to another window. |
| `animation` | `None`, `Fade`, or `Slide` (out through the tray's edge that faces the nearest screen edge). |

### Command line

| Option | Effect |
|---|---|
| `--settings FILE` | Use another settings file. |
| `--log-dir DIR` | Write logs to DIR. |
| `--verbose` | Log every tray message and position query, every click and window switch auto-hide sees, and ManagedShell's debug output. |
| `--diag DIR` | Write `state.json` (host status, window visibility, window, icon and hotspot rectangles) to DIR every 250 ms for the smoke test. Implies `--log-dir DIR --verbose`. |
| `--exit` | Close the running instance and wait for it to exit. |
| `--toggle`, `--show`, `--hide` | Show or hide the running instance's tray, for example from a shortcut or a script. |
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

Run it before every ManagedShell upgrade and after changes to the tray host or the show/hide code. It
builds the solution, starts four test tray icons and the app, and checks mirroring, the fix-ups, the
query answers, clicks, showing and hiding (commands, the hidden state, a settings edit, hotspot,
hotkey, click-away, the timer, a menu keeping the tray shown), and the hand-back to Explorer after an
exit and after a kill (46 checks).

```bash
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-SmokeTest.ps1
```

For about a minute it moves the mouse, clicks the test icons and a small window of its own, and
presses the hotkey and Esc; before each step it waits until mouse and keyboard have been left alone
for 1.5 s. `-SkipMouse` leaves those checks out. It also shows one Windows notification from the
balloon check. FreeSystemTray must not be running. The exit code is the number of failed checks.

## Layout

| Path | What |
|---|---|
| `src/FreeSystemTray.TrayHost` | `ITrayHost` and its ManagedShell implementation: the only code that talks to the shell. |
| `src/FreeSystemTray` | The WPF app: tray window, placement, show/hide (`ShowHide/`), settings, logging, watchdog. |
| `tests/FreeSystemTray.Tests` | Unit tests: placement, message formats, routing and fix-up rules, settings, hotkeys, hotspots, auto-hide rules. |
| `tests/smoke` | The end-to-end smoke test. |
| `spike` | Phase 0 feasibility spike, the test tray app and the SpikeTool harness (see [spike/README.md](spike/README.md)). |

## Third-party code

ManagedShell is licensed under Apache-2.0. A redistributed build must include its LICENSE and keep
its NOTICE and attributions (packaging is Phase 6).
