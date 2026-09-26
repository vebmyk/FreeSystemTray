# FreeSystemTray

A free-standing system tray (notification area) for Windows: a small borderless window you can put
anywhere, showing the tray icons of running apps. Clicks, menus, tooltips and flyouts work as they do
in Explorer's tray, and Explorer's own tray keeps working next to it.

Status: Phases 1 to 5 of [PLAN.md](PLAN.md) (tray host, window and placement; show/hide; scaling;
themes; management: icon rules, hidden icons, a settings window, autostart), tested on Windows 10
22H2. Robustness and packaging (Phase 6) come next. Findings from testing are in
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
- **Right-click the grip**, or FreeSystemTray's own icon, for its menu: the settings, a theme, lock
  the position, turn snapping or auto-hide on or off, hide (or show) the tray, or exit.
- **Settings:** the menu's **Settings…**, a left-click on FreeSystemTray's own icon, or starting
  the exe again. Every change applies and is saved at once.
- **Drag an icon** to move it, onto the chevron (**^**) to hide it in the hidden-icons flyout, or out
  of the flyout back into the tray. **Shift+right-click** an icon to choose where it goes.
- **Ctrl+Alt+Shift+T** hides and shows the tray (configurable, see below).
- Only one instance runs at a time. `FreeSystemTray.exe --exit` closes the running one.

FreeSystemTray's own icon also shows in Explorer's tray, so the menu and settings can be reached while
the tray is hidden. Turn it off in the settings (General) or with `"appIcon": false`. **Start
FreeSystemTray when I sign in** (settings, General) adds it to the user's Run key, where Task
Manager's Startup tab lists it.

Settings are in `%LOCALAPPDATA%\FreeSystemTray\settings.json` and logs in
`%LOCALAPPDATA%\FreeSystemTray\logs`. Edits to the settings file apply while the app runs. A second
copy of the exe runs as a watchdog: if the tray process crashes or is killed, the watchdog hands the
icons back to Explorer.

### Arranging icons

Each icon goes in the tray, in the hidden-icons flyout (opened with the chevron at the end of the
tray, as in Explorer), or nowhere. Rules decide; icons without one go where `default` says. Hiding
an icon only takes it out of this tray: the app keeps running, and Explorer's tray keeps showing it.
The settings window's Icons tab lists every icon with its rule and where it is now, and edits the
same settings. They are in `settings.json` too:

```json
"icons": {
  "default": "Tray",
  "hideWindowsHidden": true,
  "maxTrayIcons": 0,
  "rules": [
    { "match": "Discord.exe", "placement": "Overflow" },
    { "match": "7820ae83-23e3-4229-82c1-e41cb67d5b9c", "placement": "Tray" },
    { "match": "PowerToys.exe:1", "label": "PowerToys", "tooltip": "PowerToys: {tip}" }
  ],
  "order": [ "PowerToys.exe:1", "Discord.exe:1" ]
}
```

| Setting | Values |
|---|---|
| `default` | Where icons without a rule go: `Tray`, `Overflow` (the hidden-icons flyout), or `Windows`: where Explorer shows them (its taskbar icons in the tray, its hidden icons in the flyout). |
| `hideWindowsHidden` | Hide icons that Explorer registers but does not show, such as Meet Now while it is turned off in "Turn system icons on or off". A rule that places the icon wins. |
| `maxTrayIcons` | At most this many icons in the tray; the others go to the flyout, those a rule puts in the tray last. `0`: no limit. |
| `rules[].match` | Which icons: a GUID, an exe file name (`Discord.exe`, all its icons), a full path, or either with `:uID` for one of the exe's icons (`PowerToys.exe:1`). The settings window and dragging write the icon's own name: its GUID, or `exe:uID`. For each value, the rule that names the icon most exactly wins. |
| `rules[].placement` | `Tray`, `Overflow` or `Hidden`. Left out: the default decides. |
| `rules[].label` | The icon's name in the settings window and menus. |
| `rules[].tooltip` | Replaces the tooltip; `{tip}` stands for the app's own tooltip; `""` shows none. |
| `order` | Icon names in the order they are shown, also for apps that are not running. Icons not in it come after, in the order they arrived. Dragging writes it. |

An icon its app has hidden stays hidden whatever the rules say. Where Explorer shows each icon is
read from Explorer's own tray every few seconds (Windows 10; on a Windows without those toolbars,
such as Windows 11, it is unknown and the tray is used).

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
- **Full-screen apps** (videos, games, slide shows) cover the tray, as they cover Explorer's taskbar:
  while one is the active window on the tray's monitor, the tray is not always-on-top. The hotkey
  brings it up over the app; the hotkey again or a click on the app puts it back.
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

### Size

Two independent sizes: the icons, and everything around them (the padding in each icon's cell, the
grip, the border and the margins). Each takes any value, as a percentage or in pixels; pixels are
device-independent, so the tray has the same size on a 150% monitor as on a 100% one. The window
grows or shrinks around its anchor corner.

```json
"scaling": {
  "iconSize": "100%",
  "traySize": "100%",
  "snapToPixels": true,
  "iconScaling": "Smooth",
  "exeIcons": []
}
```

| Setting | Values |
|---|---|
| `iconSize` | A percentage of 16 px (`"167%"`) or pixels (`"24px"`, or `24`). From 4 to 512 px. |
| `traySize` | A percentage scales the padding, grip, border and margins (`"50%"` is compact). Pixels set each icon's cell (`"40px"`: 40×40 cells, the icon centred). Changing the icon size never changes it. |
| `snapToPixels` | `true`: sizes are rounded to whole screen pixels, which is sharper but a little off (167% gives 27 px icons). `false`: exact sizes, somewhat softer. |
| `iconScaling` | `Smooth` (bicubic) or `NearestNeighbor` (blocky, crisp at 200%, 300%, ...). |
| `exeIcons` | Apps whose icons are drawn from their .exe's icon instead of the image they hand over: sharper at large sizes, but without status such as unread counts. Exe names (`"Discord.exe"`), full paths or icon ids. An exe without an icon keeps its tray image. |

Apps hand over one small image per icon (16 to 48 px were seen); there is no larger one to ask for,
so large sizes are upscaled from it.

### Themes

Choose a theme from the menu's **Theme** submenu, the settings window (Appearance), or with
`"theme": "Glass"` in `settings.json`. Built in: Dark (the default), Light, Glass, Acrylic, Minimal,
Dark column, Glass grid and Gradient (XAML). Your own themes go in a `themes` folder next to
`settings.json` (`%LOCALAPPDATA%\FreeSystemTray\themes`); a theme's id is its file name without
`.json`, and one with the name of a built-in theme replaces it. Edits to a theme file apply while
the app runs.

A theme file lists only what it changes; everything else comes from the theme named in `basedOn`,
and in the end from Dark. Sizes are pixels at a tray size of 100% and grow with the tray size.

```json
{
  "name": "My theme",
  "basedOn": "Glass",
  "foreground": "#E0E0E0",
  "background": { "color": "#1C1C1C", "opacity": 0.55, "blur": "Blur" },
  "border": { "color": "#40FFFFFF", "thickness": 1, "cornerRadius": 8 },
  "grip": { "color": "#28FFFFFF", "size": 10, "position": "Auto" },
  "icons": { "layout": "Row", "gridColumns": 4, "spacing": 0, "hover": "#33FFFFFF", "pressed": "#55FFFFFF", "highlightCornerRadius": 4 },
  "tooltip": { "background": "#202020", "foreground": "White", "border": "#50FFFFFF", "borderThickness": 1,
               "cornerRadius": 4, "fontFamily": "Segoe UI", "fontSize": 12, "padding": 6 },
  "xaml": "MyTheme.xaml"
}
```

| Value | Meaning |
|---|---|
| colours | `#RRGGBB`, `#AARRGGBB`, `#RGB`, or a name such as `White`. |
| `foreground` | What the tray draws itself: the hidden-icons chevron. |
| `background.blur` | `None`, `Blur` (what is behind the tray, blurred, seen through the background colour) or `Acrylic` (Windows 10's acrylic, tinted with the background colour). While Windows' "Transparency effects" is off, Windows cannot blur, and the tray uses its background colour, opaque. |
| `icons.layout` | `Row`, `Column`, or `Grid` (rows of `gridColumns` icons). |
| `grip.position` | `Auto` (left of a row or grid, above a column), `Left`, `Top`, `Right` or `Bottom`. |
| `xaml` | A XAML resource dictionary, relative to the theme file, for what theme files cannot say, such as gradients. It can replace these resources: the brushes `Tray.Background`, `Tray.Foreground`, `Tray.BorderBrush`, `Tray.GripBrush`, `Icon.HoverBrush`, `Icon.PressedBrush`, `ToolTip.Background`, `ToolTip.Foreground`, `ToolTip.BorderBrush`; `ToolTip.BorderThickness` and `ToolTip.Padding` (Thickness), `ToolTip.CornerRadius` (CornerRadius), `ToolTip.FontFamily` and `ToolTip.FontSize`; and the tooltip style (an implicit `Style` for `ToolTip`). See `Themes/Gradient.xaml`. **XAML can run code: only use XAML files you trust.** |

A background is never fully transparent: Windows lets clicks through fully transparent pixels. Some
icons are white (on this test machine, Windows' own volume, network and battery icons) and hard to
see on the Light theme.

`FreeSystemTray.exe --export-theme FILE` writes the current theme with every value spelled out (and
copies its XAML next to it), so it can be shared or used as a start. `--import-theme FILE` copies a
theme (and its XAML) into your themes folder and selects it; a running tray switches to it at once.
Both write what happened to the log and exit with 1 if they fail.

### Command line

| Option | Effect |
|---|---|
| `--settings FILE` | Use another settings file. |
| `--log-dir DIR` | Write logs to DIR. |
| `--verbose` | Log every tray message and position query, every click and window switch auto-hide sees, and ManagedShell's debug output. |
| `--diag DIR` | Write `state.json` (host status, window visibility, window, icon and hotspot rectangles) to DIR every 250 ms for the smoke test. Implies `--log-dir DIR --verbose`. |
| `--exit` | Close the running instance and wait for it to exit. |
| `--toggle`, `--show`, `--hide` | Show or hide the running instance's tray, for example from a shortcut or a script. |
| `--open-settings` | Show the running instance's tray and its settings window. Starting the exe while it runs does the same. |
| `--autostart` | Started at sign-in (the Run key's command has it): wait up to a minute for Explorer's taskbar, and exit quietly if an instance is running already. |
| `--import-theme FILE`, `--export-theme FILE` | Import a theme and switch to it, or write the current one to FILE (see "Themes"). |
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
- Explorer's taskbar gets its always-on-top flag back after ManagedShell takes it away;
- Explorer's own tray is read now and then (on a background thread) to learn where Explorer shows
  each icon, and which ones it hides.

All shell code is behind `ITrayHost` in `src/FreeSystemTray.TrayHost`. FINDINGS.md explains why
each workaround is needed.

## Smoke test

Run it before every ManagedShell upgrade and after changes to the tray host, show/hide, scaling,
themes or icon management. It builds the solution, starts four test tray icons and the app, and
checks mirroring, the fix-ups, the query answers, clicks, showing and hiding (commands, the hidden
state, a settings edit, hotspot, hotkey, click-away, the timer, a menu keeping the tray shown),
stepping behind full-screen windows, icon and tray sizes, themes, icon rules and the hidden-icons
flyout (rules, an icon Windows hides, order, dragging, the icon menu), the tray's own menus staying
open, the app's own icon, the settings window, and the hand-back to Explorer after an exit and after
a kill (89 checks).

```bash
powershell -ExecutionPolicy Bypass -File tests/smoke/Invoke-SmokeTest.ps1
```

For about a minute and a half it moves the mouse, clicks and drags the test icons, clicks windows
of its own, presses the hotkey, Shift and Esc, and twice covers the primary screen with a full-screen
window of its own for a few seconds; before each step it waits until mouse and keyboard have been
left alone for 1.5 s. `-SkipMouse` leaves those checks out. It also shows one Windows notification
from the balloon check, tells Explorer to hide one test icon for a few seconds, and near the end
opens the settings window (which takes the keyboard focus) until it closes the app. FreeSystemTray
must not be running. The exit code is the number of failed checks.

## Layout

| Path | What |
|---|---|
| `src/FreeSystemTray.TrayHost` | `ITrayHost` and its ManagedShell implementation: the only code that talks to the shell. |
| `src/FreeSystemTray` | The WPF app: tray window, placement, show/hide (`ShowHide/`), scaling (`Scaling/`), themes (`Theming/`, and the built-in ones in `Themes/`), icon rules and arrangement (`Icons/`), the hidden-icons flyout, dragging icons, the settings window, the app's own icon, autostart, settings, logging, watchdog. |
| `tests/FreeSystemTray.Tests` | Unit tests: placement, message formats, routing and fix-up rules, settings, hotkeys, hotspots, auto-hide rules, full-screen tracking, sizes and icon resampling, themes, the icon panel, icon rules, arrangement and order, where Explorer shows an icon, flyout placement, autostart. |
| `tests/smoke` | The end-to-end smoke test. |
| `tools/Make-AppIcon.ps1` | Draws `src/FreeSystemTray/FreeSystemTray.ico`. |
| `spike` | Phase 0 feasibility spike, the test tray app and the SpikeTool harness (see [spike/README.md](spike/README.md)). |

## Third-party code

ManagedShell is licensed under Apache-2.0. A redistributed build must include its LICENSE and keep
its NOTICE and attributions (packaging is Phase 6).
