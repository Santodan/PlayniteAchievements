# Compiles the capture-harness tools with Roslyn against .NET Framework 4.6.2.
#
# These are diagnostics, not part of the plugin build: they reference the plugin's own assemblies from
# source\bin\Debug, so build the plugin first. Executables go to bin\ here, which is git-ignored.
$ErrorActionPreference = 'Stop'

$here = Split-Path $PSCommandPath
$repo = Resolve-Path (Join-Path $here '..\..')
$pluginBin = Join-Path $repo 'source\bin\Debug'
$out = Join-Path $here 'bin'

$csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'

foreach ($required in @($csc, $refDir, $pluginBin)) {
    if (-not (Test-Path $required)) {
        throw "not found: $required" + ($required -eq $pluginBin ? ' (build the plugin first)' : '')
    }
}

New-Item -ItemType Directory -Force $out | Out-Null

# SharpDX travels with the plugin's output; copy it next to the tools so they run without probing paths.
Copy-Item (Join-Path $pluginBin 'SharpDX*.dll') $out -Force
$valueTuple = Get-ChildItem (Join-Path $repo 'source\packages') -Recurse -Filter 'System.ValueTuple.dll' |
    Where-Object { $_.FullName -match 'net4' } | Select-Object -First 1
if ($valueTuple) { Copy-Item $valueTuple.FullName $out -Force }

$framework = @('mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms') |
    ForEach-Object { "/r:$refDir\$_.dll" }
$sharp = Get-ChildItem $out -Filter 'SharpDX*.dll' | ForEach-Object { "/r:$($_.FullName)" }
$tuple = if (Test-Path (Join-Path $out 'System.ValueTuple.dll')) { @("/r:$out\System.ValueTuple.dll") } else { @() }
$refs = $framework + $sharp + $tuple

$tools = @('CaptureHarness', 'FrameDump', 'AttributeBisect', 'PacerProbe', 'GenerationLoss')
$failed = @()
foreach ($tool in $tools) {
    $source = Join-Path $here ($tool + '.cs')
    if (-not (Test-Path $source)) { Write-Output "  skip    $tool (no source)"; continue }

    $exe = Join-Path $out ($tool + '.exe')
    $messages = & $csc /nologo /t:exe /langversion:preview /nostdlib+ /platform:x64 $refs "/out:$exe" $source 2>&1 |
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
