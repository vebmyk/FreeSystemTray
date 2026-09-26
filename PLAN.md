# Development Plan: Free-Standing Customizable System Tray for Windows

## Goal
A standalone Windows application that acts as the system tray (notification area). It is a free-floating, borderless window, not part of the taskbar, and can be placed anywhere on screen. It shows the tray icons of running applications and routes clicks, tooltips and context menus back to them.

Primary target: Windows 10. Secondary target: Windows 11 (see "Windows 11 notes").

## Requirements
1. **Free placement:** drag to any position on any monitor, with optional snapping. Position is remembered.
2. **Show/hide on demand:** global hotkey, optional screen-edge hotspot, click-away auto-hide, and an "always visible" mode.
3. **Arbitrary scaling:** overall size and icon size are each settable to any value the user wants, in pixels or as a percentage of a base size (for example 167%), independent of each other and DPI-correct. Icons may look imperfect at odd sizes; that is acceptable and must never be blocked.
4. **Appearance customization:** background, transparency/blur, corner radius, borders, icon spacing, layout direction (horizontal, vertical, grid), fonts and tooltip style, saved as themes.

## Non-goals (for now)
- Taskbar, Start menu or quick-launch replacement.
- Replicating the clock or Action Center.
- Hiding or changing Explorer's taskbar or its tray. Explorer's tray keeps working next to ours (see "Decisions").

## Key technical facts
Verified in Phase 0 and Phase 1 on Windows 10 22H2 unless marked otherwise. Details are in `FINDINGS.md`.
- Apps register tray icons by calling `Shell_NotifyIcon`, which sends `WM_COPYDATA` to the first window of class `Shell_TrayWnd` that `FindWindow` returns (the highest in Z-order). Whoever owns that window is the tray host. Apps also ask the host where their icon is (`Shell_NotifyIconGetRect`) and where the taskbar is (`SHAppBarMessage`), and place their menus and flyouts from the answers. They ask the host even when the click was in Explorer's own tray, and Explorer itself asks the host about its own taskbar and its hidden-icons chevron (Phase 1).
- Explorer owns it by default. To become the host while Explorer runs, this app keeps its own hidden `Shell_TrayWnd` above Explorer's, imports the icons Explorer already has, and broadcasts the `TaskbarCreated` registered message so running apps re-register. Explorer raises its taskbar again at times (when it restarts, or when its own flyouts open), so the host must keep checking and re-raise its window.
- Base library: **ManagedShell** (C#/.NET, **Apache-2.0**, the library behind RetroBar and Cairo Desktop), actively maintained as of September 2026. It implements the host role, the icon import, click and tooltip forwarding, and the answers to icon-position and taskbar-position queries. The UI is a WPF app on top of it.
- ManagedShell as shipped has gaps that matter for an app running next to Explorer. Each is handled in our `ITrayHost` implementation (see Architecture):
  - It does not forward the icon messages it handles to Explorer. Explorer's tray goes stale while we run (and is empty after an Explorer restart), balloon notifications are swallowed, and icons can be lost when we exit.
  - Taking the role pushes Explorer's taskbar down and removes its always-on-top flag, which stays off after we exit.
  - It answers auto-hide queries (`ABM_GETSTATE`, `ABM_GETAUTOHIDEBAR`) itself, always with "not auto-hide".
  - It answers every icon-position and taskbar-position query with its own window, including Explorer's own: Explorer's hidden-icons flyout then opens at the top-left corner of the screen, and apps' popups open next to our window even when their icon was clicked in Explorer's tray (Phase 1).
  - Tray messages already arrive while it starts, before its window can be subclassed (Phase 1).
  - Icon messages are sent messages, so they can run in the middle of any code on the tray thread that waits (locks, calls to other processes, COM). If that happens inside ManagedShell's collection events, it drops the message.
  - When "hide inactive icons" is on, its icon import reverses the order of Explorer's hidden icons each time we start.
- Built-in icons on Windows 10: volume, network, power, Windows Update, Windows Security and Bluetooth arrive as ordinary tray icons, and their flyouts open next to our window. The clock and Action Center are part of Explorer's taskbar, not tray icons; Action Center opens with `ms-actioncenter:`.
- `NIM_ADD` carries the calling app's executable path, which shell32 writes relative to a known folder (`{FOLDERID}\rest`) under Windows, System32 and Program Files. Explorer ties a GUID icon to that string, so an add we build for Explorer must carry it in the same form (Phase 1).
- Windows 10's own flyouts (network, volume, power) ignore the taskbar edge the host reports: they center on the tray or the icon and are then pushed inside the work area, so they overlap a free-floating window (Phase 1).
- WPF lets a window be layered (and so fade or be see-through) only if it is created with `AllowsTransparency`; it strips `WS_EX_LAYERED` from any other window. The tray window is created that way (Phase 2).
- Closing a Windows 10 flyout gives the foreground to Explorer's taskbar (Phase 2).
- Icon images: each app hands over one `HICON`, usually 16 px at 100% scaling (some apps send 32 or 48 px). There is no larger version to ask for.
- Reference implementations for the message formats: RetroBar, Cairo Desktop, ReactOS `explorer` (systray), Wine `explorer/systray.c`.

## Architecture
- **Language/UI:** C# / .NET, WPF (best per-icon scaling and templating control).
- **`ITrayHost` interface:** the only place that talks to the shell. Exposes an observable collection of icons (image, tooltip, state, owner) and methods for mouse input (enter, leave, move, button down and up). The UI also reports each icon's screen rectangle and the window's, which apps use to place menus and flyouts. Keeping it behind an interface lets the implementation be swapped or patched per Windows version.
- **`ITrayHost` implementation:** wraps ManagedShell, unmodified from NuGet and pinned to a tested version. On top of it (built in Phase 1, in `src/FreeSystemTray.TrayHost`):
  - **Mirroring:** after ManagedShell handles an icon message, send the same message on to Explorer's `Shell_TrayWnd`, by subclassing our own tray window. Explorer's tray stays current, Windows keeps showing balloon notifications as toasts, and nothing has to be handed back after an exit or crash. When Explorer rejects a mirrored add (it already has the icon), send it as a modify; when it rejects a modify (it doesn't know the icon), send a full add built from the icon's stored state, with the app's executable path in shell32's form.
  - **Explorer's taskbar:** give it back its always-on-top flag after taking the host role, and keep our hidden window above it. Only once: Explorer drops the flag itself while a full-screen app is in front.
  - **Auto-hide queries:** forward `ABM_GETSTATE` and `ABM_GETAUTOHIDEBAR(EX)` to Explorer, so apps see Explorer's real auto-hide setting.
  - **Position queries:** answer icon- and taskbar-position queries with our window only for interactions that start in it (the cursor is on it, or it was clicked within 5 s); otherwise Explorer answers. Explorer's questions about its own taskbar and its hidden-icons chevron always get Explorer's answer.
  - **Threading:** apply ManagedShell's changes to the UI with `Dispatcher.BeginInvoke`, and never wait on anything inside its event handlers. Compare icons by object reference, not with `NotifyIcon.Equals`. Subclass ManagedShell's window as it is created (a `WH_CBT` hook), because tray messages arrive during its startup.
- **Window layer:** one borderless, topmost, non-activating tool window (`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`), so it does not steal focus or appear in Alt+Tab. Phase 0 confirmed that apps' context menus and our tooltips work from such a window. Since Phase 2 it is per-pixel transparent (`AllowsTransparency`) and its content draws the background; show/hide animates the content.
- **Show/hide:** a controller on the UI thread (hotkey, hotspot, auto-hide, commands from `--toggle`, `--show`, `--hide`). The system-wide hooks it needs while an auto-hiding tray is shown (a low-level mouse hook, foreground and menu WinEvents) run on a thread of their own and only pass events on, so they never wait on the tray thread.
- **Settings layer:** JSON config plus theme files. Live reload (built for `settings.json` in Phase 2).
- **Shell integration:** global hotkey, single-instance mutex, autostart, Explorer-restart handling, and a small watchdog process that broadcasts `TaskbarCreated` if the app dies (the single-instance mutex and the watchdog were built in Phase 1, the hotkey in Phase 2).

## Phases

### Phase 0: Feasibility spike (done)
Done on 2026-09-25 on Windows 10 22H2 (build 19045.7725). Results and the list of apps tested are in `FINDINGS.md`. The spike, a test tray app and the test harness are in `spike/`.
- Works: taking the host role, every icon including volume, network and power, clicks, context menus, tooltips, system flyouts (they open next to our window), and Explorer restarts.
- ManagedShell as shipped fails the hand-back to Explorer (see "Key technical facts"). Mirroring fixed this in the spike.
- Decision gate: core behavior is reliable on Windows 10.
- Not tested: Windows 11, elevated tray apps, mixed DPI. These moved to later phases.

### Phase 1: Tray host, core window and placement (done)
Done on 2026-09-25 and 26 on Windows 10 22H2 (build 19045.7725). Results are in `FINDINGS.md` (Phase 1). The app is in `src/`; unit tests and the smoke test are in `tests/`. The smoke test uses the Phase 0 test app and harness; rerun it before every ManagedShell upgrade.
- Built: the production `ITrayHost` implementation with everything listed under Architecture, and a borderless, topmost tool window with dragging, a lock toggle, optional edge snapping, a saved position that keeps its anchor corner, multi-monitor placement rules, and the nearest screen edge reported to the host.
- Verified: clicking an icon in Explorer's own tray still opens its flyout or popup at the taskbar; this needed position-query routing and answers for Explorer's own queries. Apps see Explorer's auto-hide state, checked with a temporary auto-hide appbar; the end-to-end check with the taskbar set to auto-hide is left to the user (FINDINGS.md explains how).
- Not as planned: reporting the nearest edge does not make Windows 10's own flyouts open beside the tray; they overlap a free-floating window wherever it is. **Open decision:** accept this for the floating window and fix it with AppBar mode (docked, outside the work area), or try steering the flyouts by answering with shifted rectangles (fragile). See FINDINGS.md, Phase 1, finding 4.
- Not tested: multiple monitors and mixed DPI (one monitor; the placement rules are unit-tested with two).
- Free-standing floating window first. An AppBar mode that reserves screen space is an optional later addition, and the fix for the flyout overlap when the tray is docked.

### Phase 2: Show/hide (done)
Done on 2026-09-26 on Windows 10 22H2 (build 19045.7725). Results are in `FINDINGS.md` (Phase 2); usage and settings are in `README.md`.
- Built: a configurable global hotkey, a hotspot at a screen edge or corner of the tray's monitor, click-away (including switching to another window) and timed auto-hide, fade and slide animations (off when "Show animations in Windows" is off), and an always-visible mode (the default). A menu or flyout opened from one of the tray's icons keeps an auto-hiding tray shown. Also `--toggle`, `--show`, `--hide`, and live reload of `settings.json`.
- Hidden means only the window is hidden: icons keep registering (smoke-tested), and while hidden Explorer answers icon-position queries, so menus and flyouts open at the taskbar.
- Not as planned: fading needed the window to be per-pixel transparent (`AllowsTransparency`), which WPF allows only from creation. The window is now always that kind of window; see Phase 4.
- Follow-up (built): while a full-screen app is in front on the tray's monitor, the tray drops its always-on-top flag and stays behind it, as Explorer's taskbar does; the hotkey brings it up over it. Windows reports full-screen windows to shell hook windows (`FULLSCREENENTER`/`FULLSCREENEXIT`). See FINDINGS.md, "Phase 2 follow-up".
- Not tested: multiple monitors and mixed DPI, non-Win32 menus of real apps (Discord, Qt apps) with auto-hide, a long run in auto-hide mode.

### Phase 3: Scaling
- Two independent settings: icon size and window/padding scale. Each accepts any value, as pixels or as a percentage of a base size (for example 167%). No preset steps and no upper or lower clamp beyond sanity limits.
- Scale from the image the app hands over, at its native size (usually 16 px at 100% scaling; some apps send 32 or 48 px). There is no larger version to request, so large sizes will be upscaled and softer. Optional per-icon setting: use the app's .exe icon instead. It is sharper at large sizes, but it does not show status such as unread counts or connection state.
- Options for how odd sizes are rendered:
  - **Snap to whole pixels** toggle (`UseLayoutRounding`, `SnapsToDevicePixels`): sharper icons, slightly different size than requested.
  - **Exact size** (snapping off): exact requested size, possibly softer.
  - **Scaling mode:** high-quality smooth (bicubic) or nearest-neighbor. Nearest-neighbor suits integer multiples such as 200% and gives a pixel-art look.
- Per-Monitor V2 DPI awareness via manifest. Test mixed-DPI setups and moving the window between monitors.

### Phase 4: Appearance and themes
- Data-driven WPF templates: background color/opacity, blur/acrylic, corner radius, border, icon spacing, orientation (row, column, grid with N columns), hover/pressed effects, tooltip styling.
- Blur on Windows 10 uses the undocumented `SetWindowCompositionAttribute`, with a solid-color fallback. On Windows 11 prefer the official `DWMWA_SYSTEMBACKDROP_TYPE` (Mica/acrylic) and account for system-rounded corners.
- The window is already per-pixel transparent (Phase 2), so background opacity and corner radius only need the content to draw them. Test the blur on this kind of window (layered, drawn by WPF), and keep the show/hide animations working with it.
- Themes as JSON, plus optional XAML resource dictionaries for advanced users. Import/export and live preview.

### Phase 5: Management features
- Per-icon rules: hide, always show, reorder (drag), custom label or tooltip override.
- Also hide the icons Windows hides, such as Meet Now (Explorer registers it but hides it; the spike showed it).
- Overflow handling: a "hidden icons" flyout or a scrollable area.
- Settings window, tray-style menu for the app itself, autostart, single-instance.
- Balloon notifications already appear as Windows toasts through mirroring. Showing them in our own style is optional.

### Phase 6: Robustness and polish
- Explorer crash/restart recovery, graceful shutdown, logging, crash safety. Include the cases that broke the hand-back with ManagedShell as shipped: Explorer restarts while we host and then we exit, and the same followed by a crash.
- Take the host role back faster than ManagedShell's 100 ms check (react to Z-order changes), and pick up icon changes that went only to Explorer while it held the role.
- Stop the import from reversing the order of Explorer's hidden icons, for example by reading Explorer's hidden-icons toolbar directly. This is a candidate upstream fix.
- Match Explorer's tooltip behavior: for version 4 icons, show the standard tooltip only if the app asked for it, and send `NIN_POPUPOPEN` after the hover delay.
- Test with a variety of real tray apps (Discord, Steam, Dropbox, antivirus, VPN clients, OEM tools), including:
  - apps with balloon or toast-style notifications;
  - apps that run elevated (Task Manager is the easiest: it runs elevated and shows a tray icon);
  - apps with several icons from one .exe.
- Installer or portable build.

## Windows 11 notes
- Same code on both: window, placement, show/hide, hotkey, scaling, theming and settings are ordinary WPF.
- Different: Windows 11 rewrote the taskbar and its internals. Icons still arrive through `Shell_NotifyIcon`, but how well a third-party app can take over the tray host role must be tested, and it may change across Windows 11 updates. System icons (volume, network, clock) are even more tied to Explorer.
- From ManagedShell's source (not verified): on Windows 11, clicking the volume icon opens Quick Settings instead of a flyout. Before 22H2, network and power do the same, because sending them clicks crashed those builds.
- Backdrop and corner behavior differ (see Phase 4).
- Strategy: keep all shell-specific code behind `ITrayHost`. Phase 0 passed on Windows 10; Windows 11 is not tested yet. If it fails there, ship the Windows 10 version and treat 11 as a follow-up.

## Risks
1. **Built-in system icons:** work on Windows 10 (Phase 0), and clicked in Explorer's own tray their flyouts still open at the taskbar (Phase 1). Opened from our window, Windows 10's own flyouts overlap a floating tray (see Phase 1). Still open: Windows 11.
2. **Tray host handover:** confirmed with ManagedShell as shipped (stale or empty Explorer tray, stale tooltips, Explorer's own icons lost after an Explorer restart). Mitigated by mirroring, the taskbar fix and the watchdog. Remaining gap: while Explorer briefly holds the role (a few hundred milliseconds to about 2 s, for example while the Start menu or its flyouts are open), icon changes go only to Explorer.
3. **Elevated apps:** messages from admin-level apps to a non-elevated host can be blocked (UIPI). Not tested yet (Phase 6). Consider an option to run elevated.
4. **Windows updates** can change undocumented behavior we rely on: the format of tray messages in `WM_COPYDATA` (including shell32's executable-path field and its known-folder form), the `FindWindow` Z-order rule, the GUID Explorer uses for its hidden-icons chevron, the Explorer internals ManagedShell reads when importing icons, and the Windows 10 blur API.
5. **Balloon/toast notifications:** solved by mirroring (Windows shows them).
6. **Windows 11** tray-host behavior is uncertain and version-dependent.
7. **ManagedShell upgrades** can change the internals our workarounds rely on. Pin the version and rerun the smoke test before upgrading.
8. **Re-entrant icon messages:** code on the tray thread that waits can have icon messages run in the middle of it. Handled by the threading rule under Architecture.

## Decisions
- 2026-09-25: Keep Explorer's taskbar and tray as they are. Our tray is additive, and mirroring keeps Explorer's tray correct. Users who don't want every icon shown twice can use Windows' own settings: move icons to the hidden-icons area, or auto-hide the taskbar.
- 2026-09-25: Use ManagedShell unmodified from NuGet, with no fork. Workarounds stay small and inside the `ITrayHost` implementation. Offering them upstream (for example a "forward handled messages" option) is optional; if one is accepted, delete our workaround.
- 2026-09-26: While a full-screen app (video, game, slide show) is in front on the tray's monitor, the tray drops its always-on-top flag, as Explorer's taskbar does, and gets it back afterwards. Built the same day.

## Deliverables
Source repo with a README, build instructions, a sample theme set, `FINDINGS.md` from Phase 0 (done), the smoke test (`tests/smoke`, built on the Phase 0 test app and harness in `spike/`), and a short test checklist of the apps tested in Phase 0 (in `FINDINGS.md`) and Phase 6.

## Instructions to the implementer
- Phases 0, 1 and 2 are done (`FINDINGS.md`). Wait for approval before starting Phase 3.
- Have real tray apps running during testing, and state which Windows version and build you are testing on.
- Use ManagedShell for the tray host. Our own `WM_COPYDATA` handling is limited to the workarounds under Architecture. Run the smoke test (`tests/smoke`) after changes to the tray host or to show/hide, and before every ManagedShell upgrade.
- Keep tray host logic behind `ITrayHost` and separate from the UI.
- Explorer's tray must keep working while our app runs, after it exits, and after it crashes.
