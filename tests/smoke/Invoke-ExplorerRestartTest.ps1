<#
.SYNOPSIS
  End-to-end test of Explorer restarting while FreeSystemTray is the tray host: once followed by a
  graceful exit, once followed by a crash (the process killed). Both cases broke the hand-back to
  Explorer with ManagedShell as shipped (FINDINGS.md, Phase 0).

.DESCRIPTION
  Uses the test app and harness in spike/ like the smoke test. Starts three test apps (one that does
  not register again when a taskbar broadcasts TaskbarCreated, one with two icons), notes which icons
  Explorer's tray has, starts FreeSystemTray in diagnostic mode, and then twice:
    - kills explorer.exe (Windows starts it again; the test starts it if Windows has not within 15 s);
    - checks that FreeSystemTray keeps the tray host role, that its tray has Explorer's own icons with
      their new windows and none of the old ones, and that Explorer's new tray gets every icon back,
      including those of the app that ignores TaskbarCreated (FreeSystemTray gives it those);
    - exits FreeSystemTray (round 1) or kills it (round 2), and checks that Explorer's tray still has
      every icon it had at the start, is the tray host, and is always-on-top.

  Restarting Explorer closes File Explorer windows, so the test refuses to run while one is open. The
  taskbar disappears for a few seconds each time. FreeSystemTray must not be running.
  Tested on Windows 10 22H2. Exit code: the number of failed checks.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\smoke\Invoke-ExplorerRestartTest.ps1
#>
param(
    [string]$OutDir = (Join-Path $env:TEMP ("FreeSystemTray-restart-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
    [string]$Configuration = "Debug",
    [string]$AppPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$tfm = "net10.0-windows"
$app = if ($AppPath) { $AppPath } else { Join-Path $root "src\FreeSystemTray\bin\$Configuration\$tfm\FreeSystemTray.exe" }
$testApp = Join-Path $root "spike\TestTrayApp\bin\$Configuration\$tfm\TestTrayApp.exe"
$tool = Join-Path $root "spike\SpikeTool\bin\$Configuration\$tfm\SpikeTool.exe"
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

function Host-Is([string]$process) { return [bool](Tool traywnds | Select-String "FindWindow\(Shell_TrayWnd\) -> \S+ $process\(") }
function Explorer-Topmost { return [bool](Tool traywnds | Select-String "explorer\(\d+\) topmost=True") }

# Explorer's tray as a set of names: the GUID if the icon has one, else "process:uID".
function Explorer-Icons {
    Tool explorer-icons | ForEach-Object {
        if ($_ -match "owner=(\S+)\(\d+\).* uid=(\d+) .*?( guid=(\S+))?( rect=|$)") {
            if ($Matches[4]) { $Matches[4] } else { "$($Matches[1]):$($Matches[2])" }
        }
    } | Sort-Object -Unique
}

function Explorer-Tip([string]$name) {
    $line = Tool explorer-icons | Select-String "'$name tip #\d+'" | Select-Object -First 1
    if ($line) { return ($line.Line -split "'")[1] }
    return $null
}

function Explorer-Pid { (Get-Process explorer -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1).Id }

function Start-App([string]$dir) {
    New-Item -ItemType Directory -Force $dir | Out-Null
    $p = Start-Process -FilePath $app -ArgumentList "--diag `"$dir`" --settings `"$(Join-Path $OutDir 'settings.json')`"" -PassThru
    [void](Wait-Until { $s = Read-State $dir; $s -and $s.shell.weAreHost -and $s.iconCount -gt 0 } 15000)
    Start-Sleep -Milliseconds 800
    return $p
}

function Restart-Explorer {
    $old = Explorer-Pid
    Stop-Process -Name explorer -Force
    [void](Wait-Until { $n = Explorer-Pid; $n -and $n -ne $old } 15000)
    if (-not (Explorer-Pid)) { Start-Process explorer.exe | Out-Null }
    [void](Wait-Until { Tool traywnds | Select-String "explorer\(\d+\)" } 20000)
}

# Checks after Explorer restarted while $dir's FreeSystemTray hosts the tray.
function Check-After-Restart([string]$dir, [string[]]$expected, [string]$round) {
    Check "$round Explorer restarted: FreeSystemTray keeps the tray host role" (Wait-Until { Host-Is "FreeSystemTray" } 10000)
    $given = Wait-Until { Select-String -Path (Join-Path $dir "FreeSystemTray.log") -Pattern "Gave Explorer's new tray|Explorer's new tray has all" -Quiet } 20000
    $missing = @()
    $ok = Wait-Until { $now = @(Explorer-Icons); $script:missing = @($expected | Where-Object { $now -notcontains $_ }); $script:missing.Count -eq 0 } 10000
    Check "$round Explorer's new tray has every icon again, those of the app that ignores TaskbarCreated too" ($given -and $ok) "missing: $($script:missing -join ', ')"
    $s = Read-State $dir
    $dead = @($s.icons | Where-Object { -not $_.windowAlive })
    $explorerOwn = @($s.icons | Where-Object { $_.process -eq "explorer" })
    Check "$round our tray has Explorer's own icons with their new windows, and no icon of a window that no longer exists" ($dead.Count -eq 0 -and
        $explorerOwn.Count -ge 3) "dead: $(($dead | ForEach-Object { $_.tip }) -join ', '); Explorer's own: $($explorerOwn.Count)"
    Check "$round Explorer's new taskbar is always-on-top" (Explorer-Topmost)
}

# --- Preconditions -------------------------------------------------------------------------
if (Get-Process FreeSystemTray -ErrorAction SilentlyContinue) { throw "FreeSystemTray is running; exit it first (FreeSystemTray.exe --exit)." }
if (Tool windows CabinetWClass | Select-String "CabinetWClass") { throw "A File Explorer window is open; restarting Explorer would close it. Close it first." }
if (-not $SkipBuild -and -not $AppPath) {
    & dotnet build (Join-Path $root "FreeSystemTray.sln") -c $Configuration -v q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
Write-Host "Explorer restart test output: $OutDir"
if (-not (Host-Is "explorer")) { throw "Explorer is not the tray host; is Explorer running?" }

try {
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name RstA --uid 201 --log `"$OutDir`" --color #C0392B" -PassThru
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name RstB --uid 202 --v4 --ignore-taskbarcreated --log `"$OutDir`" --color #2E86C1" -PassThru
    $script:started += Start-Process -FilePath $testApp -ArgumentList "--name RstC --uid 203 --icons 2 --log `"$OutDir`" --color #27AE60" -PassThru
    [void](Wait-Until { (Explorer-Tip "RstA") -and (Explorer-Tip "RstB") -and (Explorer-Tip "RstC") })
    Start-Sleep -Milliseconds 500
    $expected = @(Explorer-Icons)
    Write-Host "Explorer's tray at the start: $($expected.Count) icons"
    Set-Content -Path (Join-Path $OutDir "explorer-icons-start.txt") -Value ($expected -join "`r`n")

    # --- Round 1: Explorer restarts while we host, then we exit -------------------------------
    $r1 = Join-Path $OutDir "run1"
    $p = Start-App $r1
    Check "takes the tray host role" (Host-Is "FreeSystemTray")
    $own = (Read-State $r1).icons | Where-Object { $_.path -like "*\FreeSystemTray.exe" } | Select-Object -First 1
    $expectedWithOurs = $expected + @("FreeSystemTray:1")
    Restart-Explorer
    Check-After-Restart $r1 $expectedWithOurs "(1)"
    Tool post "TestTrayApp:RstA" 1 | Out-Null
    Check "(1) a tooltip change after the restart reaches our tray and Explorer's" ((Wait-Until { (Explorer-Tip "RstA") -eq "RstA tip #1" } 5000) -and
        (Wait-Until { ((Read-State $r1).icons | Where-Object { $_.tip -eq "RstA tip #1" }) } 3000))
    $x = Start-Process -FilePath $app -ArgumentList "--exit --log-dir `"$r1\exit`"" -PassThru -Wait
    Check "(1) then a graceful exit returns 0" ($x.ExitCode -eq 0)
    Check "(1) Explorer is the tray host, always-on-top" ((Wait-Until { Host-Is "explorer" } 5000) -and (Explorer-Topmost))
    $ok = Wait-Until { $now = @(Explorer-Icons); $script:missing = @($expected | Where-Object { $now -notcontains $_ }); $script:missing.Count -eq 0 } 10000
    Check "(1) Explorer's tray has every icon it had at the start, its own included" $ok "missing: $($script:missing -join ', ')"
    Check "(1) and FreeSystemTray's own icon is gone from it" (Wait-Until { @(Explorer-Icons) -notcontains "FreeSystemTray:1" } 5000)

    # --- Round 2: Explorer restarts while we host, then we crash ------------------------------
    $r2 = Join-Path $OutDir "run2"
    $p = Start-App $r2
    Check "(2) takes the tray host role again" (Host-Is "FreeSystemTray")
    Restart-Explorer
    Check-After-Restart $r2 $expectedWithOurs "(2)"
    Tool post "TestTrayApp:RstC" 1 | Out-Null
    [void](Wait-Until { ((Read-State $r2).icons | Where-Object { $_.tip -eq "RstC tip #1" }) } 3000)
    Stop-Process -Id $p.Id -Force
    Check "(2) then after a crash, Explorer is the tray host, always-on-top" ((Wait-Until { Host-Is "explorer" } 5000) -and (Explorer-Topmost))
    Check "(2) the watchdog hands the tray back" (Wait-Until { Select-String -Path (Join-Path $r2 "watchdog.log") -Pattern "Hand-back:" -Quiet } 5000)
    $ok = Wait-Until { $now = @(Explorer-Icons); $script:missing = @($expected | Where-Object { $now -notcontains $_ }); $script:missing.Count -eq 0 } 10000
    Check "(2) Explorer's tray has every icon it had at the start, its own included" $ok "missing: $($script:missing -join ', ')"
    Check "(2) with the latest tooltip" ((Explorer-Tip "RstC") -eq "RstC tip #1") "Explorer: $(Explorer-Tip 'RstC')"
    Check "(2) and FreeSystemTray's own (dead) icon is gone from it" (Wait-Until { @(Explorer-Icons) -notcontains "FreeSystemTray:1" } 5000)
}
finally {
    if (Get-Process FreeSystemTray -ErrorAction SilentlyContinue) { Start-Process -FilePath $app -ArgumentList "--exit --log-dir `"$OutDir\cleanup`"" -Wait | Out-Null }
    if (-not (Get-Process explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe | Out-Null }
    foreach ($name in "RstA", "RstB", "RstC") { Tool post "TestTrayApp:$name" 6 | Out-Null }   # each deletes its icons and exits
    Start-Sleep -Milliseconds 800
    foreach ($p in $script:started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}

Write-Host ""
if ($script:failures -eq 0) { Write-Host "All checks passed. Logs: $OutDir" -ForegroundColor Green }
else { Write-Host "$($script:failures) check(s) failed. Logs: $OutDir" -ForegroundColor Red }
exit $script:failures
