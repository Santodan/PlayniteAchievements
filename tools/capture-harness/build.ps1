# Compiles the capture-harness tools with Roslyn against .NET Framework 4.6.2.
#
# These are diagnostics, not part of the plugin build: they reference the plugin's own assemblies from
# source\bin\Debug, so build the plugin first. Executables go to bin\ here, which is git-ignored.
$ErrorActionPreference = 'Stop'

$here = Split-Path $PSCommandPath
$repo = Resolve-Path (Join-Path $here '..\..')
$pluginBin = Join-Path $repo 'source\bin\Debug'
$out = Join-Path $here 'bin'

# Find Roslyn wherever Visual Studio or the Build Tools put it, rather than assuming an edition.
$csc = @(
    Get-ChildItem -ErrorAction SilentlyContinue -Recurse -Filter 'csc.exe' -Path @(
        "${env:ProgramFiles}\Microsoft Visual Studio",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio"
    ) | Where-Object { $_.FullName -match '\\MSBuild\\.*\\Roslyn\\csc\.exe$' } | Select-Object -First 1
) | ForEach-Object { $_.FullName }

if (-not $csc) {
    # Falls back to the framework compiler; it predates some modern syntax but is worth trying.
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
}

$refDir = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2"

if (-not (Test-Path $csc)) {
    throw 'no C# compiler found: install Visual Studio 2022 or the Build Tools'
}

if (-not (Test-Path $refDir)) {
    throw "no .NET Framework 4.6.2 reference assemblies at $refDir (install the 4.6.2 targeting pack)"
}

if (-not (Test-Path $pluginBin)) {
    throw "no plugin output at $pluginBin - build source\PlayniteAchievements.csproj first"
}

Write-Output "  compiler $csc"

New-Item -ItemType Directory -Force $out | Out-Null

# SharpDX and NAudio travel with the plugin's output; copy them next to the tools so they run
# without probing paths. NAudio is for ChimeSeparationProbe (tone render + IWaveIn plumbing).
Copy-Item (Join-Path $pluginBin 'SharpDX*.dll') $out -Force
Copy-Item (Join-Path $pluginBin 'NAudio.dll') $out -Force
# Playnite.SDK is for ChimeBurstProbe, whose compiled-in AudioLoopbackRecorder takes an ILogger.
Copy-Item (Join-Path $repo 'source\packages\PlayniteSDK.6.14.0\lib\net462\Playnite.SDK.dll') $out -Force
$valueTuple = Get-ChildItem (Join-Path $repo 'source\packages') -Recurse -Filter 'System.ValueTuple.dll' |
    Where-Object { $_.FullName -match 'net4' } | Select-Object -First 1
if ($valueTuple) { Copy-Item $valueTuple.FullName $out -Force }

# WPF is here for SlideProbe and SlideStoryboardProbe, which drive a real layered window and the
# composition tick. The extra references are harmless to the tools that ignore them.
$framework = @(
    'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms',
    'PresentationCore', 'PresentationFramework', 'WindowsBase', 'System.Xaml'
) | ForEach-Object { "/r:$refDir\$_.dll" }
$sharp = Get-ChildItem $out -Filter 'SharpDX*.dll' | ForEach-Object { "/r:$($_.FullName)" }
$sharp += "/r:$out\NAudio.dll"
$sharp += "/r:$out\Playnite.SDK.dll"
$tuple = if (Test-Path (Join-Path $out 'System.ValueTuple.dll')) { @("/r:$out\System.ValueTuple.dll") } else { @() }
$refs = $framework + $sharp + $tuple

$tools = @(
    'CaptureHarness', 'FrameDump', 'AttributeBisect', 'PacerProbe', 'GenerationLoss',
    'SlideProbe', 'SlideStoryboardProbe', 'SlideCadenceProbe', 'ChimeCancelProbe',
    'ChimeSeparationProbe', 'ChimeBurstProbe')
# Tools that compile plugin source files in directly, so they always test the current algorithm
# rather than a built DLL.
$extraSources = @{
    ChimeCancelProbe = @((Join-Path $repo 'source\Services\Capture\PcmAudio.cs'))
    ChimeSeparationProbe = @(
        (Join-Path $repo 'source\Services\Capture\PcmAudio.cs'),
        (Join-Path $repo 'source\Services\Recording\ProcessLoopbackCapture.cs'),
        (Join-Path $repo 'source\Common\MonotonicUtcClock.cs'))
    ChimeBurstProbe = @(
        (Join-Path $repo 'source\Services\Capture\PcmAudio.cs'),
        (Join-Path $repo 'source\Services\Recording\ProcessLoopbackCapture.cs'),
        (Join-Path $repo 'source\Services\Recording\AudioLoopbackRecorder.cs'),
        (Join-Path $repo 'source\Services\Recording\RecordingPaths.cs'),
        (Join-Path $repo 'source\Models\Settings\RecordingEnums.cs'),
        (Join-Path $repo 'source\Common\MonotonicUtcClock.cs'))
}
# Tools that need Environment.OSVersion to report the real Windows version (the manifest opts out
# of the 6.2 compatibility shim); ProcessLoopbackCapture.IsSupported depends on it.
$manifestTools = @('ChimeSeparationProbe', 'ChimeBurstProbe')
$failed = @()
foreach ($tool in $tools) {
    $source = Join-Path $here ($tool + '.cs')
    if (-not (Test-Path $source)) { Write-Output "  skip    $tool (no source)"; continue }
    $sources = @($source) + @($extraSources[$tool])  | Where-Object { $_ }
    $manifest = if ($manifestTools -contains $tool) { @("/win32manifest:$(Join-Path $here 'win10.manifest')") } else { @() }

    $exe = Join-Path $out ($tool + '.exe')
    # CaptureHarness loads the plugin into the process and therefore must match Playnite's x86
    # runtime. Keep the standalone analysis tools x64 so large frame dumps are not VA-constrained.
    $platform = if ($tool -eq 'CaptureHarness') { 'x86' } else { 'x64' }
    $messages = & $csc /nologo /t:exe /langversion:preview /nostdlib+ "/platform:$platform" $manifest $refs "/out:$exe" $sources 2>&1 |
        Where-Object { $_ -match 'error CS' }
    if ($messages) {
        $failed += $tool
        Write-Output "  FAILED  $tool"
        $messages | Select-Object -First 5 | ForEach-Object { Write-Output "            $_" }
    }
    else {
        Write-Output "  built   $tool"
    }
}

Write-Output ''
Write-Output ($failed.Count -eq 0 ? "all tools built into $out" : "failed: $($failed -join ', ')")
if ($failed.Count -gt 0) { exit 1 }
