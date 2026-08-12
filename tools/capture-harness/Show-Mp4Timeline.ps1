# Dumps MP4 track timing: mdhd duration per track and the stts (time-to-sample) table.
# Read-only. Use it to check whether a clip's video track carries real capture timing:
#   before the fix: video stts is always a single entry of count=N delta=nominal
#   after  the fix: video stts has many entries, and video/audio mdhd durations agree
#
#   .\Show-Mp4Timeline.ps1 'C:\path\to\clip.mp4'
#   Get-ChildItem seg_*.mp4 | ForEach-Object { .\Show-Mp4Timeline.ps1 $_.FullName }

param([Parameter(Mandatory = $true)][string[]]$Path)

$ErrorActionPreference = 'Stop'

function Get-U32([byte[]]$b, [int]$o) {
    [int64]([int64]$b[$o] * 16777216 + [int64]$b[$o + 1] * 65536 + [int64]$b[$o + 2] * 256 + [int64]$b[$o + 3])
}

function Get-Type([byte[]]$b, [int]$o) { [Text.Encoding]::ASCII.GetString($b, $o, 4) }

function Find-Box([byte[]]$b, [int64]$start, [int64]$end, [string]$type) {
    $o = [int64]$start
    while (($o + 8) -le $end) {
        $sz = Get-U32 $b $o; $t = Get-Type $b ($o + 4); $hdr = [int64]8
        if ($sz -eq 0) { $sz = ($end - $o) }
        if ($sz -eq 1) { $sz = [int64]0; for ($i = 0; $i -lt 8; $i++) { $sz = ($sz * 256 + $b[$o + 8 + $i]) }; $hdr = [int64]16 }
        if ($t -eq $type) { return (New-Object psobject -Property @{ S = ($o + $hdr); E = ($o + $sz) }) }
        $o = ($o + $sz)
    }
    return $null
}

function List-Boxes([byte[]]$b, [int64]$start, [int64]$end, [string]$type) {
    $res = New-Object System.Collections.ArrayList; $o = [int64]$start
    while (($o + 8) -le $end) {
        $sz = Get-U32 $b $o; $t = Get-Type $b ($o + 4)
        if ($sz -le 0) { break }
        if ($t -eq $type) { [void]$res.Add((New-Object psobject -Property @{ S = ($o + 8); E = ($o + $sz) })) }
        $o = ($o + $sz)
    }
    return $res
}

foreach ($file in $Path) {
    $b = [IO.File]::ReadAllBytes($file)
    Write-Output ("=== " + (Split-Path $file -Leaf) + "  bytes=" + $b.Length)

    $moov = Find-Box $b 0 $b.Length 'moov'
    if ($null -eq $moov) { Write-Output '  no moov box'; continue }

    $mvhd = Find-Box $b $moov.S $moov.E 'mvhd'
    $mvts = Get-U32 $b ($mvhd.S + 12)
    Write-Output ("  mvhd => " + [math]::Round((Get-U32 $b ($mvhd.S + 16)) / $mvts, 4) + " s")

    foreach ($tk in (List-Boxes $b $moov.S $moov.E 'trak')) {
        $mdia = Find-Box $b $tk.S $tk.E 'mdia'
        $mdhd = Find-Box $b $mdia.S $mdia.E 'mdhd'
        $ts = Get-U32 $b ($mdhd.S + 12); $dur = Get-U32 $b ($mdhd.S + 16)
        $kind = Get-Type $b ((Find-Box $b $mdia.S $mdia.E 'hdlr').S + 8)

        $minf = Find-Box $b $mdia.S $mdia.E 'minf'
        $stbl = Find-Box $b $minf.S $minf.E 'stbl'
        $stts = Find-Box $b $stbl.S $stbl.E 'stts'
        $n = [int](Get-U32 $b ($stts.S + 4))

        $samples = [int64]0; $tot = [int64]0; $lines = New-Object System.Collections.ArrayList
        for ($i = 0; $i -lt $n; $i++) {
            $c = Get-U32 $b ($stts.S + 8 + $i * 8)
            $d = Get-U32 $b ($stts.S + 12 + $i * 8)
            $samples = ($samples + $c); $tot = ($tot + $c * $d)
            if ($i -lt 12) { [void]$lines.Add("count=$c delta=$d (" + [math]::Round($d * 1000.0 / $ts, 3) + " ms)") }
        }

        Write-Output ("  [$kind] mdhd ts=$ts => " + [math]::Round($dur / $ts, 4) +
            " s | stts entries=$n samples=$samples sum=" + [math]::Round($tot / $ts, 4) + " s")
        foreach ($l in $lines) { Write-Output ("     " + $l) }
        if ($n -gt 12) { Write-Output ("     ... " + ($n - 12) + " more entries") }
    }
}
