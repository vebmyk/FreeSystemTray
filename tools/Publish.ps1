<#
.SYNOPSIS
  Builds the portable FreeSystemTray: a folder, and a zip of it, that runs without installing anything.

.DESCRIPTION
  Publishes src/FreeSystemTray as one exe for win-x64 with the .NET runtime inside (self-contained,
  compressed), and puts next to it:
    - Themes\: the built-in themes, read at run time;
    - portable.txt: while it is there, settings, themes and logs go in a "data" folder next to the
      exe instead of %LOCALAPPDATA%\FreeSystemTray (delete it for the latter);
    - README.md, THIRD-PARTY-NOTICES.txt and ManagedShell-LICENSE.txt (Apache-2.0 asks for the
      license to travel with a build).
  Debug symbols are embedded in the exe, so stack traces in the log have line numbers.
  -FrameworkDependent makes a small exe that needs the .NET 10 Desktop Runtime installed instead.

  Output: dist\FreeSystemTray-VERSION-win-x64[-framework-dependent]\ and the same name .zip.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Publish.ps1
#>
param(
    [switch]$FrameworkDependent,
    [string]$Runtime = "win-x64",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist")
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $root "src\FreeSystemTray\FreeSystemTray.csproj"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "FreeSystemTray-$version-$Runtime" + $(if ($FrameworkDependent) { "-framework-dependent" } else { "" })
New-Item -ItemType Directory -Force $OutDir | Out-Null
$folder = Join-Path (Resolve-Path $OutDir) $name
if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }

$publish = @("publish", $project, "-c", "Release", "-r", $Runtime, "-o", $folder, "-nologo", "-v", "q",
    "-p:PublishSingleFile=true", "-p:DebugType=embedded")
if ($FrameworkDependent) { $publish += @("--self-contained", "false") }
else { $publish += @("--self-contained", "true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true") }
& dotnet @publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Set-Content -Path (Join-Path $folder "portable.txt") -Encoding UTF8 -Value @"
This copy of FreeSystemTray is portable: while this file is here, it keeps its settings, themes and
logs in the "data" folder next to FreeSystemTray.exe. Delete this file to keep them in
%LOCALAPPDATA%\FreeSystemTray instead.
"@
Copy-Item (Join-Path $root "README.md") $folder
Copy-Item (Join-Path $root "licenses\THIRD-PARTY-NOTICES.txt") $folder
Copy-Item (Join-Path $root "licenses\ManagedShell-LICENSE.txt") $folder

$zip = "$folder.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $folder -DestinationPath $zip
$exe = Get-Item (Join-Path $folder "FreeSystemTray.exe")
Write-Host ("{0}: FreeSystemTray.exe {1:N1} MB; {2} {3:N1} MB" -f $folder, ($exe.Length / 1MB), (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1MB))
