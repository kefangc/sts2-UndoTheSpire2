param(
    [string]$Configuration = 'Debug',
    [string]$CacheRoot = 'F:\projects\undo-the-spire2-cache'
)

$ErrorActionPreference = 'Stop'
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
