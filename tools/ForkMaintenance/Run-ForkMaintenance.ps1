[CmdletBinding()]
param(
    [ValidateSet("Test", "Export", "Apply")]
    [string] $Action = "Test",
    [string] $RepositoryPath,
    [string] $BundlePath,
    [string] $Baseline = "v3.1.0",
    [switch] $AllowDirty,
    [switch] $ResumeAfterConflict,
    [switch] $Force,
    [switch] $ForceSemantic,
    [switch] $ForceOverlay,
    [switch] $NoPause
)

$ErrorActionPreference = "Stop"
$exitCode = 0
$transcriptPath = Join-Path $env:TEMP "PlayniteAchievements-ForkMaintenance-last-run.log"

function Write-RunHeader
{
    param([string] $Text)

    Write-Host ""
    Write-Host ("=" * 72) -ForegroundColor DarkGray
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ("=" * 72) -ForegroundColor DarkGray
}

try
{
    try
    {
        Stop-Transcript | Out-Null
    }
    catch
    {
    }

    Start-Transcript -LiteralPath $transcriptPath -Force | Out-Null
    Write-RunHeader "ForkMaintenance: $Action"
    Write-Host "Started:    $([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))"
    Write-Host "Transcript: $transcriptPath"

    switch ($Action)
    {
        "Test"
        {
            $arguments = @{}
            if (-not [string]::IsNullOrWhiteSpace($RepositoryPath)) { $arguments.RepositoryPath = $RepositoryPath }
            if (-not [string]::IsNullOrWhiteSpace($BundlePath)) { $arguments.BundlePath = $BundlePath }
            if ($AllowDirty) { $arguments.AllowDirty = $true }
            if ($ForceSemantic) { $arguments.ForceSemantic = $true }
            if ($ForceOverlay) { $arguments.ForceOverlay = $true }

            Write-Host "Running bundle validation..." -ForegroundColor Yellow
            & (Join-Path $PSScriptRoot "Test-ForkBundle.ps1") @arguments
        }
        "Export"
        {
            $arguments = @{ Baseline = $Baseline }
            if (-not [string]::IsNullOrWhiteSpace($RepositoryPath)) { $arguments.RepositoryPath = $RepositoryPath }
            if (-not [string]::IsNullOrWhiteSpace($BundlePath)) { $arguments.BundlePath = $BundlePath }
            if ($Force) { $arguments.Force = $true }

            Write-Host "Exporting fork bundle against $Baseline..." -ForegroundColor Yellow
            & (Join-Path $PSScriptRoot "Export-ForkBundle.ps1") @arguments
        }
        "Apply"
        {
            $arguments = @{}
            if (-not [string]::IsNullOrWhiteSpace($RepositoryPath)) { $arguments.RepositoryPath = $RepositoryPath }
            if (-not [string]::IsNullOrWhiteSpace($BundlePath)) { $arguments.BundlePath = $BundlePath }
            if ($AllowDirty) { $arguments.AllowDirty = $true }
            if ($ResumeAfterConflict) { $arguments.ResumeAfterConflict = $true }
            if ($ForceSemantic) { $arguments.ForceSemantic = $true }
            if ($ForceOverlay) { $arguments.ForceOverlay = $true }

            Write-Host "Applying fork bundle..." -ForegroundColor Yellow
            & (Join-Path $PSScriptRoot "Apply-ForkBundle.ps1") @arguments
        }
    }

    if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0)
    {
        throw "ForkMaintenance exited with code $LASTEXITCODE."
    }

    Write-RunHeader "ForkMaintenance completed successfully"
}
catch
{
    $exitCode = 1
    Write-RunHeader "ForkMaintenance failed"
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkRed
}
finally
{
    Write-Host "Finished:   $([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))"
    Write-Host "Transcript: $transcriptPath"
    try
    {
        Stop-Transcript | Out-Null
    }
    catch
    {
    }

    if (-not $NoPause)
    {
        Write-Host ""
        Write-Host "Press any key to close this window..." -ForegroundColor Green
        [void][Console]::ReadKey($true)
    }
}

exit $exitCode
