[CmdletBinding()]
param(
    [string] $RepositoryPath,
    [string] $BundlePath,
    [switch] $AllowDirty,
    [switch] $ForceSemantic,
    [switch] $ForceOverlay
)

$ErrorActionPreference = "Stop"
$arguments = @{
    DryRun = $true
    AllowDirty = $AllowDirty
    ForceSemantic = $ForceSemantic
    ForceOverlay = $ForceOverlay
}
if (-not [string]::IsNullOrWhiteSpace($RepositoryPath))
{
    $arguments.RepositoryPath = $RepositoryPath
}
if (-not [string]::IsNullOrWhiteSpace($BundlePath))
{
    $arguments.BundlePath = $BundlePath
}

& (Join-Path $PSScriptRoot "Apply-ForkBundle.ps1") @arguments
