param(
    [string]$Configuration = 'Debug',
    [string]$CacheRoot
)

$ErrorActionPreference = 'Stop'

function Resolve-PathSetting([string]$ConfiguredValue, [string]$EnvVarName, [string]$Fallback) {
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredValue)) {
        return $ConfiguredValue
    }

    $environmentValue = [System.Environment]::GetEnvironmentVariable($EnvVarName)
    if (-not [string]::IsNullOrWhiteSpace($environmentValue)) {
        return $environmentValue
    }

    return $Fallback
}

$CacheRoot = Resolve-PathSetting $CacheRoot 'UNDO_THE_SPIRE2_CACHE_ROOT' 'F:\projects\undo-the-spire2-cache'
$projectPath = Join-Path $PSScriptRoot '..\undo the spire2\undo the spire2.csproj'
$dotnetHome = Join-Path $CacheRoot 'dotnet-home'
$tempRoot = Join-Path $CacheRoot 'temp'
$nugetPackages = Join-Path $CacheRoot 'nuget-packages'

New-Item -ItemType Directory -Force -Path $CacheRoot, $dotnetHome, $tempRoot, $nugetPackages | Out-Null

$env:DOTNET_CLI_HOME = $dotnetHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:NUGET_PACKAGES = $nugetPackages
$env:TEMP = $tempRoot
$env:TMP = $tempRoot

dotnet build $projectPath -c $Configuration -v minimal
