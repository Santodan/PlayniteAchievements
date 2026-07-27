[CmdletBinding()]
param(
    [string] $PluginPath,
    [string] $CecilPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
if ([string]::IsNullOrWhiteSpace($PluginPath))
{
    $PluginPath = Join-Path $repositoryRoot "source\Resources\StartPage\StartPagePlugin.dll"
}
if ([string]::IsNullOrWhiteSpace($CecilPath))
{
    $CecilPath = Join-Path $repositoryRoot `
        "source\packages\microsoft.codecoverage\17.11.1\build\netstandard2.0\Mono.Cecil.dll"
}

$PluginPath = [IO.Path]::GetFullPath($PluginPath)
$CecilPath = [IO.Path]::GetFullPath($CecilPath)
if (-not (Test-Path -LiteralPath $PluginPath -PathType Leaf))
{
    throw "StartPage plugin was not found: $PluginPath"
}
if (-not (Test-Path -LiteralPath $CecilPath -PathType Leaf))
{
    throw "Mono.Cecil was not found: $CecilPath"
}

Add-Type -Path $CecilPath

function Get-AllTypes
{
    param([Mono.Cecil.TypeDefinition[]] $Types)

    foreach ($type in $Types)
    {
        $type
        if ($type.HasNestedTypes)
        {
            Get-AllTypes $type.NestedTypes
        }
    }
}

function Get-ProviderMethod
{
    param([Mono.Cecil.AssemblyDefinition] $Assembly)

    $provider = Get-AllTypes $Assembly.MainModule.Types |
        Where-Object { $_.FullName -eq "LandingPage.PlayniteAchievementsProvider" } |
        Select-Object -First 1
    if ($null -eq $provider)
    {
        throw "The PlayniteAchievements StartPage provider was not found."
    }

    $method = $provider.Methods |
        Where-Object {
            $_.Name -eq "GetLatestAchievements" -and
            $_.Parameters.Count -eq 3 -and
            $_.Parameters[0].ParameterType.FullName -eq "System.Int32" -and
            $_.Parameters[1].ParameterType.FullName -eq "System.Int32"
        } |
        Select-Object -First 1
    if ($null -eq $method -or -not $method.HasBody)
    {
        throw "GetLatestAchievements(int, int, CancellationToken) was not found."
    }

    return $method
}

function Test-PerGameLimit
{
    param([Mono.Cecil.MethodDefinition] $Method)

    $instructions = @($Method.Body.Instructions)
    for ($index = 0; $index -lt ($instructions.Count - 1); $index++)
    {
        if ($instructions[$index].OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldarg_2 -and
            $instructions[$index + 1].OpCode.Code -eq [Mono.Cecil.Cil.Code]::Stfld -and
            $null -ne $instructions[$index + 1].Operand -and
            $instructions[$index + 1].Operand.Name -eq "maxAchievementsPerGame")
        {
            return $true
        }
    }

    return $false
}

function Test-GameGrouping
{
    param([Mono.Cecil.AssemblyDefinition] $Assembly)

    $viewModel = Get-AllTypes $Assembly.MainModule.Types |
        Where-Object {
            $_.FullName -eq
                "LandingPage.ViewModels.PlayniteAchievements.PlayniteAchievementsViewModel"
        } |
        Select-Object -First 1
    if ($null -eq $viewModel)
    {
        throw "The PlayniteAchievements StartPage view model was not found."
    }

    $constructor = $viewModel.Methods |
        Where-Object { $_.Name -eq ".ctor" -and $_.HasBody } |
        Select-Object -First 1
    if ($null -eq $constructor)
    {
        throw "The PlayniteAchievements StartPage view-model constructor was not found."
    }

    $strings = @($constructor.Body.Instructions |
        Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr } |
        ForEach-Object { [string]$_.Operand })
    return $strings.Contains("GameInfo") -and $strings.Contains("UnlockedOn")
}

function Get-RecentQueryInstruction
{
    param([Mono.Cecil.AssemblyDefinition] $Assembly)

    $stateMachine = Get-AllTypes $Assembly.MainModule.Types |
        Where-Object {
            $_.FullName -eq
                "LandingPage.PlayniteAchievementsProvider/<GetLatestAchievements>d__14"
        } |
        Select-Object -First 1
    if ($null -eq $stateMachine)
    {
        throw "The GetLatestAchievements state machine was not found."
    }

    $moveNext = $stateMachine.Methods |
        Where-Object { $_.Name -eq "MoveNext" -and $_.HasBody } |
        Select-Object -First 1
    if ($null -eq $moveNext)
    {
        throw "The GetLatestAchievements state-machine body was not found."
    }

    return $moveNext.Body.Instructions |
        Where-Object {
            $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and
            ([string]$_.Operand).Contains("FROM UserAchievements ua") -and
            ([string]$_.Operand).Contains("ORDER BY")
        } |
        Select-Object -First 1
}

$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($PluginPath)
$temporaryPath = "$PluginPath.fork-patched"
try
{
    $method = Get-ProviderMethod $assembly
    if (-not (Test-PerGameLimit $method))
    {
        throw "The StartPage maximum-per-game setting is not wired to the provider."
    }
    if (-not (Test-GameGrouping $assembly))
    {
        throw "The StartPage game grouping or unlock-date sorting is missing."
    }

    $queryInstruction = Get-RecentQueryInstruction $assembly
    if ($null -eq $queryInstruction)
    {
        throw "The StartPage Recent Achievements query was not found."
    }

    $query = [string]$queryInstruction.Operand
    $unsafeExpression = "COALESCE(ua.UnlockTimeUtc, ugp.LastUpdatedUtc)"
    if ($query.Contains($unsafeExpression))
    {
        $query = $query.Replace(
            "$unsafeExpression AS UnlockTimeUtc",
            "ua.UnlockTimeUtc AS UnlockTimeUtc")
        $query = $query.Replace(
            "WHERE ua.Unlocked = 1",
            "WHERE ua.Unlocked = 1`r`n  AND ua.UnlockTimeUtc IS NOT NULL`r`n  AND trim(ua.UnlockTimeUtc) != ''")
        $query = $query.Replace(
            "ORDER BY $unsafeExpression DESC",
            "ORDER BY ua.UnlockTimeUtc DESC")
        $queryInstruction.Operand = $query
    }

    if (-not $query.Contains("JOIN Users u"))
    {
        $progressJoinPattern =
            "JOIN UserGameProgress ugp\r?\n\s+ON ugp\.Id = ua\.UserGameProgressId"
        $progressJoinReplacement =
            "JOIN UserGameProgress ugp`r`n    ON ugp.Id = ua.UserGameProgressId`r`nJOIN Users u`r`n    ON u.Id = ugp.UserId"
        $query = [Regex]::Replace(
            $query,
            $progressJoinPattern,
            $progressJoinReplacement,
            [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    }
    $localOwnerClause =
        "(u.IsCurrentUser = 1 OR (g.ProviderKey = 'Local' AND u.ProviderKey = 'Local'))"
    if ($query.Contains("AND u.IsCurrentUser = 1"))
    {
        $query = $query.Replace(
            "AND u.IsCurrentUser = 1",
            "AND $localOwnerClause")
    }
    elseif (-not $query.Contains($localOwnerClause))
    {
        $query = $query.Replace(
            "WHERE ua.Unlocked = 1",
            "WHERE ua.Unlocked = 1`r`n  AND $localOwnerClause")
    }
    $queryInstruction.Operand = $query

    if ($query.Contains($unsafeExpression) -or
        -not $query.Contains("JOIN Users u") -or
        -not $query.Contains("ON u.Id = ugp.UserId") -or
        -not $query.Contains($localOwnerClause) -or
        -not $query.Contains("ua.UnlockTimeUtc IS NOT NULL") -or
        -not $query.Contains("trim(ua.UnlockTimeUtc) != ''") -or
        -not $query.Contains("ORDER BY ua.UnlockTimeUtc DESC"))
    {
        throw "The Recent Achievements query could not be changed safely."
    }

    if (Test-Path -LiteralPath $temporaryPath)
    {
        Remove-Item -LiteralPath $temporaryPath -Force
    }

    $assembly.Write($temporaryPath)
    $assembly.Dispose()
    $assembly = $null

    $verificationAssembly =
        [Mono.Cecil.AssemblyDefinition]::ReadAssembly($temporaryPath)
    try
    {
        $verificationMethod = Get-ProviderMethod $verificationAssembly
        $verificationQueryInstruction =
            Get-RecentQueryInstruction $verificationAssembly
        $verificationQuery = [string]$verificationQueryInstruction.Operand

        if (-not (Test-PerGameLimit $verificationMethod) -or
            -not (Test-GameGrouping $verificationAssembly) -or
            $verificationQuery.Contains($unsafeExpression) -or
            -not $verificationQuery.Contains("JOIN Users u") -or
            -not $verificationQuery.Contains("ON u.Id = ugp.UserId") -or
            -not $verificationQuery.Contains($localOwnerClause) -or
            -not $verificationQuery.Contains("ua.UnlockTimeUtc IS NOT NULL") -or
            -not $verificationQuery.Contains("trim(ua.UnlockTimeUtc) != ''") -or
            -not $verificationQuery.Contains("ORDER BY ua.UnlockTimeUtc DESC"))
        {
            throw "The patched StartPage DLL did not pass IL verification."
        }
    }
    finally
    {
        $verificationAssembly.Dispose()
    }

    Copy-Item -LiteralPath $temporaryPath -Destination $PluginPath -Force
}
finally
{
    if ($null -ne $assembly)
    {
        $assembly.Dispose()
    }
    if (Test-Path -LiteralPath $temporaryPath)
    {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}

Write-Host "Patched StartPage recency to use personal and Local genuine unlock timestamps."
Write-Host "Game grouping and both configured achievement limits were preserved."
Write-Host "Output: $PluginPath"
