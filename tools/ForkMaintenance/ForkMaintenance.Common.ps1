Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-FmConfig
{
    $configPath = Join-Path $PSScriptRoot "fork-maintenance.json"
    if (-not (Test-Path -LiteralPath $configPath))
    {
        throw "Fork maintenance configuration was not found: $configPath"
    }

    return Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
}

function Get-FmRepositoryRoot
{
    param([string] $RepositoryPath)

    $candidate = $RepositoryPath
    if ([string]::IsNullOrWhiteSpace($candidate))
    {
        $candidate = $PSScriptRoot
    }

    $root = (& git -C $candidate rev-parse --show-toplevel 2>&1 | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root))
    {
        throw "Unable to resolve a Git repository from '$candidate'."
    }

    return [IO.Path]::GetFullPath([string]$root)
}

function Invoke-FmGit
{
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [switch] $AllowFailure
    )

    # Git writes successful three-way apply diagnostics to stderr. Temporarily
    # keep native stderr from becoming a PowerShell terminating error and use
    # Git's exit code as the source of truth.
    $previousErrorActionPreference = $ErrorActionPreference
    try
    {
        $ErrorActionPreference = "Continue"
        $result = @(& git -C $RepositoryRoot @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0 -and -not $AllowFailure)
    {
        $message = ($result -join [Environment]::NewLine)
        throw "git $($Arguments -join ' ') failed with exit code $exitCode.`n$message"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $result
    }
}

function ConvertTo-FmGitPath
{
    param([Parameter(Mandatory = $true)][string] $Path)
    return $Path.Replace('\', '/').TrimStart('/')
}

function Test-FmPathMatchesPrefix
{
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][string[]] $Prefixes
    )

    $normalized = ConvertTo-FmGitPath $Path
    foreach ($prefixValue in $Prefixes)
    {
        $prefix = (ConvertTo-FmGitPath $prefixValue).TrimEnd('/')
        if ($normalized.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            $normalized.StartsWith("$prefix/", [StringComparison]::OrdinalIgnoreCase))
        {
            return $true
        }
    }

    return $false
}

function Get-FmSha256
{
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        return $null
    }

    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Write-FmUtf8NoBom
{
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [AllowEmptyString()][string] $Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent))
    {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    [IO.File]::WriteAllText(
        $Path,
        $Content,
        [Text.UTF8Encoding]::new($false))
}

function Get-FmProtectedHashes
{
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]] $ProtectedPaths
    )

    $hashes = @{}
    foreach ($path in $ProtectedPaths)
    {
        $fullPath = Join-Path $RepositoryRoot ($path.Replace('/', '\'))
        $hashes[$path] = Get-FmSha256 $fullPath
    }

    return $hashes
}

function Assert-FmProtectedHashes
{
    param(
        [Parameter(Mandatory = $true)][string] $RepositoryRoot,
        [Parameter(Mandatory = $true)][string[]] $ProtectedPaths,
        [Parameter(Mandatory = $true)][hashtable] $ExpectedHashes
    )

    foreach ($path in $ProtectedPaths)
    {
        $actual = Get-FmSha256 (Join-Path $RepositoryRoot ($path.Replace('/', '\')))
        if ($actual -ne $ExpectedHashes[$path])
        {
            throw "Protected file changed unexpectedly: $path"
        }
    }
}

function Get-FmKeyedXmlElements
{
    param([AllowEmptyString()][string] $Xml)

    $elements = @{}
    if ([string]::IsNullOrWhiteSpace($Xml))
    {
        return $elements
    }

    $document = [Xml.XmlDocument]::new()
    $document.PreserveWhitespace = $true
    $document.LoadXml($Xml)

    foreach ($node in $document.DocumentElement.ChildNodes)
    {
        if ($node.NodeType -ne [Xml.XmlNodeType]::Element)
        {
            continue
        }

        $keyAttribute = $node.Attributes |
            Where-Object { $_.LocalName -eq "Key" } |
            Select-Object -First 1
        if ($null -ne $keyAttribute -and -not [string]::IsNullOrWhiteSpace($keyAttribute.Value))
        {
            $elements[$keyAttribute.Value] = $node.OuterXml
        }
    }

    return $elements
}

function Get-FmXmlKey
{
    param([Parameter(Mandatory = $true)][Xml.XmlNode] $Node)

    $attribute = $Node.Attributes |
        Where-Object { $_.LocalName -eq "Key" } |
        Select-Object -First 1
    if ($null -eq $attribute)
    {
        return $null
    }

    return $attribute.Value
}

function Get-FmRelativePath
{
    param(
        [Parameter(Mandatory = $true)][string] $BasePath,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $baseUri = [Uri]((([IO.Path]::GetFullPath($BasePath)).TrimEnd('\') + '\'))
    $pathUri = [Uri][IO.Path]::GetFullPath($Path)
    return [Uri]::UnescapeDataString(
        $baseUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}
