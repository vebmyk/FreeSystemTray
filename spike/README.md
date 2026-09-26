# Phase 0 spike

Three small .NET 10 programs used for the Phase 0 feasibility test. Results are in [../FINDINGS.md](../FINDINGS.md).
`TestTrayApp` and `SpikeTool` also drive the smoke test of the production app (`tests/smoke`).

| Project | What it is |
|---|---|
| `TraySpike` | WPF window (borderless, topmost, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`) that takes the tray-host role through ManagedShell 0.0.374 and lists the icons in a row. All shell code sits behind `Tray/ITrayHost.cs`. Superseded by `src/`; kept as the Phase 0 reference. |
| `TestTrayApp` | A tray icon that logs every callback it receives (protocol version 0 or 4) and shows a context menu. It can be controlled from outside, for testing icon updates, balloons and deletes. |
| `SpikeTool` | Test harness: lists Explorer's own tray icons, the `Shell_TrayWnd` Z-order and visible windows; simulates clicks, drags and keys; broadcasts `TaskbarCreated`; re-registers an icon on behalf of its owner (`replay-add`). Since Phase 1 also: what apps see from appbar queries (`appbar`, `autohide-probe`) and icon-position queries (`iconrect`), messages to Explorer's tray only (`explorer-add`, `explorer-delete`), and Explorer's taskbar child windows (`explorer-tray`). Since Phase 2: any key chord (`key ctrl+alt+shift+t`), waiting until the user leaves mouse and keyboard alone (`wait-idle`), and a harmless window of its own to click "somewhere else" (`click-target`). The command list is at the top of `SpikeTool/Program.cs`. |

`SpikeTool explorer-icons` reads all three of Explorer's tray toolbars. Phase 0's version missed the
"system-promoted" one, where Windows 10 first shows a newly added icon before it moves it to the
hidden icons (FINDINGS.md, Phase 1).

## Build

Requires the .NET 10 SDK. The repository's `nuget.config` adds nuget.org, because this machine's NuGet config only had the Visual Studio offline source.

```bash
dotnet build spike/TraySpike/TraySpike.csproj
dotnet build spike/TestTrayApp/TestTrayApp.csproj
dotnet build spike/SpikeTool/SpikeTool.csproj
```

## Run

```bash
spike/TraySpike/bin/Debug/net10.0-windows/TraySpike.exe --defer-sync --mirror --fix-topmost
```

| Flag | Effect |
|---|---|
| `--log DIR` | Where `spike.log` and `state.json` go (default `%LOCALAPPDATA%\TraySpike`). `state.json` lists every icon with its screen rectangle; the harness reads it. |
| `--x N --y N` | Initial window position in physical pixels. |
| `--icon-size PX` | Displayed icon size (default 16). |
| `--activating` | Leave out `WS_EX_NOACTIVATE`. |
| `--defer-sync` | Update the UI collection after ManagedShell's event returns. Avoids the re-entrancy problem described in FINDINGS.md. |
| `--mirror` | Forward every icon message to Explorer's tray as well, so Explorer stays in sync (FINDINGS.md, "Mirroring"). |
| `--fix-topmost` | Give Explorer's taskbar back its always-on-top flag after ManagedShell removes it. |

Exit: right-click the grey grip on the left edge and choose **Exit TraySpike**. A graceful exit broadcasts `TaskbarCreated` so apps re-register with Explorer.

If something goes wrong (for example the spike was killed):

```bash
spike/SpikeTool/bin/Debug/net10.0-windows/SpikeTool.exe taskbarcreated
```

If volume, network or power are still missing from the taskbar after that, restart Explorer.

TestTrayApp: `TestTrayApp.exe --name Alpha --uid 1 --v4 --log DIR`, then `SpikeTool post "TestTrayApp:Alpha" N` with N = 1 modify tooltip, 2 balloon, 3 delete, 4 add, 5 change icon, 6 exit.

## Logs

`phase0-logs/` holds the logs from the test runs referenced in FINDINGS.md. They contain local details (Wi-Fi network name, user paths, app list), so the folder is kept out of git (`.gitignore`) and exists only on the test machine.
