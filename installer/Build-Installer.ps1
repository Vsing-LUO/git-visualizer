[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'output'
}
$packageRoot = Split-Path -Parent $PSScriptRoot
$programPath = Join-Path $packageRoot 'artifacts\publish\win-x64\GitVisualizer.exe'
$sourceIconPath = Join-Path $packageRoot 'src\GitVisualizer.App\Assets\GitVisualizer.ico'
$setupIconPath = Join-Path $PSScriptRoot 'GitVisualizer.ico'
$launcherSourcePath = Join-Path $PSScriptRoot 'UninstallLauncher.cs'
$launcherPath = Join-Path $PSScriptRoot 'uninstall.exe'

if (-not (Test-Path -LiteralPath $programPath -PathType Leaf)) {
    throw "找不到最终程序：$programPath"
}

if (-not (Test-Path -LiteralPath $sourceIconPath -PathType Leaf)) {
    throw "找不到应用程序图标：$sourceIconPath"
}

Copy-Item -LiteralPath $sourceIconPath -Destination $setupIconPath -Force

$csharpCompilerCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csharpCompiler = $csharpCompilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $csharpCompiler) {
    throw '未找到用于生成 uninstall.exe 的 .NET Framework C# 编译器。'
}

& $csharpCompiler `
    /nologo `
    /target:winexe `
    /platform:anycpu `
    /optimize+ `
    "/win32icon:$setupIconPath" `
    "/out:$launcherPath" `
    /reference:System.dll `
    /reference:System.Windows.Forms.dll `
    $launcherSourcePath
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
    throw 'uninstall.exe 编译失败。'
}

$compilerCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    'C:\Program Files\Inno Setup 7\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)
$compiler = $compilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $compiler) {
    throw '未找到 Inno Setup 6/7 命令行编译器 ISCC.exe。'
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& $compiler "/O$OutputDirectory" (Join-Path $PSScriptRoot 'GitVisualizer.iss')
if ($LASTEXITCODE -ne 0) {
    throw "安装程序编译失败，ISCC 退出码：$LASTEXITCODE"
}

$setupPath = Join-Path $OutputDirectory 'GitVisualizer-v1.3.2-Setup.exe'
if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
    throw "编译器未生成预期文件：$setupPath"
}

$setup = Get-Item -LiteralPath $setupPath
$hash = Get-FileHash -LiteralPath $setupPath -Algorithm SHA256
Write-Host "安装程序已生成：$($setup.FullName)"
Write-Host "大小：$($setup.Length) bytes"
Write-Host "SHA-256：$($hash.Hash)"
Write-Host "卸载入口已生成：$launcherPath"
