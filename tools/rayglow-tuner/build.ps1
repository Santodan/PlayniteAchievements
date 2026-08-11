# Builds the ray glow tuner with Roslyn against .NET Framework 4.6.2.
#
# The tuner drives the plugin's own track builder and arrow layout, compiled straight from source, so
# what it draws is what the plugin will draw. It does NOT reference source\bin\Debug: those three files
# only need the framework, which keeps the tuner usable while the rest of the plugin is mid-edit.
#
# The tuning numbers are `const` in the shipped source, which cannot be moved by a slider, so the two
# files are copied into obj\ with those specific constants rewritten as static fields. Nothing in the
# repository is modified. If a rename ever breaks the rewrite the compile fails loudly rather than
# silently tuning something that is no longer there.
$ErrorActionPreference = 'Stop'

$here = Split-Path $PSCommandPath
$repo = Resolve-Path (Join-Path $here '..\..')
$obj = Join-Path $here 'obj'
$out = Join-Path $here 'bin'

$csc = 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'

foreach ($required in @($csc, $refDir)) {
    if (-not (Test-Path $required)) { throw "not found: $required" }
}

New-Item -ItemType Directory -Force $obj | Out-Null
New-Item -ItemType Directory -Force $out | Out-Null

# Constants the tuner needs to move at runtime, per source file.
$unfreeze = @{
    'source\Views\Controls\RayGlow\RayArrowLayout.cs' = @(
        'DefaultArrowCount',
        'PrimaryLobes', 'SecondaryLobes', 'TertiaryLobes',
        'PrimaryAmplitude', 'SecondaryAmplitude', 'TertiaryAmplitude',
        'PrimaryPhase', 'SecondaryPhase', 'TertiaryPhase',
        'EnvelopeDriftRatio', 'AlternationAmplitude',
        'MinHeightFraction', 'SlendernessRatio', 'MaxWidthFraction',
        'InwardFraction', 'MinInwardDip', 'TipWidthFraction'
    )
    'source\Services\Images\RayTrackBuilder.cs'       = @(
        'ChaikinIterations', 'SmoothingSampleCount', 'SmoothingPasses', 'AlphaThreshold'
    )
}

$patched = @()
foreach ($relative in $unfreeze.Keys) {
    $sourcePath = Join-Path $repo $relative
    if (-not (Test-Path $sourcePath)) { throw "not found: $sourcePath" }

    $text = Get-Content $sourcePath -Raw
    foreach ($name in $unfreeze[$relative]) {
        $pattern = "(?m)^(\s*)(?:private|internal|public)\s+const\s+(\w+)\s+$name\s*="
        if ($text -notmatch $pattern) {
            throw "could not find a constant named '$name' in $relative -- was it renamed?"
        }

        $text = [regex]::Replace($text, $pattern, "`${1}internal static `${2} $name =")
    }

    $target = Join-Path $obj (Split-Path $relative -Leaf)
    Set-Content -Path $target -Value $text -Encoding UTF8
    $patched += $target
}

# RayTrack.cs is used as-is; nothing in it is tuned.
$sources = @(
    (Join-Path $repo 'source\Services\Images\RayTrack.cs'),
    (Join-Path $here 'RayGlowTuner.cs'),
    (Join-Path $here 'TunerWindow.cs')
) + $patched

$refs = @(
    'mscorlib', 'System', 'System.Core', 'System.Xml',
    'WindowsBase', 'PresentationCore', 'PresentationFramework', 'System.Xaml',
    'System.Windows.Forms', 'System.Drawing'
) | ForEach-Object { "/r:`"$refDir\$_.dll`"" }

$exe = Join-Path $out 'RayGlowTuner.exe'
$arguments = @(
    '/nologo', '/target:winexe', '/langversion:latest', '/nostdlib+', '/optimize+',
    "/out:$exe"
) + $refs + ($sources | ForEach-Object { "`"$_`"" })

& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }

Write-Host "built $exe" -ForegroundColor Green
if ($args -notcontains '-NoRun') {
    & $exe
}
