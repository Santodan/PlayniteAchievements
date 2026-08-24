#requires -Version 5.1
#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [ValidateRange(30, 86400)]
    [int] $Seconds = 300,

    [ValidateRange(1, 240)]
    [int] $Fps = 60,

    [string] $PluginDir,

    [ValidateRange(10, 3600)]
    [int] $ExportEverySeconds = 20,

    [ValidateRange(0, 1000)]
    [int] $TeardownCycles = 10,

    [ValidateRange(320, 3840)]
    [int] $Width = 1280,

    [ValidateRange(240, 2160)]
    [int] $Height = 720,

    [switch] $MediaFoundationOnly
)

$ErrorActionPreference = 'Stop'
$harnessDir = $PSScriptRoot
$repoRoot = Split-Path (Split-Path $harnessDir -Parent) -Parent
$buildScript = Join-Path $harnessDir 'build.ps1'
$harnessExe = Join-Path $harnessDir 'bin\CaptureHarness.exe'
if (-not $PluginDir) {
    $PluginDir = Join-Path $repoRoot 'source\bin\Debug'
}

$gflags = @(
    'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\gflags.exe',
    'C:\Program Files (x86)\Windows Kits\10\Debuggers\x86\gflags.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
$cdb = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x86\cdb.exe'
if (-not $gflags -or -not (Test-Path $cdb)) {
    throw 'Install Debugging Tools for Windows (GFlags and the x86 CDB) from the Windows SDK first.'
}

& $buildScript
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $harnessExe)) {
    throw 'Capture harness build failed.'
}

$PluginDir = (Resolve-Path $PluginDir).Path
$logDir = Join-Path $repoRoot 'artifacts\pageheap'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $logDir ("pageheap-$stamp.log")

$enableAttempted = $false
try {
    $enableAttempted = $true
    & $gflags /p /enable CaptureHarness.exe /full
    if ($LASTEXITCODE -ne 0) {
        throw 'GFlags could not enable full page heap for CaptureHarness.exe.'
    }

    Write-Output "Full page heap enabled; debugger log: $logPath"

    # Ignore first-chance AVs but stop at an unhandled one. After a clean target exit the remaining
    # debugger commands are harmless; after a fault they preserve the analysis and native stack.
    $debugCommands = 'sxd av; g; !analyze -v; kv; q'
    $mode = if ($MediaFoundationOnly) { '--mf-stress' } else { '--stress' }
    $targetArgs = @(
        $mode, $Seconds, $Fps, $PluginDir, $ExportEverySeconds, $TeardownCycles, $Width, $Height
    )
    & $cdb -o -logo $logPath -c $debugCommands $harnessExe @targetArgs
    $debuggerExit = $LASTEXITCODE

    $logText = Get-Content -Raw $logPath
    if ($logText -match '!!! second chance !!!' -or
        $logText -match 'STATUS_HEAP_CORRUPTION' -or
        $logText -match 'heap block at .* modified') {
        throw "The debugger caught an unhandled/page-heap fault. Inspect $logPath"
    }

    if ($debuggerExit -ne 0) {
        throw "CaptureHarness exited through CDB with code $debuggerExit. Inspect $logPath"
    }

    Write-Output "Page-heap soak completed without a detected native fault: $logPath"
}
finally {
    if ($enableAttempted) {
        & $gflags /p /disable CaptureHarness.exe
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'GFlags could not disable page heap; disable it manually before another run.'
        }
        else {
            Write-Output 'Full page heap disabled for CaptureHarness.exe.'
        }
    }
}
