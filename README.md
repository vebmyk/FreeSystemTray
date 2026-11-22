# FreeSystemTray

<p align="center">
  <a href="https://github.com/vebmyk/FreeSystemTray/releases/latest">
    <img src="https://img.shields.io/badge/Download-latest%20release-2ea44f?style=for-the-badge&logo=windows&logoColor=white" alt="Download latest release" height="60">
  </a>
</p>

A free-standing system tray (notification area) for Windows: a small borderless window you can put
anywhere, showing the tray icons of running apps. Clicks, menus, tooltips and flyouts work as they do
in Explorer's tray, and Explorer's own tray keeps working next to it: every icon stays there too,
and if FreeSystemTray exits or crashes, Explorer's tray carries on as before.

Made for and tested on Windows 10 22H2. Windows 11 has not been tested; there it could not tell which
icons Explorer shows and which it hides (see [Arranging icons](#arranging-icons)).

## Getting started

1. Unzip `FreeSystemTray-<version>-win-x64.zip` anywhere you can write to. Nothing needs to be
   installed: the .NET runtime is inside the exe.
2. Start `FreeSystemTray.exe`. The tray appears above the bottom-right corner of the primary screen.
3. To have it start when you sign in, turn on **Start FreeSystemTray when I sign in** in the
   settings (General). This adds it to your Run key, where Task Manager's Startup tab lists it.

To build it yourself, see [DEVELOPMENT.md](DEVELOPMENT.md).

## Using it

- **Move it** by dragging the grip on its left edge. It snaps to screen and work-area edges when
  an edge comes within 12 px (at 100% scaling). The position is saved as the corner nearest a screen
  corner, so a tray at the right edge grows to the left when icons are added.
- **Right-click the grip**, or FreeSystemTray's own icon, for its menu: the settings, a theme, lock
  the position, turn snapping or auto-hide on or off, hide (or show) the tray, or exit.
- **Settings:** the menu's **Settings…**, a left-click on FreeSystemTray's own icon, or starting
  the exe again. Every change applies and is saved at once.
- **Drag an icon** to move it, onto the chevron (**^**) to hide it in the hidden-icons flyout, or out
  of the flyout back into the tray. In a grid of a fixed size the chevron expands the tray instead
  (see [Layout](#layout-of-the-icons)). **Shift+right-click** an icon to choose where it goes.
- **Ctrl+Alt+Shift+T** hides and shows the tray (configurable, see
  [Showing and hiding](#showing-and-hiding)).
- Only one instance runs at a time. `FreeSystemTray.exe --exit` closes the running one.

FreeSystemTray's own icon also shows in Explorer's tray, so the menu and settings can be reached while
the tray is hidden. Turn it off in the settings (General) or with `"appIcon": false`.

Tooltips and popups behave as in Explorer's tray: apps whose icons ask for the standard tooltip get
it, after the same delay; the others can show a popup of their own. A rule's replacement tooltip
(see [Arranging icons](#arranging-icons)) is always shown.

### If it crashes

A second copy of the exe runs alongside as a watchdog. If the tray crashes or is killed, the
watchdog hands the icons back to Explorer. After a crash it also starts FreeSystemTray again, unless
it keeps crashing soon after starting (three times in a row); not when someone ended it on purpose
(Task Manager, `taskkill`), and not while Windows is signing out. **Start FreeSystemTray again if it
crashes** (settings, General) or `"restartAfterCrash": false` turns that off. If the tray stops
responding for about 25 s, the watchdog ends it and treats it as a crash.

## Settings and files

Everything in the settings window is also in `settings.json`, described in the sections below.

| | Normal copy | Portable copy (`portable.txt` next to the exe) |
|---|---|---|
| Settings | `%LOCALAPPDATA%\FreeSystemTray\settings.json` | `data\settings.json` next to the exe |
| Your themes | `%LOCALAPPDATA%\FreeSystemTray\themes` | `data\themes` |
| Logs | `%LOCALAPPDATA%\FreeSystemTray\logs` | `data\logs` |

The zip comes with `portable.txt`; delete it to keep everything in `%LOCALAPPDATA%\FreeSystemTray`
instead. Edits to the settings file apply while the app runs. It may have comments and trailing
commas, as theme files may; the app does not keep them when it saves. If something does not work
(a hotkey another program holds, a theme that does not load), the log says why.

## Arranging icons

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
| `maxTrayIcons` | At most this many icons in the tray; the others go to the flyout, those a rule puts in the tray last. `0`: no limit. A grid of a fixed size (below) limits it too. |
| `rules[].match` | Which icons: a GUID, an exe file name (`Discord.exe`, all its icons), a full path, or either with `:uID` for one of the exe's icons (`PowerToys.exe:1`). The settings window and dragging write the icon's own name: its GUID, or `exe:uID`. For each value, the rule that names the icon most exactly wins. |
| `rules[].placement` | `Tray`, `Overflow` or `Hidden`. Left out: the default decides. |
| `rules[].label` | The icon's name in the settings window and menus. |
| `rules[].tooltip` | Replaces the tooltip; `{tip}` stands for the app's own tooltip; `""` shows none. |
| `order` | Icon names in the order they are shown, also for apps that are not running. Icons not in it come after, in the order they arrived. Dragging writes it. |

An icon its app has hidden stays hidden whatever the rules say. Where Explorer shows each icon is
read from Explorer's own tray every few seconds (Windows 10; on a Windows without those toolbars,
such as Windows 11, it is unknown and the tray is used).

## Showing and hiding

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

## Layout of the icons

A row, a column or a grid, set apart from the theme: `columns` × `rows` in the settings window
(Appearance) or in `settings.json`. `0` means as many as needed.

```json
"layout": { "columns": 4, "rows": 2 }
```

| `columns` × `rows` | Layout |
|---|---|
| `0` × `1` | A row (the default). |
| `1` × `0` | A column; the grip goes on top. |
| `4` × `0` | Rows of four, as many rows as needed. |
| `0` × `2` | Two rows, filled top to bottom, growing to the right. |
| `4` × `2` | A fixed grid of 8 cells. |

A fixed grid never grows past its size. When more icons are to be shown than fit, or some are in
the hidden icons, its last cell is the chevron, and a click on it expands the tray in place: the
icons that did not fit and the hidden icons follow in more rows of the same width, away from the
tray's anchor corner (upwards for a tray at the bottom), so the grid and the chevron stay where
they are. Another click, or the tray hiding, collapses it. The hidden-icons flyout is not used.
Dragging an icon onto the chevron expands the tray too.

## Size

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

## Themes

Choose a theme from the menu's **Theme** submenu, the settings window (Appearance), or with
`"theme": "Glass"` in `settings.json`. Built in: Dark (the default), Light, Glass, Acrylic, Minimal
and Gradient (XAML). A theme is only the look: the layout is [its own setting](#layout-of-the-icons)
(the former Dark column and Glass grid themes are now Dark and Glass in a column and in rows of four,
and settings that named them are changed to that). Your own themes go in a `themes` folder next to
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
  "icons": { "spacing": 0, "hover": "#33FFFFFF", "pressed": "#55FFFFFF", "highlightCornerRadius": 4 },
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
| `grip.position` | `Auto` (left of a row or grid, above a column), `Left`, `Top`, `Right` or `Bottom`. |
| `icons.layout`, `icons.gridColumns` | No longer read: the layout is a setting. A theme that has them gets a note in the log. |
| `xaml` | A XAML resource dictionary, relative to the theme file, for what theme files cannot say, such as gradients. It can replace these resources: the brushes `Tray.Background`, `Tray.Foreground`, `Tray.BorderBrush`, `Tray.GripBrush`, `Icon.HoverBrush`, `Icon.PressedBrush`, `ToolTip.Background`, `ToolTip.Foreground`, `ToolTip.BorderBrush`; `ToolTip.BorderThickness` and `ToolTip.Padding` (Thickness), `ToolTip.CornerRadius` (CornerRadius), `ToolTip.FontFamily` and `ToolTip.FontSize`; and the tooltip style (an implicit `Style` for `ToolTip`). See `Themes\Gradient.xaml` next to the exe. **XAML can run code: only use XAML files you trust.** |

A background is never fully transparent: Windows lets clicks through fully transparent pixels. Some
icons are white (on this test machine, Windows' own volume, network and battery icons) and hard to
see on the Light theme.

`FreeSystemTray.exe --export-theme FILE` writes the current theme with every value spelled out (and
copies its XAML next to it), so it can be shared or used as a start. `--import-theme FILE` copies a
theme (and its XAML) into your themes folder and selects it; a running tray switches to it at once.
A theme whose XAML would land outside the themes folder (`"xaml": "..\\other.xaml"`) is not imported.
Both write what happened to the log and exit with 1 if they fail.

## Command line

| Option | Effect |
|---|---|
| `--exit` | Close the running instance and wait for it to exit. |
| `--toggle`, `--show`, `--hide` | Show or hide the running instance's tray, for example from a shortcut or a script. |
| `--open-settings` | Show the running instance's tray and its settings window. Starting the exe while it runs does the same. |
| `--import-theme FILE`, `--export-theme FILE` | Import a theme and switch to it, or write the current one to FILE (see [Themes](#themes)). |
| `--settings FILE` | Use another settings file. |
| `--log-dir DIR` | Write logs to DIR. |
| `--verbose` | Log much more, for tracking down a problem. |
| `--autostart` | Used by sign-in autostart: wait up to a minute for Explorer's taskbar, and exit quietly if an instance is running already. |

Options for testing and diagnosis are in [DEVELOPMENT.md](DEVELOPMENT.md#diagnostic-command-line-options).

## Removing it

Turn off **Start FreeSystemTray when I sign in** (settings, General), exit it from its menu, and
delete its folder. A normal copy also leaves `%LOCALAPPDATA%\FreeSystemTray` (settings, themes and
logs), which you can delete. Explorer's tray has kept every icon all along, so nothing needs to be
restored.

## License and third-party code

FreeSystemTray itself has no license file yet. It uses
[ManagedShell](https://github.com/cairoshell/ManagedShell), licensed under Apache-2.0 (it has no
NOTICE file); the .NET runtime and the packages ManagedShell uses are MIT. The portable build carries
`THIRD-PARTY-NOTICES.txt` and `ManagedShell-LICENSE.txt`.
