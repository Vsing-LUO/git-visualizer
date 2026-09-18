$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run=Join-Path $root ('validation/uninstall-clean-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run | Out-Null
$production=Join-Path $env:LOCALAPPDATA 'GitVisualizer'
function Snapshot {
 if(Test-Path -LiteralPath $production){@(Get-ChildItem -LiteralPath $production -Recurse -File -Force | ForEach-Object { [ordered]@{path=$_.FullName;hash=(Get-FileHash -LiteralPath $_.FullName).Hash} }) | ConvertTo-Json -Depth 4 -Compress} else {'absent'}
}
$before=Snapshot
$testHelper=Join-Path $run 'test-uninstaller.exe'
$csc=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $csc /nologo /target:winexe /define:CLEANUP_TEST /reference:System.Windows.Forms.dll /reference:System.dll ('/out:'+$testHelper) (Join-Path $PSScriptRoot 'UninstallLauncher.cs')
if($LASTEXITCODE -ne 0){throw 'Test helper compilation failed'}
$compiler=Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'
& $compiler /DTestBuild '/DAppName=GitVisualizer Uninstall QA' ('/DLauncherSource='+$testHelper) ('/O'+$run) /FSetup-QA (Join-Path $PSScriptRoot 'GitVisualizer.iss') *> (Join-Path $run 'test-build.log')
if($LASTEXITCODE -ne 0){throw 'Test installer build failed'}
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GitVisualizer.Uninstall.QA_is1'
$desktop=Join-Path ([Environment]::GetFolderPath('Desktop')) 'GitVisualizer Uninstall QA.lnk'
if((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $desktop)){throw 'Previous QA installation exists'}
$originalOverride=$env:GV_UNINSTALL_TEST_ROOT
try {
 $env:GV_UNINSTALL_TEST_ROOT=Join-Path $run 'isolated-profile'
 $data=Join-Path $env:GV_UNINSTALL_TEST_ROOT 'GitVisualizer'
 $cache=Join-Path $env:GV_UNINSTALL_TEST_ROOT '.net/GitVisualizer/hash'
 $external=Join-Path $run 'unrelated-repository'
 New-Item -ItemType Directory -Path $external -Force | Out-Null
 [IO.File]::WriteAllText((Join-Path $external 'valuable.txt'),'keep external repository')
 $install=Join-Path $run 'installed application 中文'
 if(Test-Path -LiteralPath $install){throw 'Install test directory exists'}
 $p=Start-Process -FilePath (Join-Path $run 'Setup-QA.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER','/TASKS="desktopicon"',('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $run 'install.log')+'"')) -WindowStyle Hidden -Wait -PassThru
 if($p.ExitCode -ne 0){throw 'QA install failed'}
 $visible=Join-Path $install '卸载 GitVisualizer.exe'
 if(!(Test-Path -LiteralPath $visible)){throw 'Visible uninstaller missing'}
 if((Get-FileHash -LiteralPath $visible).Hash -ne (Get-FileHash -LiteralPath $testHelper).Hash){throw 'Uninstall helper mismatch'}
 foreach($folder in @('Recovery','Drafts/quarantine','Logs')) {New-Item -ItemType Directory -Path (Join-Path $data $folder) -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $data ($folder+'/sample.dat')),'app data')}
 [IO.File]::WriteAllText((Join-Path $data 'settings.json'),'settings')
 [IO.File]::WriteAllText((Join-Path $data 'state.db'),'database')
 New-Item -ItemType Directory -Path $cache -Force | Out-Null
 [IO.File]::WriteAllText((Join-Path $cache 'runtime.dll'),'cache')
 New-Item -ItemType Junction -Path (Join-Path $data 'external-link') -Target $external | Out-Null
 # A locked data file must stop uninstall and retain installed program/registration.
 $locked=[IO.File]::Open((Join-Path $data 'settings.json'),'Open','ReadWrite','None')
 try {
  $p=Start-Process -FilePath (Join-Path $install '.uninstall/unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $run 'locked-uninstall.log')+'"')) -WindowStyle Hidden -Wait -PassThru
  $lockedExit=$p.ExitCode
  if($lockedExit -eq 0 -or !(Test-Path -LiteralPath (Join-Path $install 'GitVisualizer.exe')) -or !(Test-Path -LiteralPath $registry)){throw 'Locked cleanup failure was not safely reported'}
 } finally {$locked.Dispose()}
 # Recreate all categories after partial cleanup to prove success covers every one.
 foreach($folder in @('Recovery','Drafts/quarantine','Logs')) {New-Item -ItemType Directory -Path (Join-Path $data $folder) -Force | Out-Null; [IO.File]::WriteAllText((Join-Path $data ($folder+'/sample.dat')),'app data')}
 [IO.File]::WriteAllText((Join-Path $data 'state.db'),'database')
 [IO.File]::WriteAllText((Join-Path $install 'user-owned.txt'),'keep user file')
 $p=Start-Process -FilePath $visible -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $run 'uninstall.log')+'"')) -WindowStyle Hidden -Wait -PassThru
 if($p.ExitCode -ne 0){throw 'Visible uninstaller launcher failed'}
 $deadline=[DateTime]::UtcNow.AddSeconds(45)
 while((Test-Path -LiteralPath $registry) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 200}
 foreach($path in @($data,(Join-Path $env:GV_UNINSTALL_TEST_ROOT '.net/GitVisualizer'),$visible,(Join-Path $install 'GitVisualizer.exe'),$registry,$desktop)){if(Test-Path -LiteralPath $path){throw "Uninstall left: $path"}}
 if([IO.File]::ReadAllText((Join-Path $external 'valuable.txt')) -ne 'keep external repository'){throw 'Junction escaped cleanup scope'}
 if([IO.File]::ReadAllText((Join-Path $install 'user-owned.txt')) -ne 'keep user file'){throw 'Uninstall removed unrelated file'}
 if((Snapshot) -cne $before){throw 'Production profile changed'}
 [ordered]@{status='passed';visibleLauncher=$true;settingsLogsDraftsRecoveryRemoved=$true;cacheRemoved=$true;junctionTargetPreserved=$true;unrelatedFilePreserved=$true;lockedCleanupAbortsUninstall=$true;lockedExitCode=$lockedExit;productionProfileUnchanged=$true;isolation='Same source with CLEANUP_TEST selecting isolated data/cache paths and test-only credential prefix; test AppId and shortcut names.'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'tests.json') -Encoding utf8
 Write-Output 'Clean uninstall integration passed.'
} finally { $env:GV_UNINSTALL_TEST_ROOT=$originalOverride }
