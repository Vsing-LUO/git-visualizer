$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $binary -Force | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /reference:System.Windows.Forms.dll /reference:System.dll `
    ('/win32icon:' + (Join-Path $root 'src/GitVisualizer.App/Assets/GitVisualizer.ico')) `
    ('/out:' + (Join-Path $binary '卸载 GitVisualizer.exe')) (Join-Path $PSScriptRoot 'UninstallLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw 'Uninstall launcher compilation failed.' }
$compiler = Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'Inno Setup compiler not found.' }
$exe = Join-Path $root 'artifacts/publish/win-x64/GitVisualizer.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Run Build-CurrentDark.ps1 first.' }
$out = Join-Path $root 'release'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$assets = Join-Path $PSScriptRoot 'assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Add-Type -AssemblyName System.Drawing
$logo = [Drawing.Image]::FromFile((Join-Path $root 'src/GitVisualizer.App/Assets/GitVisualizerLogo-256.png'))
try {
 foreach ($kind in @('wizard','header')) {
  if ($kind -eq 'wizard') { $w=344; $h=628; $size=192; $x=76; $y=100 } else { $w=110; $h=110; $size=110; $x=0; $y=0 }
  $bmp = [Drawing.Bitmap]::new($w,$h)
  $g = [Drawing.Graphics]::FromImage($bmp)
  try {
   $g.Clear([Drawing.ColorTranslator]::FromHtml('#142238'))
   $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $g.DrawImage($logo,$x,$y,$size,$size)
   if ($kind -eq 'wizard') {
    $font = [Drawing.Font]::new('Segoe UI',24,[Drawing.FontStyle]::Bold)
    $small = [Drawing.Font]::new('Segoe UI',16)
    $format = [Drawing.StringFormat]::new()
    $format.Alignment = [Drawing.StringAlignment]::Center
    try {
     $g.DrawString('GitVisualizer',$font,[Drawing.Brushes]::White,[Drawing.RectangleF]::new(0,340,$w,60),$format)
     $g.DrawString('v1.3.3',$small,[Drawing.Brushes]::LightSteelBlue,[Drawing.RectangleF]::new(0,403,$w,40),$format)
    } finally { $font.Dispose();$small.Dispose();$format.Dispose() }
   }
   $bmp.Save((Join-Path $assets ($kind+'.bmp')),[Drawing.Imaging.ImageFormat]::Bmp)
  } finally { $g.Dispose(); $bmp.Dispose() }
 }
} finally { $logo.Dispose() }
& $compiler (Join-Path $PSScriptRoot 'GitVisualizer.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Compress-Archive -LiteralPath $exe,(Join-Path $PSScriptRoot '使用说明.txt') -DestinationPath (Join-Path $out 'GitVisualizer-v1.3.3-portable.zip') -Force
Get-ChildItem -LiteralPath $out -File | Where-Object Extension -in @('.exe','.zip') | ForEach-Object {
 ((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name)
} | Set-Content -LiteralPath (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii

