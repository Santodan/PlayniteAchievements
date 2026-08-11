# Finds which frame of a clip matches the live screenshot taken for the same unlock.
#
# The screenshot is grabbed at a known real instant (just after detection), so the matching frame's
# position in the clip pins how clip time maps to wall clock — which is what tells us whether the
# composited card sits on the right moment.
param(
    [Parameter(Mandatory = $true)][string]$Clip,
    [Parameter(Mandatory = $true)][string]$Shot,
    [double]$From = 0.0,
    [double]$To = 20.0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sp = Split-Path $PSCommandPath

# build.ps1 puts the executables in bin\; a shared bundle is just that folder, with this script dropped in.
$dump = @("$sp\bin\FrameDump.exe", "$sp\FrameDump.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $dump) { throw "no FrameDump.exe in $sp\bin or beside this script - run build.ps1" }

$dir = Join-Path $sp ('match_' + [IO.Path]::GetFileNameWithoutExtension($Clip).Replace(' ', '_'))
if (-not (Test-Path $dir)) {
    & $dump $Clip $dir $From $To 320 > $null 2>&1
}

$frames = Get-ChildItem $dir -Filter *.png | Sort-Object Name
Write-Output ("frames to search: " + $frames.Count)

# Reduce both the shot and each frame to the same small grid, then compare.
function Signature([string]$path) {
    $src = [System.Drawing.Bitmap]::FromFile($path)
    try {
        $small = New-Object System.Drawing.Bitmap 40, 24
        try {
            $g = [System.Drawing.Graphics]::FromImage($small)
            try {
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBilinear
                $g.DrawImage($src, 0, 0, 40, 24)
            } finally { $g.Dispose() }

            $values = New-Object 'double[]' 960
            $i = 0
            for ($y = 0; $y -lt 24; $y++) {
                for ($x = 0; $x -lt 40; $x++) {
                    $c = $small.GetPixel($x, $y)
                    $values[$i] = 0.299 * $c.R + 0.587 * $c.G + 0.114 * $c.B
                    $i++
                }
            }

            return $values
        } finally { $small.Dispose() }
    } finally { $src.Dispose() }
}

$target = Signature $Shot
$best = [double]::MaxValue
$bestName = ''
$results = @()
foreach ($f in $frames) {
    $sig = Signature $f.FullName
    $sum = 0.0
    for ($i = 0; $i -lt $sig.Length; $i++) { $d = $sig[$i] - $target[$i]; $sum += $d * $d }
    $rms = [math]::Sqrt($sum / $sig.Length)
    $results += [pscustomobject]@{ Name = $f.BaseName; Rms = $rms }
    if ($rms -lt $best) { $best = $rms; $bestName = $f.BaseName }
}

Write-Output ""
Write-Output "closest frames to the screenshot (lower = more alike):"
$results | Sort-Object Rms | Select-Object -First 8 | ForEach-Object {
    Write-Output ("  " + $_.Name + "   rms " + $_.Rms.ToString('0.00'))
}
Write-Output ""
Write-Output ("BEST MATCH: " + $bestName + " (rms " + $best.ToString('0.00') + ")")
