[CmdletBinding()]
param(
    [string] $RepositoryPath,
    [string] $BundlePath,
    [switch] $DryRun,
    [switch] $AllowDirty,
    [switch] $ResumeAfterConflict,
    [switch] $ForceSemantic,
    [switch] $ForceOverlay
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ForkMaintenance.Common.ps1")

$config = Get-FmConfig
$repositoryRoot = Get-FmRepositoryRoot $RepositoryPath
if ([string]::IsNullOrWhiteSpace($BundlePath))
{
    $BundlePath = Join-Path $PSScriptRoot ([string]$config.bundleDirectory)
}

$bundleFullPath = [IO.Path]::GetFullPath($BundlePath)
$manifestPath = Join-Path $bundleFullPath "bundle.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf))
{
    throw "Fork bundle manifest was not found: $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1)
{
    throw "Unsupported fork bundle schema: $($manifest.schemaVersion)"
}

$protectedPaths = @($manifest.protectedPaths | ForEach-Object { [string]$_ })
$protectedHashes = Get-FmProtectedHashes $repositoryRoot $protectedPaths

if (-not $AllowDirty -and -not $ResumeAfterConflict)
{
    $status = @((Invoke-FmGit $repositoryRoot @("status", "--porcelain")).Output)
    if ($status.Count -gt 0)
    {
        throw "Target repository is not clean. Apply to a fresh upstream worktree or use -AllowDirty deliberately."
    }
}

if ($ResumeAfterConflict)
{
    $unmerged = @((Invoke-FmGit $repositoryRoot @(
        "diff", "--name-only", "--diff-filter=U"
    ) -AllowFailure).Output)
    if ($unmerged.Count -gt 0)
    {
        throw "Cannot resume while merge conflicts remain:`n$($unmerged -join "`n")"
    }
}

$patchPath = Join-Path $bundleFullPath ([string]$manifest.patch.path)
$localizationPath = Join-Path $bundleFullPath ([string]$manifest.localizationRecipes.path)
if ((Get-FmSha256 $patchPath) -ne [string]$manifest.patch.sha256)
{
    throw "Fork patch hash does not match bundle.json."
}
if ((Get-FmSha256 $localizationPath) -ne [string]$manifest.localizationRecipes.sha256)
{
    throw "Localization recipe hash does not match bundle.json."
}

$patchText = Get-Content -LiteralPath $patchPath -Raw
foreach ($protectedPath in $protectedPaths)
{
    $escaped = [Regex]::Escape((ConvertTo-FmGitPath $protectedPath))
    if ($patchText -match "(?m)^(---|\+\+\+) [ab]/$escaped$")
    {
        throw "Bundle patch illegally contains protected file: $protectedPath"
    }
}

$overlayRoot = Join-Path $bundleFullPath "overlay"
$overlayOperations = @()
foreach ($entry in @($manifest.overlayFiles))
{
    $relativePath = ConvertTo-FmGitPath ([string]$entry.path)
    if (Test-FmPathMatchesPrefix $relativePath $protectedPaths)
    {
        throw "Bundle overlay illegally contains protected file: $relativePath"
    }

    $sourcePath = Join-Path $overlayRoot ($relativePath.Replace('/', '\'))
    if ((Get-FmSha256 $sourcePath) -ne [string]$entry.sha256)
    {
        throw "Overlay hash does not match bundle.json: $relativePath"
    }

    $destinationPath = Join-Path $repositoryRoot ($relativePath.Replace('/', '\'))
    if (Test-Path -LiteralPath $destinationPath -PathType Leaf)
    {
        $destinationHash = Get-FmSha256 $destinationPath
        if ($destinationHash -ne [string]$entry.sha256 -and -not $ForceOverlay)
        {
            throw "Overlay destination already exists with different content: $relativePath. Resolve it or use -ForceOverlay."
        }
    }

    $overlayOperations += [pscustomobject]@{
        RelativePath = $relativePath
        SourcePath = $sourcePath
        DestinationPath = $destinationPath
    }
}

$localizationRecipes = @()
if (Test-Path -LiteralPath $localizationPath -PathType Leaf)
{
    # Windows PowerShell 5.1 returns a top-level JSON array as one Object[]
    # pipeline object. Assign it directly so foreach enumerates its entries.
    $localizationRecipes =
        Get-Content -LiteralPath $localizationPath -Raw |
        ConvertFrom-Json
}

$localizationOperations = @()
foreach ($recipe in $localizationRecipes)
{
    $relativePath = ConvertTo-FmGitPath ([string]$recipe.path)
    $targetPath = Join-Path $repositoryRoot ($relativePath.Replace('/', '\'))
    if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf))
    {
        throw "Localization target does not exist: $relativePath"
    }

    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.Load($targetPath)
    $existingNodes = @{}
    foreach ($node in $document.DocumentElement.ChildNodes)
    {
        if ($node.NodeType -ne [Xml.XmlNodeType]::Element)
        {
            continue
        }

        $key = Get-FmXmlKey $node
        if (-not [string]::IsNullOrWhiteSpace($key))
        {
            $existingNodes[$key] = $node
        }
    }

    foreach ($entry in @($recipe.entries))
    {
        $key = [string]$entry.key
        $existingXml = $null
        if ($existingNodes.ContainsKey($key))
        {
            $existingXml = [string]$existingNodes[$key].OuterXml
        }

        $baseXml = if ($null -eq $entry.baseXml) { $null } else { [string]$entry.baseXml }
        $desiredXml = [string]$entry.desiredXml
        $safeToApply = ($existingXml -eq $desiredXml) -or ($existingXml -eq $baseXml)
        if (-not $safeToApply -and -not $ForceSemantic)
        {
            throw "Localization key changed independently upstream: $relativePath :: $key. Resolve it or use -ForceSemantic."
        }
    }

    $localizationOperations += [pscustomobject]@{
        Recipe = $recipe
        TargetPath = $targetPath
        Document = $document
        ExistingNodes = $existingNodes
    }
}

if (-not $ResumeAfterConflict -and -not [string]::IsNullOrWhiteSpace($patchText))
{
    $checkResult = Invoke-FmGit $repositoryRoot @(
        "apply", "--check", "--3way", "--whitespace=nowarn", $patchPath
    ) -AllowFailure
    if ($checkResult.ExitCode -ne 0)
    {
        $details = ($checkResult.Output -join [Environment]::NewLine)
        throw "Three-way patch preflight failed.`n$details"
    }
}

if ($DryRun)
{
    Assert-FmProtectedHashes $repositoryRoot $protectedPaths $protectedHashes
    Write-Host "Fork bundle dry-run passed."
    Write-Host "  Patch:                three-way preflight clean"
    Write-Host "  Fork-only overlays:   $($overlayOperations.Count)"
    Write-Host "  Localization files:   $($localizationOperations.Count)"
    return
}

try
{
    if (-not $ResumeAfterConflict)
    {
        Invoke-FmGit $repositoryRoot @("config", "rerere.enabled", "true") | Out-Null
        Invoke-FmGit $repositoryRoot @("config", "merge.conflictStyle", "zdiff3") | Out-Null
    }

    if (-not $ResumeAfterConflict -and -not [string]::IsNullOrWhiteSpace($patchText))
    {
        $applyResult = Invoke-FmGit $repositoryRoot @(
            "apply", "--3way", "--whitespace=nowarn", $patchPath
        ) -AllowFailure
        if ($applyResult.ExitCode -ne 0)
        {
            $conflicts = @((Invoke-FmGit $repositoryRoot @(
                "diff", "--name-only", "--diff-filter=U"
            ) -AllowFailure).Output)
            $details = ($applyResult.Output -join [Environment]::NewLine)
            throw "Three-way patch left conflicts. Resolve these files and rerun validation:`n$($conflicts -join "`n")`n$details"
        }
    }

    foreach ($operation in $localizationOperations)
    {
        $recipe = $operation.Recipe
        $document = $operation.Document
        $existingNodes = $operation.ExistingNodes

        foreach ($entry in @($recipe.entries))
        {
            $key = [string]$entry.key
            $fragment = $document.CreateDocumentFragment()
            $fragment.InnerXml = [string]$entry.desiredXml
            $desiredNode = $fragment.FirstChild
            if ($existingNodes.ContainsKey($key))
            {
                $replacement = $document.ImportNode($desiredNode, $true)
                $document.DocumentElement.ReplaceChild(
                    $replacement,
                    $existingNodes[$key]) | Out-Null
                $existingNodes[$key] = $replacement
            }
            else
            {
                $document.DocumentElement.AppendChild(
                    $document.ImportNode($desiredNode, $true)) | Out-Null
            }
        }

        foreach ($keyValue in @($recipe.removedKeys))
        {
            $key = [string]$keyValue
            if ($existingNodes.ContainsKey($key))
            {
                $document.DocumentElement.RemoveChild($existingNodes[$key]) | Out-Null
            }
        }

        $settings = [Xml.XmlWriterSettings]::new()
        $settings.Encoding = [Text.UTF8Encoding]::new($false)
        $settings.Indent = $false
        $settings.NewLineHandling = [Xml.NewLineHandling]::None
        $writer = [Xml.XmlWriter]::Create($operation.TargetPath, $settings)
        try
        {
            $document.Save($writer)
        }
        finally
        {
            $writer.Dispose()
        }
    }

    foreach ($operation in $overlayOperations)
    {
        [IO.Directory]::CreateDirectory(
            (Split-Path -Parent $operation.DestinationPath)) | Out-Null
        Copy-Item -LiteralPath $operation.SourcePath `
            -Destination $operation.DestinationPath `
            -Force:$ForceOverlay
    }
}
finally
{
    Assert-FmProtectedHashes $repositoryRoot $protectedPaths $protectedHashes
}

Write-Host "Fork bundle applied successfully."
if ($ResumeAfterConflict)
{
    Write-Host "  Three-way patch: resumed after conflicts were resolved."
}
else
{
    Write-Host "  Three-way patch applied with git rerere enabled."
}
Write-Host "  Fork-only overlays copied: $($overlayOperations.Count)"
Write-Host "  Localization dictionaries updated semantically: $($localizationOperations.Count)"
