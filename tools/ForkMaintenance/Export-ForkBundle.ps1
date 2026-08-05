[CmdletBinding()]
param(
    [string] $Baseline,
    [string] $RepositoryPath,
    [string] $BundlePath,
    [switch] $Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ForkMaintenance.Common.ps1")

$config = Get-FmConfig
$repositoryRoot = Get-FmRepositoryRoot $RepositoryPath
if ([string]::IsNullOrWhiteSpace($Baseline))
{
    $Baseline = [string]$config.defaultBaseline
}

$baselineResult = Invoke-FmGit $repositoryRoot @("rev-parse", "$Baseline^{commit}")
$baselineCommit = ([string]$baselineResult.Output[0]).Trim()

if ([string]::IsNullOrWhiteSpace($BundlePath))
{
    $BundlePath = Join-Path $PSScriptRoot ([string]$config.bundleDirectory)
}

$bundleFullPath = [IO.Path]::GetFullPath($BundlePath)
$maintenanceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
if (-not $bundleFullPath.StartsWith($maintenanceRoot, [StringComparison]::OrdinalIgnoreCase))
{
    throw "BundlePath must stay inside '$PSScriptRoot'."
}

if (Test-Path -LiteralPath $bundleFullPath)
{
    if (-not $Force)
    {
        throw "Bundle already exists. Use -Force to replace it: $bundleFullPath"
    }

    Remove-Item -LiteralPath $bundleFullPath -Recurse -Force
}

[IO.Directory]::CreateDirectory($bundleFullPath) | Out-Null
$overlayRoot = Join-Path $bundleFullPath "overlay"
[IO.Directory]::CreateDirectory($overlayRoot) | Out-Null

$protectedPaths = @($config.protectedPaths | ForEach-Object { ConvertTo-FmGitPath ([string]$_) })
$excludedPaths = @($config.excludedPaths | ForEach-Object { ConvertTo-FmGitPath ([string]$_) })
$allExcludedPrefixes = @($protectedPaths + $excludedPaths)

$excludePathspecs = @()
foreach ($path in $protectedPaths)
{
    $excludePathspecs += ":(exclude)$path"
}
foreach ($path in $excludedPaths)
{
    $excludePathspecs += ":(exclude)$path/**"
}
$localizationGlob = ConvertTo-FmGitPath ([string]$config.semanticLocalizationGlob)
$excludePathspecs += ":(exclude)$localizationGlob"

$addedArgs = @(
    "diff", "--name-only", "--diff-filter=A", $baselineCommit, "--", "."
) + $excludePathspecs
$addedFiles = @((Invoke-FmGit $repositoryRoot $addedArgs).Output |
    ForEach-Object { ConvertTo-FmGitPath ([string]$_) } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$untrackedFiles = @((Invoke-FmGit $repositoryRoot @(
    "ls-files", "--others", "--exclude-standard"
)).Output |
    ForEach-Object { ConvertTo-FmGitPath ([string]$_) } |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        -not (Test-FmPathMatchesPrefix $_ $allExcludedPrefixes) -and
        -not $_.StartsWith("source/Localization/", [StringComparison]::OrdinalIgnoreCase)
    })

# A no-commit upstream integration leaves upstream-added files untracked relative to the
# fork branch. They are not overlays when the selected baseline already owns the path.
$untrackedFiles = @($untrackedFiles | Where-Object {
    $baselinePath = "$baselineCommit`:$($_)"
    (Invoke-FmGit $repositoryRoot @("cat-file", "-e", $baselinePath) -AllowFailure).ExitCode -ne 0
})

$overlayFiles = @($addedFiles + $untrackedFiles | Sort-Object -Unique)
$overlayManifest = @()
foreach ($relativePath in $overlayFiles)
{
    $sourcePath = Join-Path $repositoryRoot ($relativePath.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
    {
        continue
    }

    $destinationPath = Join-Path $overlayRoot ($relativePath.Replace('/', '\'))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath)) | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
    $overlayManifest += [ordered]@{
        path = $relativePath
        sha256 = Get-FmSha256 $destinationPath
    }
}

$patchArgs = @(
    "diff",
    "--binary",
    "--full-index",
    "--find-renames",
    "--diff-filter=MDRTUXB",
    $baselineCommit,
    "--",
    "."
) + $excludePathspecs
$patchResult = Invoke-FmGit $repositoryRoot $patchArgs
$patchText = ($patchResult.Output -join "`n")
if (-not [string]::IsNullOrEmpty($patchText))
{
    $patchText += "`n"
}
$patchPath = Join-Path $bundleFullPath "fork-changes.patch"
Write-FmUtf8NoBom $patchPath $patchText

$localizationPaths = @((Invoke-FmGit $repositoryRoot @(
    "diff",
    "--name-only",
    $baselineCommit,
    "--",
    $localizationGlob
)).Output |
    ForEach-Object { ConvertTo-FmGitPath ([string]$_) } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$localizationRecipes = @()
foreach ($relativePath in $localizationPaths)
{
    $currentPath = Join-Path $repositoryRoot ($relativePath.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $currentPath -PathType Leaf))
    {
        continue
    }

    $baseResult = Invoke-FmGit $repositoryRoot @(
        "show", "$baselineCommit`:$relativePath"
    ) -AllowFailure
    $baseXml = ""
    if ($baseResult.ExitCode -eq 0)
    {
        $baseXml = ($baseResult.Output -join "`n")
    }

    $currentXml = Get-Content -LiteralPath $currentPath -Raw
    $baseElements = Get-FmKeyedXmlElements $baseXml
    $currentElements = Get-FmKeyedXmlElements $currentXml
    $entries = @()

    foreach ($key in @($currentElements.Keys | Sort-Object))
    {
        $baseValue = $null
        if ($baseElements.ContainsKey($key))
        {
            $baseValue = [string]$baseElements[$key]
        }

        $desiredValue = [string]$currentElements[$key]
        if ($baseValue -ne $desiredValue)
        {
            $entries += [ordered]@{
                key = $key
                baseXml = $baseValue
                desiredXml = $desiredValue
            }
        }
    }

    $removedKeys = @($baseElements.Keys |
        Where-Object { -not $currentElements.ContainsKey($_) } |
        Sort-Object)

    if ($entries.Count -gt 0 -or $removedKeys.Count -gt 0)
    {
        $localizationRecipes += [ordered]@{
            path = $relativePath
            entries = $entries
            removedKeys = $removedKeys
        }
    }
}

$localizationPath = Join-Path $bundleFullPath "localization-recipes.json"
$localizationJson = ConvertTo-Json $localizationRecipes -Depth 8
Write-FmUtf8NoBom $localizationPath ($localizationJson + "`n")

$bundleManifest = [ordered]@{
    schemaVersion = 1
    generatedUtc = [DateTime]::UtcNow.ToString("o")
    baselineRef = $Baseline
    baselineCommit = $baselineCommit
    protectedPaths = $protectedPaths
    patch = [ordered]@{
        path = "fork-changes.patch"
        sha256 = Get-FmSha256 $patchPath
    }
    localizationRecipes = [ordered]@{
        path = "localization-recipes.json"
        sha256 = Get-FmSha256 $localizationPath
        fileCount = $localizationRecipes.Count
        entryCount = @($localizationRecipes | ForEach-Object { @($_.entries).Count } |
            Measure-Object -Sum).Sum
    }
    overlayFiles = $overlayManifest
}

$manifestPath = Join-Path $bundleFullPath "bundle.json"
Write-FmUtf8NoBom $manifestPath ((ConvertTo-Json $bundleManifest -Depth 8) + "`n")

Write-Host "Fork bundle exported successfully."
Write-Host "  Baseline:             $Baseline ($baselineCommit)"
Write-Host "  Three-way patch:      $patchPath"
Write-Host "  Fork-only overlays:   $($overlayManifest.Count)"
Write-Host "  Localization files:   $($localizationRecipes.Count)"
Write-Host "  Bundle manifest:      $manifestPath"
