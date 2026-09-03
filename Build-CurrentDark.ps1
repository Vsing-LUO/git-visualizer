[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'artifacts\publish\win-x64'
}

$sdkCandidates = @(
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
)
$dotnet = $sdkCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $dotnet) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

$installedSdks = & $dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or $installedSdks -notmatch '^10\.0\.302\s') {
    throw '.NET SDK 10.0.302 is required. The published executable does not require an installed SDK.'
}

$project = Join-Path $PSScriptRoot 'src\GitVisualizer.App\GitVisualizer.App.csproj'
$iconBuilder = Join-Path $PSScriptRoot 'Build-AppIcon.ps1'

& $iconBuilder
if ($LASTEXITCODE -ne 0) {
    throw 'Application icon generation failed.'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

& $dotnet restore $project `
    --runtime win-x64
if ($LASTEXITCODE -ne 0) {
    throw 'Dependency restore failed.'
}

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $OutputDirectory

if ($LASTEXITCODE -ne 0) {
    throw 'Publish failed.'
}

Write-Host "Build completed: $OutputDirectory"
