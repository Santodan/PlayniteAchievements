[CmdletBinding()]
param(
    [string] $RepositoryPath,
    [string] $BundlePath,
    [string] $UpstreamRef = "upstream/main",
    [string] $UpstreamRemote = "upstream",
    [switch] $SkipFetch,
    [switch] $ResumeAfterConflict,
    [switch] $ForceSemantic,
    [switch] $ForceOverlay
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ForkMaintenance.Common.ps1")

$config = Get-FmConfig
$repositoryRoot = Get-FmRepositoryRoot $RepositoryPath
$protectedPaths = @($config.protectedPaths | ForEach-Object { ConvertTo-FmGitPath ([string]$_) })
$maintenancePath = "tools/ForkMaintenance"

$existingMerge = Invoke-FmGit $repositoryRoot @(
    "rev-parse", "--verify", "-q", "MERGE_HEAD"
) -AllowFailure
if (-not $ResumeAfterConflict -and $existingMerge.ExitCode -eq 0)
{
    throw "In-place update refused because another merge is already in progress. Finish or abort it first."
}
if ($ResumeAfterConflict -and $existingMerge.ExitCode -ne 0)
{
    throw "There is no ForkMaintenance merge to resume. Run Update without -ResumeAfterConflict first."
}

# An in-place update deliberately replaces the tracked fork implementation with the
# selected upstream tree before applying the saved fork layer. Only protected files and
# the maintenance bundle itself may already be dirty; anything else could be user work.
if (-not $ResumeAfterConflict)
{
    $unexpectedPaths = @()
    $statusLines = @((Invoke-FmGit $repositoryRoot @(
        "status", "--porcelain=v1", "--untracked-files=all"
    )).Output)
    foreach ($line in $statusLines)
    {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4)
        {
            continue
        }

        $path = $line.Substring(3)
        if ($path.Contains(" -> "))
        {
            $path = $path.Substring($path.IndexOf(" -> ") + 4)
        }
        $path = (ConvertTo-FmGitPath $path.Trim('"'))

        if (-not (Test-FmPathMatchesPrefix $path ($protectedPaths + @($maintenancePath))))
        {
            $unexpectedPaths += $path
        }
    }

    if ($unexpectedPaths.Count -gt 0)
    {
        throw "In-place update refused because non-protected local changes exist:`n$($unexpectedPaths -join "`n")"
    }
}

$protectedHashes = Get-FmProtectedHashes $repositoryRoot $protectedPaths

if (-not $ResumeAfterConflict -and -not $SkipFetch)
{
    Write-Host "Fetching $UpstreamRemote..." -ForegroundColor Yellow
    Invoke-FmGit $repositoryRoot @("fetch", "--prune", $UpstreamRemote) | Out-Null
}

$resolvedUpstream = @((Invoke-FmGit $repositoryRoot @(
    "rev-parse", "--verify", "$UpstreamRef^{commit}"
)).Output) | Select-Object -First 1
if (-not $ResumeAfterConflict)
{
    $ancestry = Invoke-FmGit $repositoryRoot @(
        "merge-base", "--is-ancestor", $resolvedUpstream, "HEAD"
    ) -AllowFailure
    if ($ancestry.ExitCode -notin @(0, 1))
    {
        throw "Unable to determine whether $UpstreamRef is already an ancestor of HEAD."
    }

    if ($ancestry.ExitCode -eq 1)
    {
        # Record upstream as a real second parent without asking Git to merge the
        # fork's divergent files. The desired tree is assembled below from the
        # upstream snapshot plus the exported fork layer. MERGE_HEAD remains in
        # place so the user's reviewed commit is a true upstream merge commit.
        Write-Host "Recording $UpstreamRef as the pending merge parent..." -ForegroundColor Yellow
        Invoke-FmGit $repositoryRoot @(
            "merge", "--no-commit", "--no-ff", "--strategy=ours", $resolvedUpstream
        ) | Out-Null
    }
    else
    {
        Write-Host "$UpstreamRef is already in this branch's ancestry." -ForegroundColor DarkGray
    }

    Write-Host "Updating the current checkout in place from $UpstreamRef ($($resolvedUpstream.Substring(0, 8)))..." -ForegroundColor Yellow
    $restoreArguments = @(
        "restore",
        "--source=$UpstreamRef",
        "--staged",
        "--worktree",
        "--",
        "."
    )
    foreach ($path in $protectedPaths + @($maintenancePath))
    {
        $restoreArguments += ":(exclude)$path"
        $restoreArguments += ":(exclude)$path/**"
    }
    Invoke-FmGit $repositoryRoot $restoreArguments | Out-Null
    Assert-FmProtectedHashes $repositoryRoot $protectedPaths $protectedHashes
}
else
{
    Write-Host "Resuming the in-place update after conflict resolution..." -ForegroundColor Yellow
}

$applyArguments = @{ RepositoryPath = $repositoryRoot }
if ($ResumeAfterConflict) { $applyArguments.ResumeAfterConflict = $true }
else { $applyArguments.AllowDirty = $true }
if (-not [string]::IsNullOrWhiteSpace($BundlePath)) { $applyArguments.BundlePath = $BundlePath }
if ($ForceSemantic) { $applyArguments.ForceSemantic = $true }
if ($ForceOverlay) { $applyArguments.ForceOverlay = $true }

& (Join-Path $PSScriptRoot "Apply-ForkBundle.ps1") @applyArguments

Assert-FmProtectedHashes $repositoryRoot $protectedPaths $protectedHashes
# Keep the Source Control panel straightforward: files should appear once as ordinary
# working-tree changes, not as staged upstream deletions plus untracked overlay copies.
Invoke-FmGit $repositoryRoot @("restore", "--staged", "--", ".") | Out-Null
Write-Host "In-place fork update completed successfully."
Write-Host "  Upstream base:         $UpstreamRef ($resolvedUpstream)"
Write-Host "  Updated checkout:      $repositoryRoot"
Write-Host "  Protected files kept:  $($protectedPaths.Count)"
$pendingMerge = Invoke-FmGit $repositoryRoot @(
    "rev-parse", "--verify", "-q", "MERGE_HEAD"
) -AllowFailure
if ($pendingMerge.ExitCode -eq 0)
{
    Write-Host "  Pending merge parent:  $($pendingMerge.Output | Select-Object -First 1)"
    Write-Host "Stage and commit the reviewed files normally; Git will retain the upstream ancestry."
}
