<#
.SYNOPSIS
  End-to-end smoke test of FreeSystemTray against the real shell. Run it before every ManagedShell
  upgrade and after changes to the tray host, show/hide, scaling or themes.

.DESCRIPTION
  Uses the Phase 0 test app (spike/TestTrayApp) and harness (spike/SpikeTool). It starts four test
  tray icons and FreeSystemTray in diagnostic mode, then checks:
    - taking the tray host role and keeping Explorer's taskbar always-on-top;
    - mirroring: tooltip changes, new icons and balloons reach Explorer's tray too;
    - the full-add fix-up after Explorer has forgotten an icon;
    - auto-hide queries answered by Explorer, and the hidden-icons chevron position;
    - with -SkipMouse left out: clicks and menus from our window, and icon-position answers that
      depend on where the cursor is;
    - show/hide: --hide, --show and --toggle; icons keep registering while the window is hidden; a
      settings file edit applies while running; with -SkipMouse left out also the hotspot, the
      hotkey, click-away, the auto-hide timer, and a menu opened from the tray keeping it shown;
    - scaling: icon and tray sizes set in the settings file, snapping, .exe icons (a click on a
      scaled icon with -SkipMouse left out);
    - themes: layouts, blur against Windows' transparency setting, a theme of one's own with live
      edits, --export-theme and --import-theme;
    - with -SkipMouse left out: stepping behind a full-screen window (a new one, and one turning
      full screen like F11) and the hotkey bringing the tray up over it;
    - management: FreeSystemTray's own icon in both trays; icon rules (hidden icons, hidden, a
      tooltip), an icon Windows hides, the default placement, the saved order and a tray size limit;
      with -SkipMouse left out also the hidden-icons flyout (open, click an icon in it, close),
      dragging icons (to a new place, onto the chevron), and the tray's own menus staying open and
      taking Esc (the grip's, FreeSystemTray's icon's, Shift+right-click's in the tray and the
      flyout); the settings
      window (--open-settings, and starting the exe a second time);
    - robustness (Phase 6): Explorer's icons imported without changing the order of its hidden icons,
      also an app that ignores TaskbarCreated and has two icons; the tray host role taken back at once
      when Explorer's taskbar goes above ours, and icon changes that reached only Explorer meanwhile
      (a tooltip, an add, a delete) caught up on; icons of an app that died removed from both trays;
      with -SkipMouse left out, tooltips and popups as in Explorer: a version 4 icon without
      NIF_SHOWTIP gets NIN_POPUPOPEN after the hover delay and no tooltip, NIN_POPUPCLOSE when the
      pointer leaves, a button goes down or the tray hides; no WM_MOUSEHOVER or WM_MOUSELEAVE;
    - hand-back on a graceful exit and after the process is killed (watchdog), FreeSystemTray's own
      icon leaving Explorer's tray both times; not started again after being killed on purpose;
    - crash safety (Phase 6): after a real crash (an unhandled exception, --debug-crash) the watchdog
      hands the tray back and starts FreeSystemTray again; a tray that stops answering (--debug-hang)
      is ended by the watchdog after about 25 s and started again. While it hangs, apps' icon
      messages to it fail, and the tray window does not redraw.
  The balloon check shows a real Windows notification ("Balloon from SmokeA").
  The mouse and keyboard checks move the real mouse and press the tray's hotkey (Ctrl+Alt+Shift+T)
  and Esc, and the full-screen checks cover the primary screen with a black window for up to 8 s.
  Before each one the test waits until nobody has touched mouse or keyboard for 1.5 s. Near the end
  the settings window opens (and takes the keyboard focus) until the test closes the app.
  Explorer is told to hide one test icon for a moment (as Windows hides Meet Now).

  Tested on Windows 10 22H2 with "Always show all icons" off. FreeSystemTray must not be running.
  Exit code: the number of failed checks.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-SmokeTest.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-SmokeTest.ps1 -AppPath dist\FreeSystemTray-0.1.0-win-x64\FreeSystemTray.exe
  Tests a published build (tools\Publish.ps1); the test apps come from the -Configuration build.
#>
param(
    [string]$OutDir = (Join-Path $env:TEMP ("FreeSystemTray-smoke-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
    [string]$Configuration = "Debug",
    [string]$AppPath,
    [switch]$SkipBuild,
    [switch]$SkipMouse
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$tfm = "net10.0-windows"
$app = if ($AppPath) { (Resolve-Path $AppPath).Path } else { Join-Path $root "src\FreeSystemTray\bin\$Configuration\$tfm\FreeSystemTray.exe" }
$testApp = Join-Path $root "spike\TestTrayApp\bin\$Configuration\$tfm\TestTrayApp.exe"
$tool = Join-Path $root "spike\SpikeTool\bin\$Configuration\$tfm\SpikeTool.exe"
$chevronGuid = "964b6542-bbad-44ee-848a-3a95d85951ea"
$script:failures = 0
$script:started = @()

function Check([string]$name, [bool]$ok, [string]$detail = "") {
    if ($ok) { Write-Host ("PASS  " + $name) -ForegroundColor Green }
    else { Write-Host ("FAIL  " + $name + $(if ($detail) { "  ($detail)" })) -ForegroundColor Red; $script:failures++ }
}

function Wait-Until([scriptblock]$condition, [int]$timeoutMs = 10000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    do {
        try { if (& $condition) { return $true } } catch { }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    return $false
}

function Tool { & $tool @args }

function Read-State([string]$dir) {
    $path = Join-Path $dir "state.json"
    if (-not (Test-Path $path)) { return $null }
    return Get-Content $path -Raw | ConvertFrom-Json
}

function Our-Icon([string]$dir, [string]$name) {
    $s = Read-State $dir
    if ($null -eq $s) { return $null }
    return $s.icons | Where-Object { $_.tip -like "$name tip*" } | Select-Object -First 1
}

# Tooltip of the test icon in Explorer's tray (any of its three toolbars), or $null.
function Explorer-Tip([string]$name) {
    $line = Tool explorer-icons | Select-String "'$name tip #\d+'" | Select-Object -First 1
    if ($line) { return ($line.Line -split "'")[1] }
    return $null
}

function Post([string]$name, [int]$n) { Tool post "TestTrayApp:$name" $n | Out-Null }

function Host-Is([string]$process) { return [bool](Tool traywnds | Select-String "FindWindow\(Shell_TrayWnd\) -> \S+ $process\(") }

function Explorer-Topmost { return [bool](Tool traywnds | Select-String "explorer\(\d+\) topmost=True") }

function Start-App([string]$dir) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $p = Start-Process -FilePath $app -ArgumentList "--diag `"$dir`" --settings `"$(Join-Path $OutDir 'settings.json')`"" -PassThru
    [void](Wait-Until { $s = Read-State $dir; $s -and $s.shell.weAreHost -and $s.iconCount -gt 0 } 15000)
    Start-Sleep -Milliseconds 800
    return $p
}

function Stop-App([string]$dir) {
    $p = Start-Process -FilePath $app -ArgumentList "--exit --log-dir `"$dir\exit`"" -PassThru -Wait
    return $p.ExitCode
}

function Click-Icon([string]$dir, [string]$name, [string]$kind) {
    $icon = Our-Icon $dir $name
    if (-not $icon -or -not $icon.rect) { return $false }
    $x = [int]$icon.rect[0] + [int]($icon.rect[2] / 2); $y = [int]$icon.rect[1] + [int]($icon.rect[3] / 2)
    Tool click $x $y $kind | Out-Null
    return $true
}

function Log-Has([string]$name, [string]$pattern) {
    return [bool](Select-String -Path (Join-Path $OutDir "testtray-$name.log") -Pattern $pattern -Quiet)
}

# Simulated input only once the user has left mouse and keyboard alone for a moment.
function Idle { Tool wait-idle 1500 120000 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "Mouse or keyboard kept being used; stopped before simulating input." } }

function Send-Command([string]$dir, [string]$command) {
    Start-Process -FilePath $app -ArgumentList "$command --log-dir `"$dir\cmd`"" -Wait | Out-Null
}

function Is-Shown([string]$dir) { $s = Read-State $dir; return [bool]($s -and $s.shown -and $s.visible) }
function Is-Hidden([string]$dir) { $s = Read-State $dir; return [bool]($s -and -not $s.shown -and -not $s.visible -and -not $s.trayWindowVisible) }

# The last "Shown (...)" or "Hidden (...)" line of the app's log.
function Last-ShowHide([string]$dir) {
    $line = Select-String -Path (Join-Path $dir "FreeSystemTray.log") -Pattern "^\S+ \S+ (Shown|Hidden) \(" | Select-Object -Last 1
    if ($line) { return $line.Line.Substring(24) }
    return ""
}

function Write-Settings([string]$json) { Set-Content -Path (Join-Path $OutDir "settings.json") -Value $json -Encoding UTF8 }

# Explorer's hidden-icons flyout, in order: "owner tooltip" per icon.
function Explorer-Flyout {
    $in = $false
    foreach ($l in (Tool explorer-icons)) {
        if ($l -match "^\[(\S+)\]") { $in = $Matches[1] -eq "overflow"; continue }
        if ($in -and $l -match "'([^']*)' owner=(\S+)") { "$($Matches[2]) $($Matches[1])" }
    }
}

# The test app's log lines after the first $skip.
function Log-After([string]$name, [int]$skip) { @(Get-Content (Join-Path $OutDir "testtray-$name.log") | Select-Object -Skip $skip) }
function Log-Count([string]$name) { @(Get-Content (Join-Path $OutDir "testtray-$name.log")).Count }

# --- Preconditions -------------------------------------------------------------------------
if (Get-Process FreeSystemTray -ErrorAction SilentlyContinue) { throw "FreeSystemTray is running; exit it first (FreeSystemTray.exe --exit)." }
if (-not $SkipBuild) {
    & dotnet build (Join-Path $root "FreeSystemTray.sln") -c $Configuration -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Host "Smoke test output: $OutDir"
if (-not (Host-Is "explorer")) { throw "Explorer is not the tray host; is Explorer running?" }

try {
    # --- Start: three test icons, then FreeSystemTray -------------------------------------
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name SmokeA --uid 101 --log `"$OutDir`" --color #C0392B" -PassThru
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name SmokeB --uid 102 --v4 --log `"$OutDir`" --color #2E86C1" -PassThru
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name SmokeE --uid 105 --icons 2 --ignore-taskbarcreated --log `"$OutDir`" --color #7F8C8D" -PassThru
    [void](Wait-Until { (Explorer-Tip "SmokeA") -and (Explorer-Tip "SmokeB") -and (Explorer-Tip "SmokeE") })
    $flyoutBefore = @(Explorer-Flyout)
    $run = Join-Path $OutDir "run"
    $proc = Start-App $run

    Check "takes the tray host role" (Host-Is "FreeSystemTray")
    Check "Explorer's taskbar stays always-on-top" (Explorer-Topmost)
    Check "test icons appear in our window" ((Our-Icon $run "SmokeA") -and (Our-Icon $run "SmokeB"))
    $flyoutAfter = @(Explorer-Flyout)
    $kept = (@($flyoutBefore | Where-Object { $flyoutAfter -contains $_ }) -join "|") -eq (@($flyoutAfter | Where-Object { $flyoutBefore -contains $_ }) -join "|")
    Check "Explorer's icons are imported without changing the order of its hidden icons" ($kept -and $flyoutBefore.Count -gt 0 -and
        (Select-String -Path (Join-Path $run "FreeSystemTray.log") -Pattern "Imported \d+ icons from Explorer's tray" -Quiet)) "before: $($flyoutBefore -join ' | '); after: $($flyoutAfter -join ' | ')"
    Check "an app that ignores TaskbarCreated is imported too, both of its icons" (Wait-Until {
        @((Read-State $run).icons | Where-Object { $_.tip -like "SmokeE tip*" }).Count -eq 2 } 3000)

    # --- Mirroring ------------------------------------------------------------------------
    Post "SmokeB" 1
    Check "tooltip change reaches Explorer's tray" (Wait-Until { (Explorer-Tip "SmokeB") -eq "SmokeB tip #1" } 5000) "Explorer: $(Explorer-Tip 'SmokeB')"
    Check "tooltip change reaches our window" (Wait-Until { (Our-Icon $run "SmokeB").tip -eq "SmokeB tip #1" } 5000)

    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name SmokeC --uid 103 --v4 --showtip --log `"$OutDir`" --color #27AE60" -PassThru
    Check "icon added while we host appears in our window" (Wait-Until { Our-Icon $run "SmokeC" } 5000)
    Check "icon added while we host appears in Explorer's tray" (Wait-Until { Explorer-Tip "SmokeC" } 5000)

    $a = Our-Icon $run "SmokeA"
    Post "SmokeA" 2
    Check "balloon reaches us" (Wait-Until { (Read-State $run).balloons -match "Balloon from SmokeA" } 5000)
    Check "balloon is mirrored to Explorer (Windows shows the toast)" (Wait-Until {
        Select-String -Path (Join-Path $run "FreeSystemTray.log") -Pattern "MIRROR MODIFY hwnd=$($a.hwnd) uid=101 nif=0x10 .*explorer=1" -Quiet } 5000)

    # Explorer forgets SmokeA; SmokeA's next modify must come back as a full add.
    Tool explorer-delete $a.hwnd $a.uid | Out-Null
    [void](Wait-Until { -not (Explorer-Tip "SmokeA") } 3000)
    Post "SmokeA" 1
    Check "full add restores an icon Explorer had forgotten" (Wait-Until { (Explorer-Tip "SmokeA") -eq "SmokeA tip #1" } 5000) "Explorer: $(Explorer-Tip 'SmokeA')"

    # --- Queries that need no mouse -------------------------------------------------------
    $appbar = Tool appbar
    $viaHost = ($appbar | Select-String "^ABM_GETSTATE -> (\d+)").Matches.Groups[1].Value
    $direct = ($appbar | Select-String "sent to Explorer's taskbar directly -> (\d+)").Matches.Groups[1].Value
    Check "ABM_GETSTATE is Explorer's answer" ($viaHost -ne "" -and $viaHost -eq $direct) "host $viaHost, Explorer $direct"
    $probe = Tool autohide-probe top
    Check "ABM_GETAUTOHIDEBAR(EX) sees Explorer's auto-hide bars" ((@($probe | Select-String "PASS")).Count -eq 2) (($probe | Select-String "GETAUTOHIDEBAR") -join "; ")
    $chevron = Tool explorer-tray | Select-String "\[Button\] '' rect=\[(\d+),(\d+),(\d+),(\d+)\]$" | Select-Object -First 1
    if ($chevron) {
        $g = $chevron.Matches.Groups
        $expected = "icon rect [$($g[1].Value),$($g[2].Value),$($g[3].Value),$($g[4].Value)]"
        $answer = Tool iconrect 0 4294967295 $chevronGuid
        Check "Explorer's chevron position is answered (hidden-icons flyout opens at the taskbar)" ($answer -eq $expected) "$answer, expected $expected"
    }
    else { Write-Host "SKIP  chevron check: no visible chevron (all icons shown?)" }

    # --- The tray host role and Explorer's tray kept in step (Phase 6) ---------------------------
    Tool raise-explorer | Out-Null
    Check "Explorer's taskbar put above ours: the tray takes the role back within 50 ms" (Wait-Until {
        $line = Select-String -Path (Join-Path $run "FreeSystemTray.log") -Pattern "held the tray host role for (\d+) ms" | Select-Object -Last 1
        $line -and [int]$line.Matches[0].Groups[1].Value -le 50 -and (Host-Is "FreeSystemTray") } 3000)
    $e = Our-Icon $run "SmokeE"
    Start-Sleep -Milliseconds 3500   # a read of Explorer's tray from before the next gap
    Tool explorer-tip $e.hwnd 105 "SmokeE tip, changed in Explorer only" raise | Out-Null
    # SmokeE has two icons (uIDs 105 and 106), whose tooltips both start with "SmokeE tip".
    Check "a tooltip change that reached only Explorer while it held the role is caught up on" (Wait-Until {
        ((Read-State $run).icons | Where-Object { $_.tip -like "SmokeE*" -and $_.uid -eq 105 }).tip -eq "SmokeE tip, changed in Explorer only" } 3000)
    Start-Sleep -Milliseconds 3500
    Tool explorer-add $e.hwnd 107 - 0x8064 "C:\SmokeE.exe" "SmokeE extra, in Explorer only" raise | Out-Null
    function Extra { (Read-State $run).icons | Where-Object { $_.tip -like "SmokeE extra*" } }
    $added = Wait-Until { Extra } 3000
    Start-Sleep -Milliseconds 3500
    Tool explorer-delete $e.hwnd 107 raise | Out-Null
    Check "an icon added, and later deleted, while Explorer held the role is added to ours, and deleted" ($added -and
        (Wait-Until { -not (Extra) } 3000))
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name SmokeF --uid 108 --log `"$OutDir`" --color #34495E" -PassThru
    [void](Wait-Until { (Our-Icon $run "SmokeF") -and (Explorer-Tip "SmokeF") } 5000)
    Stop-Process -Id $script:started[-1].Id -Force
    Check "the icon of an app that died is removed from our tray and from Explorer's" (Wait-Until { -not (Our-Icon $run "SmokeF") -and -not (Explorer-Tip "SmokeF") } 6000)

    # --- Mouse: clicks from our window, position answers by cursor --------------------------
    if (-not $SkipMouse) {
        Idle
        Check "right-click on a v0 icon: WM_RBUTTONUP and its menu opens" ((Click-Icon $run "SmokeA" "right") -and
            (Wait-Until { Log-Has "SmokeA" "MENU OPEN" } 3000) -and (Log-Has "SmokeA" "EVENT WM_RBUTTONUP"))
        Tool key esc | Out-Null
        Start-Sleep -Milliseconds 500
        $b = Our-Icon $run "SmokeB"
        $ourRect = "[$($b.rect[0]),$($b.rect[1]),$([int]$b.rect[0] + [int]$b.rect[2]),$([int]$b.rect[1] + [int]$b.rect[3])]"
        Idle
        Check "left-click on a v4 icon: NIN_SELECT, and the app is told our icon's rectangle" ((Click-Icon $run "SmokeB" "left") -and
            (Wait-Until { Log-Has "SmokeB" ("NIN_SELECT .*iconRect=" + [regex]::Escape($ourRect)) } 3000)) "expected iconRect=$ourRect"

        Idle
        $w = (Read-State $run).window
        Tool move ([int]$w[0] + 5) ([int]$w[1] + [int]($w[3] / 2)) | Out-Null
        Start-Sleep -Milliseconds 200
        $ours = Tool iconrect $b.hwnd $b.uid
        Check "cursor on our window: apps get our icon's position" ($ours -eq "icon rect [$($b.rect -join ',')]") $ours
        $clock = Tool explorer-tray | Select-String "\[TrayClockWClass\].* rect=\[(\d+),(\d+),(\d+),(\d+)\]" | Select-Object -First 1
        if ($clock) {
            $g = $clock.Matches.Groups
            Tool move ([int]$g[1].Value + [int]($g[3].Value / 2)) ([int]$g[2].Value + [int]($g[4].Value / 2)) | Out-Null
            Start-Sleep -Milliseconds 200
            # Explorer's answer: the icon's rectangle in its tray, or S_FALSE (0x00000001) while the
            # icon sits in its hidden-icons flyout and that flyout is closed. Either way, not ours.
            $theirs = Tool iconrect $b.hwnd $b.uid
            Check "cursor on Explorer's taskbar: apps get Explorer's answer for the icon, not ours" ($theirs -ne $ours -and
                ($theirs -like "icon rect*" -or $theirs -like "*0x00000001")) "$theirs vs ours $ours"
        }
    }

    # --- Tooltips and popups, as in Explorer (Phase 6) ---------------------------------------------
    if (-not $SkipMouse) {
        function Hover([string]$name) { $i = Our-Icon $run $name; Tool move ([int]$i.rect[0] + [int]([int]$i.rect[2] / 2)) ([int]$i.rect[1] + [int]([int]$i.rect[3] / 2)) | Out-Null }
        function Away { $w = (Read-State $run).window; Tool move ([int]$w[0] - 150) ([int]$w[1] - 150) | Out-Null }
        Idle
        Away
        Start-Sleep -Milliseconds 300
        $n = Log-Count "SmokeB"
        Hover "SmokeB"
        Start-Sleep -Milliseconds 250
        $early = @(Log-After "SmokeB" $n | Where-Object { $_ -match "NIN_POPUPOPEN" }).Count
        Start-Sleep -Milliseconds 750
        $open = @(Log-After "SmokeB" $n | Where-Object { $_ -match "NIN_POPUPOPEN" }).Count
        $b = Our-Icon $run "SmokeB"
        Check "version 4 without NIF_SHOWTIP: NIN_POPUPOPEN after the hover delay, and no tooltip of ours" ($early -eq 0 -and $open -eq 1 -and $b.popupOpen -and
            -not $b.showsToolTip -and $null -eq $b.shownTip) "after 250 ms: $early, after 1 s: $open; popupOpen $($b.popupOpen), shownTip '$($b.shownTip)'"
        Away
        Check "NIN_POPUPCLOSE when the pointer leaves" (Wait-Until { @(Log-After "SmokeB" $n | Where-Object { $_ -match "NIN_POPUPCLOSE" }).Count -eq 1 } 2000)

        Idle
        $n = Log-Count "SmokeB"
        Hover "SmokeB"
        Start-Sleep -Milliseconds 900
        $c = Our-Icon $run "SmokeB"
        Tool click ([int]$c.rect[0] + [int]([int]$c.rect[2] / 2)) ([int]$c.rect[1] + [int]([int]$c.rect[3] / 2)) | Out-Null
        Start-Sleep -Milliseconds 1200
        $events = @(Log-After "SmokeB" $n | Where-Object { $_ -match "EVENT (NIN_POPUP\w+|WM_LBUTTONDOWN|NIN_SELECT)" } | ForEach-Object { $_ -replace ".*EVENT (\S+).*", '$1' })
        Check "a click while the popup is open closes it first, and it does not open again until the pointer comes back" (($events -join " ") -eq "NIN_POPUPOPEN NIN_POPUPCLOSE WM_LBUTTONDOWN NIN_SELECT") ($events -join " ")

        Idle
        Away
        Start-Sleep -Milliseconds 300
        $n = Log-Count "SmokeB"
        Hover "SmokeB"
        [void](Wait-Until { (Our-Icon $run "SmokeB").popupOpen } 2000)
        Send-Command $run "--hide"
        $closed = Wait-Until { @(Log-After "SmokeB" $n | Where-Object { $_ -match "NIN_POPUPCLOSE" }).Count -eq 1 } 2000
        Send-Command $run "--show"
        [void](Wait-Until { Is-Shown $run } 3000)
        Check "hiding the tray with the pointer on the icon closes its popup" $closed

        Idle
        $n = Log-Count "SmokeC"
        $m = Log-Count "SmokeA"
        Hover "SmokeC"
        Start-Sleep -Milliseconds 1000
        Hover "SmokeA"
        Start-Sleep -Milliseconds 1000
        Away
        Start-Sleep -Milliseconds 300
        $cc = Our-Icon $run "SmokeC"; $aa = Our-Icon $run "SmokeA"
        Check "version 4 with NIF_SHOWTIP, and version 0: our tooltip, and no popup messages" ($cc.showsToolTip -and $cc.shownTip -and $aa.showsToolTip -and $aa.shownTip -and
            @(Log-After "SmokeC" $n | Where-Object { $_ -match "NIN_POPUP" }).Count -eq 0 -and @(Log-After "SmokeA" $m | Where-Object { $_ -match "NIN_POPUP" }).Count -eq 0)
    }

    # --- Show/hide: commands, the hidden state, a settings edit while running ---------------
    $hotkey = (Read-State $run).hotkey
    Check "the default hotkey is registered" ($hotkey -eq "Ctrl+Alt+Shift+T") "hotkey: $hotkey (in use by another program?)"
    Send-Command $run "--hide"
    Check "--hide hides the window, and Explorer answers position queries meanwhile" (Wait-Until { Is-Hidden $run } 3000) (Last-ShowHide $run)
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name SmokeD --uid 104 --log `"$OutDir`" --color #F39C12" -PassThru
    Check "icon added while hidden reaches our host" (Wait-Until { Our-Icon $run "SmokeD" } 5000)
    Check "icon added while hidden reaches Explorer's tray" (Wait-Until { Explorer-Tip "SmokeD" } 5000)
    Post "SmokeD" 1
    Check "tooltip change while hidden reaches our host" (Wait-Until { (Our-Icon $run "SmokeD").tip -eq "SmokeD tip #1" } 5000)
    if (-not $SkipMouse) {
        Idle
        $w = (Read-State $run).window
        Tool move ([int]$w[0] + [int]($w[2] / 2)) ([int]$w[1] + [int]($w[3] / 2)) | Out-Null
        Start-Sleep -Milliseconds 200
        $d = Our-Icon $run "SmokeD"
        $answer = Tool iconrect $d.hwnd $d.uid
        $inWindow = $false
        if ($answer -match "icon rect \[(-?\d+),(-?\d+),\d+,\d+\]") {
            $inWindow = [int]$Matches[1] -ge [int]$w[0] -and [int]$Matches[1] -lt [int]$w[0] + [int]$w[2] -and [int]$Matches[2] -ge [int]$w[1] -and [int]$Matches[2] -lt [int]$w[1] + [int]$w[3]
        }
        Check "hidden, cursor on the tray's spot: apps get Explorer's answer, not the hidden window's" (-not $inWindow -and
            ($answer -like "icon rect*" -or $answer -like "*0x00000001")) "$answer; window [$($w -join ',')]"
    }
    Send-Command $run "--show"
    Check "--show shows it again, with the icon added while hidden in place" (Wait-Until { (Is-Shown $run) -and (Our-Icon $run "SmokeD").rect } 3000) (Last-ShowHide $run)
    Send-Command $run "--toggle"
    $toggledOff = Wait-Until { Is-Hidden $run } 3000
    Send-Command $run "--toggle"
    Check "--toggle hides, then shows" ($toggledOff -and (Wait-Until { Is-Shown $run } 3000)) (Last-ShowHide $run)

    # Auto-hide, switched on by editing the settings file while the app runs.
    Write-Settings '{ "showHide": { "autoHide": true, "hotspot": "Left", "hideDelayMs": 800 } }'
    Check "a settings file edit applies while running (auto-hide on)" (Wait-Until { (Read-State $run).autoHide -eq $true } 5000)
    Start-Sleep -Milliseconds 1500
    Check "switching auto-hide on does not hide the tray before it is used" (Is-Shown $run)

    # --- Show/hide with the mouse and the hotkey ---------------------------------------------
    if (-not $SkipMouse) {
        Send-Command $run "--hide"
        [void](Wait-Until { Is-Hidden $run } 3000)
        $h = (Read-State $run).hotspot
        $w = (Read-State $run).window
        $away = @(([int]$h[0] + 200), ([int]$h[1] + 200))   # on the tray's monitor, clear of tray and hotspot
        Idle
        Tool move ([int]$h[0]) ([int]$h[1] + [int]($h[3] / 2)) | Out-Null
        Check "the hotspot (left screen edge) shows the tray" (Wait-Until { Is-Shown $run } 3000) (Last-ShowHide $run)
        Tool move $away[0] $away[1] | Out-Null
        Check "it hides 0.8 s after the pointer leaves the hotspot" ((Wait-Until { Is-Hidden $run } 4000) -and
            (Last-ShowHide $run) -like "Hidden (800 ms after the pointer left)") (Last-ShowHide $run)

        if ($hotkey) {
            Idle
            Tool key ctrl+alt+shift+t | Out-Null
            Check "the hotkey shows the tray" (Wait-Until { Is-Shown $run } 2000) (Last-ShowHide $run)
            Start-Sleep -Milliseconds 1500
            Check "shown by the hotkey, the timer waits for the pointer to visit it" (Is-Shown $run)
            Idle
            Tool click-target $away[0] $away[1] | Out-Null
            Check "a click elsewhere hides it" ((Wait-Until { Is-Hidden $run } 2000) -and (Last-ShowHide $run) -like "Hidden (click away*") (Last-ShowHide $run)

            Idle
            Tool key ctrl+alt+shift+t | Out-Null
            [void](Wait-Until { Is-Shown $run } 2000)
            Start-Sleep -Milliseconds 300
            $menusBefore = @(Select-String -Path (Join-Path $OutDir "testtray-SmokeA.log") -Pattern "MENU OPEN").Count
            Idle
            [void](Click-Icon $run "SmokeA" "right")
            [void](Wait-Until { @(Select-String -Path (Join-Path $OutDir "testtray-SmokeA.log") -Pattern "MENU OPEN").Count -gt $menusBefore } 3000)
            Tool move $away[0] $away[1] | Out-Null
            Start-Sleep -Milliseconds 2000
            Check "a menu opened from the tray keeps it shown (pointer away for 2 s)" (Is-Shown $run) (Last-ShowHide $run)
            Idle
            Tool key esc | Out-Null
            Check "once that menu closes, it hides after the delay" ((Wait-Until { Is-Hidden $run } 4000) -and
                (Last-ShowHide $run) -like "Hidden (800 ms after the pointer left)") (Last-ShowHide $run)

            Idle
            Tool key ctrl+alt+shift+t | Out-Null
            $on = Wait-Until { Is-Shown $run } 2000
            Start-Sleep -Milliseconds 300
            Tool key ctrl+alt+shift+t | Out-Null
            Check "the hotkey toggles: shows, then hides" ($on -and (Wait-Until { Is-Hidden $run } 2000)) (Last-ShowHide $run)
        }
        else { Write-Host "SKIP  hotkey checks: the hotkey is not registered" }
    }
    Write-Settings '{ }'
    Check "switching auto-hide off shows the tray again" (Wait-Until { (Is-Shown $run) -and (Read-State $run).autoHide -eq $false } 5000) (Last-ShowHide $run)

    # --- Full-screen apps: the tray steps behind them, as Explorer's taskbar does -------------
    if (-not $SkipMouse) {
        $w = (Read-State $run).window
        $px = [int]$w[0] + [int]$w[2] - 3; $py = [int]$w[1] + 2   # a pixel of the tray's background
        $trayColour = Tool pixel $px $py
        function Behind { $s = Read-State $run; return [bool]($s.behindFullScreen -and -not $s.topmost -and -not $s.trayWindowVisible) }
        function OnTop { $s = Read-State $run; return [bool](-not $s.behindFullScreen -and $s.topmost -and $s.trayWindowVisible) }
        Idle
        $fs = Start-Process -FilePath $tool -ArgumentList "fullscreen-window 8000" -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $OutDir "fullscreen.txt")
        Check "a full-screen window in front: the tray steps behind it" ((Wait-Until { Behind } 3000) -and (Tool pixel $px $py) -ne $trayColour) "pixel $(Tool pixel $px $py), tray $trayColour"
        if ($hotkey) {
            Tool key ctrl+alt+shift+t | Out-Null
            Check "the hotkey brings it up over the full-screen window" ((Wait-Until { (Read-State $run).topmost -and (Read-State $run).trayWindowVisible } 2000) -and
                (Wait-Until { (Tool pixel $px $py) -eq $trayColour } 1000)) "pixel $(Tool pixel $px $py)"
            Tool click 300 300 | Out-Null   # on the full-screen window, away from the tray
            Check "a click on the full-screen window puts it back behind" (Wait-Until { Behind } 2000)
        }
        [void]$fs.WaitForExit(15000)
        Check "once the full-screen window closes, the tray is always-on-top again" ((Wait-Until { OnTop } 3000) -and (Tool pixel $px $py) -eq $trayColour)
        Idle
        $fs = Start-Process -FilePath $tool -ArgumentList "fullscreen-window 4000 f11" -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $OutDir "fullscreen-f11.txt")
        Check "a window that turns full screen (F11): the tray steps behind it" (Wait-Until { Behind } 3000)
        Check "and is always-on-top again when that window leaves full screen" (Wait-Until { OnTop } 4000)
        [void]$fs.WaitForExit(15000)
    }

    # --- Scaling: icon size and tray size, set in the settings file while the app runs --------
    $before = (Read-State $run).window
    $corner = "$([int]$before[0] + [int]$before[2]),$([int]$before[1] + [int]$before[3])"
    function Corner { $w = (Read-State $run).window; return "$([int]$w[0] + [int]$w[2]),$([int]$w[1] + [int]$w[3])" }
    function Test-Icons([scriptblock]$ok) { $s = Read-State $run; $mine = @($s.icons | Where-Object { $_.tip -like "Smoke*" }); return $mine.Count -ge 4 -and -not ($mine | Where-Object { -not (& $ok $_) }) }

    Write-Settings '{ "scaling": { "iconSize": "200%" } }'
    Check "icon size 200%: icons drawn at 32 px in 40 px cells, the size apps are told" (Wait-Until {
        (Read-State $run).layout.iconSize -eq 32 -and (Test-Icons { param($i) $i.drawnPx -eq 32 -and $i.rect[2] -eq 40 -and $i.rect[3] -eq 40 }) } 5000)
    Check "the window grows around its anchor corner" ((Corner) -eq $corner) "corner $(Corner), was $corner"
    if (-not $SkipMouse) {
        Idle
        $b = Our-Icon $run "SmokeB"
        $ourRect = "[$($b.rect[0]),$($b.rect[1]),$([int]$b.rect[0] + [int]$b.rect[2]),$([int]$b.rect[1] + [int]$b.rect[3])]"
        Check "a click on a scaled icon reaches its app, which is told the scaled rectangle" ((Click-Icon $run "SmokeB" "left") -and
            (Wait-Until { Log-Has "SmokeB" ("NIN_SELECT .*iconRect=" + [regex]::Escape($ourRect)) } 3000)) "expected iconRect=$ourRect"
    }
    Write-Settings '{ "scaling": { "iconSize": "24px", "traySize": "40px" } }'
    Check "icon size 24px, tray size 40px: 24 px icons in 40 px cells" (Wait-Until {
        Test-Icons { param($i) $i.drawnPx -eq 24 -and $i.rect[2] -eq 40 } } 5000)
    Write-Settings '{ "scaling": { "iconSize": "167%", "snapToPixels": false } }'
    Check "167% without snapping: the exact 26.72 px, drawn from a 27 px image" (Wait-Until {
        (Read-State $run).layout.iconSize -eq 26.72 -and (Test-Icons { param($i) $i.drawnPx -eq 27 }) } 5000)
    # TestTrayApp.exe has no icon of its own (its icons stay as they are); explorer.exe has one.
    Write-Settings '{ "scaling": { "iconSize": "300%", "exeIcons": ["explorer.exe", "TestTrayApp.exe"] } }'
    Check "exeIcons: Explorer's own tray icons are drawn from explorer.exe's icon at 48 px; an exe without one keeps its tray image" (Wait-Until {
        $s = Read-State $run
        $explorer = @($s.icons | Where-Object { $_.process -eq "explorer" -and $_.drawnPx })
        $explorer.Count -gt 0 -and -not ($explorer | Where-Object { $_.drawnPx -ne 48 -or $_.drawnFromPx -ne 48 }) -and
            (Test-Icons { param($i) $i.drawnPx -eq 48 -and $i.drawnFromPx -eq 16 }) } 5000)
    Write-Settings '{ }'
    Check "back to 100%: 16 px icons in 24 px cells, in the same place" ((Wait-Until { Test-Icons { param($i) $i.drawnPx -eq 16 -and $i.rect[2] -eq 24 } } 5000) -and
        (Corner) -eq $corner) "corner $(Corner), was $corner"

    # --- Themes: built-in ones, the user's own (live edits), import and export -------------
    function Theme-State { return (Read-State $run).theme }
    function Smoke-Rects { return @((Read-State $run).icons | Where-Object { $_.tip -like "Smoke*" -and $_.rect } | ForEach-Object { , @([int]$_.rect[0], [int]$_.rect[1]) }) }
    Write-Settings '{ "theme": "DarkColumn" }'
    Check "theme DarkColumn: the icons stand in one column, and the tray keeps its anchor corner" ((Wait-Until {
        $r = Smoke-Rects; (Theme-State).arrangement -eq "Column" -and $r.Count -ge 4 -and @($r | ForEach-Object { $_[0] } | Sort-Object -Unique).Count -eq 1 } 5000) -and
        (Corner) -eq $corner) "corner $(Corner), was $corner"
    Write-Settings '{ "theme": "GlassGrid" }'
    Check "theme GlassGrid: rows of 4 icons, rounded corners" (Wait-Until {
        $s = Read-State $run; $s.theme.arrangement -eq "Grid" -and $s.layout.cornerRadius -eq 8 -and
            @($s.icons | Where-Object { $_.rect } | ForEach-Object { [int]$_.rect[0] } | Sort-Object -Unique).Count -eq 4 } 5000)
    $ts = Theme-State
    Check "a blur theme blurs only while Windows' transparency effects are on, else it is solid" ($ts.blur -eq "Blur" -and $ts.blurShown -eq $ts.transparencyEffects) "blurShown $($ts.blurShown), transparency effects $($ts.transparencyEffects)"

    $themes = Join-Path $OutDir "themes"
    New-Item -ItemType Directory -Force $themes | Out-Null
    Set-Content -Path (Join-Path $themes "SmokeTheme.json") -Encoding UTF8 -Value '{ "name": "Smoke theme", "basedOn": "Glass", "border": { "cornerRadius": 12 } }'
    Write-Settings '{ "theme": "SmokeTheme" }'
    Check "a theme of one's own, based on a built-in one" (Wait-Until { $t = Theme-State; $t.id -eq "SmokeTheme" -and (Read-State $run).layout.cornerRadius -eq 12 } 5000)
    Set-Content -Path (Join-Path $themes "SmokeTheme.json") -Encoding UTF8 -Value '{ "name": "Smoke theme", "basedOn": "Glass", "border": { "cornerRadius": 3 }, "icons": { "spacing": 4 } }'
    Check "editing it applies while the app runs" (Wait-Until { $s = Read-State $run; $s.layout.cornerRadius -eq 3 -and $s.layout.spacing -eq 4 } 5000)
    Set-Content -Path (Join-Path $themes "SmokeTheme.json") -Encoding UTF8 -Value '{ "border": '
    Check "a broken edit is reported and the version in use stays" (Wait-Until { $s = Read-State $run; $s.theme.problems.Count -gt 0 -and $s.layout.cornerRadius -eq 3 } 5000) ((Theme-State).problems -join "; ")

    Write-Settings '{ "theme": "Gradient" }'
    [void](Wait-Until { (Theme-State).id -eq "Gradient" } 5000)
    $export = Join-Path $OutDir "export\Exported.json"
    $p = Start-Process -FilePath $app -ArgumentList "--export-theme `"$export`" --settings `"$(Join-Path $OutDir 'settings.json')`" --log-dir `"$run\cmd`"" -PassThru -Wait
    Check "--export-theme writes the theme with its XAML" ($p.ExitCode -eq 0 -and (Test-Path $export) -and (Test-Path (Join-Path $OutDir "export\Gradient.xaml")))
    $p = Start-Process -FilePath $app -ArgumentList "--import-theme `"$export`" --settings `"$(Join-Path $OutDir 'settings.json')`" --log-dir `"$run\cmd`"" -PassThru -Wait
    Check "--import-theme copies it into the themes folder and the running tray switches to it" ($p.ExitCode -eq 0 -and
        (Wait-Until { $t = Theme-State; $t.id -eq "Exported" -and $t.xaml -like "*\themes\Gradient.xaml" -and $t.problems.Count -eq 0 } 5000)) "theme $((Theme-State).id), xaml $((Theme-State).xaml)"

    Write-Settings '{ }'
    Check "back to Dark: one row of 24 px cells, in the same place" ((Wait-Until { (Theme-State).id -eq "Dark" -and (Test-Icons { param($i) $i.rect[2] -eq 24 }) -and
        @(Smoke-Rects | ForEach-Object { $_[1] } | Sort-Object -Unique).Count -eq 1 } 5000) -and (Corner) -eq $corner) "corner $(Corner), was $corner"

    # --- Management: the app's own icon, icon rules, the flyout, dragging, the settings window --
    function Smoke([string]$name) { Our-Icon $run $name }
    function Tray-Count { @((Read-State $run).icons | Where-Object { $_.placement -eq "Tray" }).Count }
    function Saved-Icons { (Get-Content (Join-Path $OutDir "settings.json") -Raw | ConvertFrom-Json).icons }
    function Explorer-Has-Ours { [bool](Tool explorer-icons | Select-String "'FreeSystemTray' owner=") }
    function Center([object]$r) { return @(([int]$r[0] + [int]([int]$r[2] / 2)), ([int]$r[1] + [int]([int]$r[3] / 2))) }
    # Whether the tray's menu named $what is open or closed, from the app's log.
    function Last-Menu([string]$what) {
        $line = Select-String -Path (Join-Path $run "FreeSystemTray.log") -Pattern "$what (opened|closed)$" | Select-Object -Last 1
        if ($line) { return $line.Matches[0].Groups[1].Value }
        return ""
    }
    # Opens a menu with $open; true if it is still open 0.8 s later and Esc then closes it (it has the keyboard).
    function Menu-Stays-Open([string]$what, [scriptblock]$open) {
        Idle
        & $open
        Start-Sleep -Milliseconds 800
        $stayed = (Last-Menu $what) -eq "opened"
        Idle
        Tool key esc | Out-Null
        return $stayed -and (Wait-Until { (Last-Menu $what) -eq "closed" } 2000)
    }
    $mine = (Read-State $run).icons | Where-Object { $_.path -like "*\FreeSystemTray.exe" } | Select-Object -First 1
    Check "FreeSystemTray's own icon is in the tray and in Explorer's tray" ((Read-State $run).appIcon -and $mine.placement -eq "Tray" -and (Explorer-Has-Ours)) "placement $($mine.placement)"

    Write-Settings '{ "icons": { "rules": [ { "match": "TestTrayApp.exe:103", "placement": "Overflow" }, { "match": "TestTrayApp.exe:104", "placement": "Hidden" }, { "match": "TestTrayApp.exe:102", "tooltip": "Renamed: {tip}" } ] } }'
    Check "rules: SmokeC in the hidden icons behind a chevron, SmokeD hidden, SmokeB's tooltip replaced" (Wait-Until {
        $s = Read-State $run
        (Smoke "SmokeC").placement -eq "Overflow" -and (Smoke "SmokeD").placement -eq "Hidden" -and (Smoke "SmokeB").shownTip -eq "Renamed: SmokeB tip #1" -and
            $s.chevron -and $s.overflow.count -ge 1 } 5000)
    Post "SmokeD" 1
    Check "a hidden icon keeps registering: its tooltip change reaches our host and Explorer's tray" ((Wait-Until { (Smoke "SmokeD").tip -eq "SmokeD tip #2" } 5000) -and
        (Wait-Until { (Explorer-Tip "SmokeD") -eq "SmokeD tip #2" } 5000))

    $a = Smoke "SmokeA"
    Tool explorer-hide $a.hwnd 101 on | Out-Null
    Check "an icon Windows hides (Explorer hides it; the app did not) is hidden here too" (Wait-Until { $i = Smoke "SmokeA"; $i.explorer -eq "Hidden" -and $i.placement -eq "Hidden" } 8000) "$((Smoke 'SmokeA').explorer), $((Smoke 'SmokeA').placement)"
    Tool explorer-hide $a.hwnd 101 off | Out-Null
    Check "and it comes back when Windows shows it again" (Wait-Until { (Smoke "SmokeA").placement -eq "Tray" } 8000)

    Write-Settings '{ "icons": { "default": "Overflow", "rules": [ { "match": "TestTrayApp.exe:101", "placement": "Tray" } ] } }'
    Check "icons without a rule in the hidden icons: only the icon a rule puts in the tray stays" (Wait-Until { (Tray-Count) -eq 1 -and (Smoke "SmokeA").placement -eq "Tray" } 5000) "tray: $(Tray-Count)"
    Write-Settings '{ "icons": { "maxTrayIcons": 2, "order": [ "TestTrayApp.exe:104", "TestTrayApp.exe:102" ] } }'
    Check "the saved order comes first, and a full tray spills into the hidden icons" (Wait-Until {
        (Tray-Count) -eq 2 -and (Smoke "SmokeD").index -eq 0 -and (Smoke "SmokeB").index -eq 1 -and (Smoke "SmokeA").placement -eq "Overflow" } 5000) "tray: $(Tray-Count)"

    Write-Settings '{ "icons": { "rules": [ { "match": "TestTrayApp.exe:103", "placement": "Overflow" } ] } }'
    [void](Wait-Until { (Smoke "SmokeC").placement -eq "Overflow" -and (Smoke "SmokeA").placement -eq "Tray" -and (Read-State $run).chevron } 5000)
    if (-not $SkipMouse) {
        Idle
        $c = Center (Read-State $run).chevron
        Tool click $c[0] $c[1] | Out-Null
        Check "the chevron opens the hidden-icons flyout" (Wait-Until { (Read-State $run).overflow.open -and (Smoke "SmokeC").rect } 3000)
        $ci = Smoke "SmokeC"
        $cRect = "[$($ci.rect[0]),$($ci.rect[1]),$([int]$ci.rect[0] + [int]$ci.rect[2]),$([int]$ci.rect[1] + [int]$ci.rect[3])]"
        Idle
        Check "a click on an icon in the flyout reaches its app, which is told the icon's rectangle there" ((Click-Icon $run "SmokeC" "left") -and
            (Wait-Until { Log-Has "SmokeC" ("NIN_SELECT .*iconRect=" + [regex]::Escape($cRect)) } 3000)) "expected iconRect=$cRect"
        Idle
        $w = (Read-State $run).window
        Tool click-target ([int]$w[0] - 400) ([int]$w[1] - 300) | Out-Null
        Check "a click elsewhere closes the flyout" (Wait-Until { -not (Read-State $run).overflow.open } 3000)

        # SmokeB onto the first other icon in the tray: it takes that icon's place.
        $target = (Read-State $run).icons | Where-Object { $_.placement -eq "Tray" -and $_.tip -notlike "SmokeB*" } | Sort-Object index | Select-Object -First 1
        $selects = @(Select-String -Path (Join-Path $OutDir "testtray-SmokeB.log") -Pattern "NIN_SELECT").Count
        $from = Center (Smoke "SmokeB").rect; $to = Center $target.rect
        Idle
        Tool drag $from[0] $from[1] $to[0] $to[1] | Out-Null
        Check "dragging an icon onto another moves it to that place and saves the order; its app hears no click" ((Wait-Until { (Smoke "SmokeB").index -eq $target.index } 3000) -and
            (Saved-Icons).order -contains "TestTrayApp.exe:102" -and @(Select-String -Path (Join-Path $OutDir "testtray-SmokeB.log") -Pattern "NIN_SELECT").Count -eq $selects) "SmokeB at $((Smoke 'SmokeB').index), expected $($target.index)"

        $from = Center (Smoke "SmokeA").rect; $to = Center (Read-State $run).chevron
        Idle
        Tool drag $from[0] $from[1] $to[0] $to[1] | Out-Null
        Check "dragging an icon onto the chevron puts it in the hidden icons, as a rule" ((Wait-Until { (Smoke "SmokeA").placement -eq "Overflow" } 3000) -and
            @((Saved-Icons).rules | Where-Object { $_.match -eq "TestTrayApp.exe:101" -and $_.placement -eq "Overflow" }).Count -eq 1)

        $d = Center (Smoke "SmokeD").rect
        $heard = @(Select-String -Path (Join-Path $OutDir "testtray-SmokeD.log") -Pattern "MENU OPEN|WM_RBUTTON").Count
        Check "Shift+right-click opens the tray's menu for the icon (it stays open; Esc closes it), and its app hears nothing of it" ((Menu-Stays-Open "Icon menu for SmokeD.*" {
            Tool click $d[0] $d[1] right shift | Out-Null }) -and
            @(Select-String -Path (Join-Path $OutDir "testtray-SmokeD.log") -Pattern "MENU OPEN|WM_RBUTTON").Count -eq $heard) (Last-Menu "Icon menu for SmokeD.*")

        # The tray's own menus, which took the foreground too late and closed at once in the first Phase 5 build.
        $w = (Read-State $run).window
        Check "right-click on the grip: the app menu stays open, and Esc closes it" (Menu-Stays-Open "App menu" {
            Tool click ([int]$w[0] + 4) ([int]$w[1] + [int]([int]$w[3] / 2)) right | Out-Null }) (Last-Menu "App menu")
        $mine = Center ((Read-State $run).icons | Where-Object { $_.path -like "*\FreeSystemTray.exe" } | Select-Object -First 1).rect
        Check "right-click on FreeSystemTray's own icon: the app menu stays open, and Esc closes it" (Menu-Stays-Open "App menu" {
            Tool click $mine[0] $mine[1] right | Out-Null }) (Last-Menu "App menu")
        Idle
        $c = Center (Read-State $run).chevron
        Tool click $c[0] $c[1] | Out-Null
        [void](Wait-Until { (Read-State $run).overflow.open -and (Smoke "SmokeA").rect } 3000)
        $a = Center (Smoke "SmokeA").rect
        Check "Shift+right-click on an icon in the flyout: its menu stays open, and Esc closes it" (Menu-Stays-Open "Icon menu for SmokeA.*" {
            Tool click $a[0] $a[1] right shift | Out-Null }) (Last-Menu "Icon menu for SmokeA.*")
        Idle
        Tool click-target ([int]$w[0] - 400) ([int]$w[1] - 300) | Out-Null
        [void](Wait-Until { -not (Read-State $run).overflow.open } 3000)
    }
    Write-Settings '{ }'
    [void](Wait-Until { Test-Icons { param($i) $i.placement -eq "Tray" } } 5000)

    Send-Command $run "--open-settings"
    Check "--open-settings opens the settings window" (Wait-Until { (Read-State $run).settingsWindow -eq "FreeSystemTray settings" } 5000)
    $p = Start-Process -FilePath $app -ArgumentList "--log-dir `"$run\second`"" -PassThru -Wait
    Check "starting the exe again hands over to the running one (its settings) and exits" ($p.ExitCode -eq 0 -and
        (Select-String -Path (Join-Path $run "second\FreeSystemTray.log") -Pattern "showing its settings" -Quiet))

    $hoverLeave = @(Get-ChildItem $OutDir -Filter "testtray-Smoke*.log" | Select-String -Pattern "WM_MOUSEHOVER|WM_MOUSELEAVE")
    Check "no app was sent WM_MOUSEHOVER or WM_MOUSELEAVE (Explorer sends neither)" ($hoverLeave.Count -eq 0) (($hoverLeave | Select-Object -First 2) -join "; ")

    # --- Graceful exit ----------------------------------------------------------------------
    Post "SmokeC" 1
    [void](Wait-Until { (Our-Icon $run "SmokeC").tip -eq "SmokeC tip #1" } 3000)
    Check "graceful exit returns 0" ((Stop-App $run) -eq 0)
    Check "after exit, Explorer is the tray host" (Wait-Until { Host-Is "explorer" } 5000)
    Check "after exit, Explorer's taskbar is always-on-top" (Explorer-Topmost)
    Check "after exit, Explorer has every test icon with its latest tooltip" (Wait-Until {
        (Explorer-Tip "SmokeA") -eq "SmokeA tip #1" -and (Explorer-Tip "SmokeB") -eq "SmokeB tip #1" -and (Explorer-Tip "SmokeC") -eq "SmokeC tip #1" } 5000)
    Check "the watchdog saw a clean exit" (Wait-Until { Select-String -Path (Join-Path $run "watchdog.log") -Pattern "exited cleanly" -Quiet } 5000)
    Check "after exit, FreeSystemTray's own icon is gone from Explorer's tray" (-not (Explorer-Has-Ours))

    # --- Crash --------------------------------------------------------------------------------
    $crash = Join-Path $OutDir "crash"
    $proc = Start-App $crash
    Check "takes the role again" (Host-Is "FreeSystemTray")
    Check "and its own icon is back in Explorer's tray" (Wait-Until { Explorer-Has-Ours } 5000)
    Post "SmokeC" 1
    [void](Wait-Until { (Our-Icon $crash "SmokeC").tip -eq "SmokeC tip #2" } 3000)
    Stop-Process -Id $proc.Id -Force
    Check "after a crash, Explorer is the tray host" (Wait-Until { Host-Is "explorer" } 5000)
    Check "after a crash, the watchdog hands the tray back" (Wait-Until { Select-String -Path (Join-Path $crash "watchdog.log") -Pattern "Hand-back:" -Quiet } 5000)
    Check "after a crash, Explorer has the latest tooltip" (Wait-Until { (Explorer-Tip "SmokeC") -eq "SmokeC tip #2" } 5000) "Explorer: $(Explorer-Tip 'SmokeC')"
    Check "after a crash, Explorer's taskbar is always-on-top" (Explorer-Topmost)
    Check "after a crash, the watchdog removes FreeSystemTray's own (dead) icon from Explorer's tray" (Wait-Until { -not (Explorer-Has-Ours) } 5000)
    Check "killed on purpose, it is not started again" (Wait-Until { Select-String -Path (Join-Path $crash "watchdog.log") -Pattern "Not starting FreeSystemTray again: it did not crash" -Quiet } 5000)

    # --- Crash safety: a real crash and a hang (Phase 6) --------------------------------------
    $cs = Join-Path $OutDir "crashsafety"
    $proc = Start-App $cs
    $first = (Read-State $cs).pid
    Start-Process -FilePath $app -ArgumentList "--debug-crash --log-dir `"$cs\cmd`"" -Wait | Out-Null
    Check "a real crash: the watchdog hands the tray back and starts FreeSystemTray again, which takes the role" ((Wait-Until {
        Select-String -Path (Join-Path $cs "watchdog.log") -Pattern "Started FreeSystemTray again after the crash" -Quiet } 10000) -and
        (Wait-Until { $s = Read-State $cs; $s.pid -ne $first -and $s.shell.weAreHost -and $s.iconCount -gt 0 } 15000))
    Check "the log has the exception, and the new instance knows it was started again after a crash" ((Select-String -Path (Join-Path $cs "FreeSystemTray.log") -Pattern "Unhandled exception.*--debug-crash" -Quiet) -and
        (Select-String -Path (Join-Path $cs "FreeSystemTray.log") -Pattern "Started again by the watchdog after a crash \(1 in a row\)" -Quiet) -and
        (Select-String -Path (Join-Path $cs "watchdog.log") -Pattern "exit code 0xE0434352" -Quiet))
    $second = (Read-State $cs).pid
    Start-Sleep -Milliseconds 1000
    Start-Process -FilePath $app -ArgumentList "--debug-hang 120 --log-dir `"$cs\cmd`"" -Wait | Out-Null
    Check "a tray that stops answering: the watchdog ends it after about 25 s, hands the tray back and starts it again" ((Wait-Until {
        Select-String -Path (Join-Path $cs "watchdog.log") -Pattern "is hung" -Quiet } 60000) -and
        (Wait-Until { $s = Read-State $cs; $s.pid -ne $second -and $s.shell.weAreHost -and $s.iconCount -gt 0 } 20000)) ((Select-String -Path (Join-Path $cs "watchdog.log") -Pattern "answer|hung|Started" | Select-Object -Last 3 | ForEach-Object { $_.Line }) -join "; ")
    Check "after the restarts, Explorer's tray is still complete" ((Explorer-Tip "SmokeA") -and (Explorer-Tip "SmokeB") -and (Explorer-Tip "SmokeC"))
    Check "and a graceful exit of the restarted tray returns 0" ((Stop-App $cs) -eq 0)
}
finally {
    if (Get-Process FreeSystemTray -ErrorAction SilentlyContinue) { [void](Stop-App (Join-Path $OutDir "cleanup")) }
    foreach ($name in "SmokeA", "SmokeB", "SmokeC", "SmokeD", "SmokeE") { Post $name 6 }   # each test app deletes its icon and exits
    Start-Sleep -Milliseconds 800
    foreach ($p in $script:started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}

Write-Host ""
if ($script:failures -eq 0) { Write-Host "All checks passed. Logs: $OutDir" -ForegroundColor Green }
else { Write-Host "$($script:failures) check(s) failed. Logs: $OutDir" -ForegroundColor Red }
exit $script:failures
