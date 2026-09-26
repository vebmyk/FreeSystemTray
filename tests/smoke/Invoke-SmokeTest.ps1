<#
.SYNOPSIS
  End-to-end smoke test of FreeSystemTray against the real shell. Run it before every ManagedShell
  upgrade and after changes to the tray host.

.DESCRIPTION
  Uses the Phase 0 test app (spike/TestTrayApp) and harness (spike/SpikeTool). It starts three test
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
    - with -SkipMouse left out: stepping behind a full-screen window (a new one, and one turning
      full screen like F11) and the hotkey bringing the tray up over it;
    - hand-back on a graceful exit and after the process is killed (watchdog).
  The balloon check shows a real Windows notification ("Balloon from SmokeA").
  The mouse and keyboard checks move the real mouse and press the tray's hotkey (Ctrl+Alt+Shift+T)
  and Esc, and the full-screen checks cover the primary screen with a black window for up to 8 s.
  Before each one the test waits until nobody has touched mouse or keyboard for 1.5 s.

  Tested on Windows 10 22H2 with "Always show all icons" off. FreeSystemTray must not be running.
  Exit code: the number of failed checks.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-SmokeTest.ps1
#>
param(
    [string]$OutDir = (Join-Path $env:TEMP ("FreeSystemTray-smoke-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
    [string]$Configuration = "Debug",
    [switch]$SkipBuild,
    [switch]$SkipMouse
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$tfm = "net10.0-windows"
$app = Join-Path $root "src\FreeSystemTray\bin\$Configuration\$tfm\FreeSystemTray.exe"
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
    [void](Wait-Until { (Explorer-Tip "SmokeA") -and (Explorer-Tip "SmokeB") })
    $run = Join-Path $OutDir "run"
    $proc = Start-App $run

    Check "takes the tray host role" (Host-Is "FreeSystemTray")
    Check "Explorer's taskbar stays always-on-top" (Explorer-Topmost)
    Check "test icons appear in our window" ((Our-Icon $run "SmokeA") -and (Our-Icon $run "SmokeB"))

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

    # --- Graceful exit ----------------------------------------------------------------------
    Post "SmokeC" 1
    [void](Wait-Until { (Our-Icon $run "SmokeC").tip -eq "SmokeC tip #1" } 3000)
    Check "graceful exit returns 0" ((Stop-App $run) -eq 0)
    Check "after exit, Explorer is the tray host" (Wait-Until { Host-Is "explorer" } 5000)
    Check "after exit, Explorer's taskbar is always-on-top" (Explorer-Topmost)
    Check "after exit, Explorer has every test icon with its latest tooltip" (Wait-Until {
        (Explorer-Tip "SmokeA") -eq "SmokeA tip #1" -and (Explorer-Tip "SmokeB") -eq "SmokeB tip #1" -and (Explorer-Tip "SmokeC") -eq "SmokeC tip #1" } 5000)
    Check "the watchdog saw a clean exit" (Wait-Until { Select-String -Path (Join-Path $run "watchdog.log") -Pattern "exited cleanly" -Quiet } 5000)

    # --- Crash --------------------------------------------------------------------------------
    $crash = Join-Path $OutDir "crash"
    $proc = Start-App $crash
    Check "takes the role again" (Host-Is "FreeSystemTray")
    Post "SmokeC" 1
    [void](Wait-Until { (Our-Icon $crash "SmokeC").tip -eq "SmokeC tip #2" } 3000)
    Stop-Process -Id $proc.Id -Force
    Check "after a crash, Explorer is the tray host" (Wait-Until { Host-Is "explorer" } 5000)
    Check "after a crash, the watchdog hands the tray back" (Wait-Until { Select-String -Path (Join-Path $crash "watchdog.log") -Pattern "Hand-back:" -Quiet } 5000)
    Check "after a crash, Explorer has the latest tooltip" (Wait-Until { (Explorer-Tip "SmokeC") -eq "SmokeC tip #2" } 5000) "Explorer: $(Explorer-Tip 'SmokeC')"
    Check "after a crash, Explorer's taskbar is always-on-top" (Explorer-Topmost)
}
finally {
    if (Get-Process FreeSystemTray -ErrorAction SilentlyContinue) { [void](Stop-App (Join-Path $OutDir "cleanup")) }
    foreach ($name in "SmokeA", "SmokeB", "SmokeC", "SmokeD") { Post $name 6 }   # each test app deletes its icon and exits
    Start-Sleep -Milliseconds 800
    foreach ($p in $script:started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}

Write-Host ""
if ($script:failures -eq 0) { Write-Host "All checks passed. Logs: $OutDir" -ForegroundColor Green }
else { Write-Host "$($script:failures) check(s) failed. Logs: $OutDir" -ForegroundColor Red }
exit $script:failures
