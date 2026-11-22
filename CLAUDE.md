# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

FreeSystemTray is a free-standing Windows system tray (WPF, .NET 10, Windows 10 22H2) that runs
*next to* Explorer's tray, not instead of it. README.md is the user documentation, DEVELOPMENT.md how to build and test, PLAN.md the
architecture and decisions, FINDINGS.md the per-phase test findings and why each shell workaround
exists. Read the relevant FINDINGS.md section before changing a workaround.

## Commands

```bash
dotnet build FreeSystemTray.sln
dotnet test tests/FreeSystemTray.Tests/FreeSystemTray.Tests.csproj
dotnet test tests/FreeSystemTray.Tests/FreeSystemTray.Tests.csproj --filter "FullyQualifiedName~PlacementMathTests"
src/FreeSystemTray/bin/Debug/net10.0-windows/FreeSystemTray.exe
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-SmokeTest.ps1            # end-to-end, ~4 min, -SkipMouse to skip input
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-ExplorerRestartTest.ps1  # restarts Explorer
powershell -ExecutionPolicy Bypass -File tools/Publish.ps1                           # portable build into dist/
```

- Only one instance runs; `FreeSystemTray.exe --exit` closes the running one. The smoke tests
  require it not to be running, and they drive the real mouse/keyboard and shell.
- Run the smoke test after changes to the tray host, show/hide, scaling, themes or icon management,
  and before any ManagedShell upgrade; the Explorer restart test after tray host changes; the smoke
  test with `-AppPath` against the published exe before a release. Exit code = failed checks.
- `--diag DIR` writes `state.json` every 250 ms (used by the smoke tests); `--verbose` logs every
  tray message. `--no-mirror`, `--no-role-keeper`, etc. switch off single workarounds for comparison.
  User options: README.md "Command line"; diagnostic ones: DEVELOPMENT.md.

## Architecture

- `src/FreeSystemTray.TrayHost`: `ITrayHost` and its ManagedShell implementation
  (`ManagedShellTrayHost`). This is the **only** code that talks to the shell; keep shell logic
  here and out of the UI. ManagedShell is used unmodified from NuGet, pinned to 0.0.374 — the
  workarounds depend on its internals. Workarounds layered on top (subclassing its hidden
  `Shell_TrayWnd` via a `WH_CBT` hook): mirroring every icon message to Explorer's tray (with
  add/modify fix-ups), forwarding auto-hide queries to Explorer, routing position queries to our
  window only while the user interacts with it, restoring Explorer's topmost flag, keeping the host
  role (`HostRoleKeeper`) and catching up after gaps, importing Explorer's icons by reading its
  toolbars, handling Explorer restarts, and handing icons back (`TrayHandBack`).
- Threading rules in the host: apply ManagedShell changes to the UI with `Dispatcher.BeginInvoke`,
  never block inside its event handlers, compare icons by reference (not `NotifyIcon.Equals`).
  Reading Explorer's tray happens on the thread pool; system-wide hooks for show/hide run on their
  own thread and only forward events.
- `src/FreeSystemTray`: the WPF app. `TrayWindow` is a borderless, topmost, non-activating,
  per-pixel transparent tool window. Icons flow host → `Icons/` (`IconModel`: pure, unit-tested
  arrangement by rules into tray / hidden-icons flyout (`OverflowWindow`) / hidden) → `IconPanel`.
  Subsystems in folders: `Placement/`, `ShowHide/`, `Scaling/`, `Theming/` (+ built-in `Themes/`),
  `Settings/` (JSON, live reload; the settings window edits the same settings), `Logging/`,
  `Diagnostics/`. `Watchdog.cs`: a second copy of the exe that hands icons back to Explorer if the
  tray dies, restarts it after crashes (not after deliberate kills or in a crash loop), and ends a
  hung tray.
- Pure logic is kept separate from Win32 so it can be unit-tested (`tests/FreeSystemTray.Tests`).
- `spike/`: Phase 0 spike plus `TestTrayApp` and `SpikeTool`, which the smoke tests build and use.

## Constraints

- Explorer's tray must keep working while the app runs, after it exits, and after it crashes.
- State the Windows version/build when reporting test results; record findings in FINDINGS.md.
