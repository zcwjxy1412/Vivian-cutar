[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Candidate,
  [Parameter(Mandatory)][string]$Install,
  [Parameter(Mandatory)][string]$DataDirectory,
  [Parameter(Mandatory)][string]$Snapshot,
  [Parameter(Mandatory)][string]$Backup,
  [Parameter(Mandatory)][string]$ExpectedCommit,
  [switch]$RehearsalFailureAfterInstall,
  [switch]$RehearsalFailureAfterDatabase
)
$ErrorActionPreference='Stop'

function Assert-Physical([string]$Path) {
  $full=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
  for($part=$full; $part; $part=[IO.Path]::GetDirectoryName($part)) {
    if((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
      throw '部署路径包含链接，未做任何切换。'
    }
  }
  return $full
}
function Copy-Verified([string]$From,[string]$To) {
  if(Test-Path -LiteralPath $To){throw '备份或暂存目录已存在。'}
  [void][IO.Directory]::CreateDirectory($To)
  foreach($entry in Get-ChildItem -LiteralPath $From -Force) {
    $source=Assert-Physical $entry.FullName
    $target=Join-Path $To $entry.Name
    if($entry.PSIsContainer){Copy-Verified $source $target}
    else {
      [IO.File]::Copy($source,$target,$false)
      if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash){throw '复制校验失败。'}
    }
  }
}
function Compare-Data([string]$Original,[string]$Prepared) {
  $check=@'
import sqlite3,sys,pathlib,json
old,new=map(pathlib.Path,sys.argv[1:])
db=sqlite3.connect(old.resolve().as_uri()+'?mode=ro',uri=True)
db.execute('ATTACH DATABASE ? AS next',(new.resolve().as_uri()+'?mode=ro',))
for schema in ['main','next']:
 assert db.execute('PRAGMA '+schema+'.integrity_check').fetchone()[0]=='ok','integrity'
 assert db.execute('PRAGMA '+schema+'.application_id').fetchone()[0]==1447646798,'profile'
assert db.execute('PRAGMA main.user_version').fetchone()[0] in (7,8),'source_version'
assert db.execute('PRAGMA next.user_version').fetchone()[0]==8,'target_version'
assert db.execute("select count(*) from next.execution_plans where state in ('Accepted','Running')").fetchone()[0]==0,'active_plan'
assert db.execute("select count(*) from main.runs where cleanup='Pending' or (cleanup='Unconfirmed' and acknowledged_at is null) or phase not in ('Finished','Blocked')").fetchone()[0]==0,'unresolved'
assert db.execute("select count(*) from main.batch_execution_records where state='running'").fetchone()[0]==0,'batch_running'
tables=[r[0] for r in db.execute("select name from main.sqlite_master where type='table'")]
for name in tables:
 q='"'+name.replace('"','""')+'"'
 assert db.execute('select count(*) from main.'+q).fetchone()==db.execute('select count(*) from next.'+q).fetchone(),'count'
 for first,second in [('main','next'),('next','main')]:
  assert db.execute('select * from '+first+'.'+q+' EXCEPT select * from '+second+'.'+q).fetchone() is None,'contents'
print(json.dumps({'original_tables_identical':len(tables),'integrity':'ok','schema':8}))
'@
  $result=& python -c $check $Original $Prepared
  if($LASTEXITCODE -ne 0){throw '数据库比对失败，拒绝部署。'}
  return ($result -join "`n")
}

$Candidate=Assert-Physical $Candidate; $Install=Assert-Physical $Install
$DataDirectory=Assert-Physical $DataDirectory; $Snapshot=Assert-Physical $Snapshot; $Backup=Assert-Physical $Backup
$all=@($Candidate,$Install,$DataDirectory,$Snapshot,$Backup)
for($i=0;$i -lt $all.Count;$i++){for($j=0;$j -lt $all.Count;$j++){
  if($i -ne $j -and ($all[$i] -eq $all[$j] -or $all[$i].StartsWith($all[$j]+'\',[StringComparison]::OrdinalIgnoreCase))){throw '部署路径重叠。'}
}}
if($ExpectedCommit -notmatch '^[0-9a-f]{40}$'){throw '需要完整源码提交号。'}
foreach($directory in @($Candidate,$Install,$DataDirectory,$Snapshot)){if(-not(Test-Path -LiteralPath $directory -PathType Container)){throw '必要目录缺失。'}}
if(Test-Path -LiteralPath $Backup){throw '备份目录必须是新目录。'}
if(-not(Test-Path -LiteralPath (Join-Path $Candidate 'VIvian-UnifiedInstall.marker')) -or
   -not(Test-Path -LiteralPath (Join-Path $Candidate 'VIvian.exe')) -or
   (Get-Content -LiteralPath (Join-Path $Candidate 'BUILD-INFO.txt') -Raw) -notmatch [regex]::Escape($ExpectedCommit)){throw '候选身份不符。'}
if([IO.Path]::GetPathRoot($Install) -ne [IO.Path]::GetPathRoot($Backup)){throw '程序备份须在安装所在盘。'}
if(($RehearsalFailureAfterInstall -or $RehearsalFailureAfterDatabase) -and -not ([IO.Path]::GetFileName([IO.Path]::GetDirectoryName($Install)).StartsWith('vivian-deploy-rehearsal-'))){throw '故障注入只允许临时演练目录。'}
$profileDb=Join-Path $DataDirectory 'vivian.db'; $snapshotDb=Join-Path $Snapshot 'vivian.db'
$nameBytes=[Text.Encoding]::UTF8.GetBytes($DataDirectory.ToUpperInvariant())
$mutexName='Local\VIvian-'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($nameBytes)).Substring(0,24)
$mutex=[Threading.Mutex]::new($false,$mutexName); $owned=$false; $desktop=$null
$id=[Guid]::NewGuid().ToString('N'); $staged=Join-Path ([IO.Path]::GetDirectoryName($Install)) ('.furina-stage-'+$id)
$oldProgram=Join-Path $Backup 'program'; $oldDb=$profileDb+'.before-'+$id+'.bak'; $nextDb=$profileDb+'.next-'+$id
$oldMoved=$false; $newMoved=$false; $dbReplaced=$false
try {
  try{$owned=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true}
  if(-not $owned){throw '请先关闭正式 furina，再更新。'}
  $lockPath=Join-Path ([Environment]::GetFolderPath('UserProfile')) '.vivian-m6\desktop-operation.lock'
  [void](Assert-Physical $lockPath)
  $desktop=[IO.FileStream]::new($lockPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  if($desktop.Length -ne 0){throw '桌面清理待确认，拒绝部署。'}
  if(Get-Process VIvian -ErrorAction SilentlyContinue){throw '请先关闭所有 furina 窗口，再更新。'}
  foreach($name in @('configuration-recovery\temporary-config-recovery.json','protocol-recovery.json','vivian.db-wal','vivian.db-shm','vivian.db-journal')) {
    if(Test-Path -LiteralPath (Join-Path $DataDirectory $name)){throw '档案仍有恢复记录或数据库伴随文件，拒绝切换。'}
  }
  foreach($suffix in @('-wal','-shm','-journal')) {
    if(Test-Path -LiteralPath ($snapshotDb+$suffix)){throw '升级副本仍有数据库伴随文件，拒绝切换。'}
  }
  $dataEvidence=Compare-Data $profileDb $snapshotDb
  Copy-Verified $Candidate $staged
  [void][IO.Directory]::CreateDirectory($Backup)
  Copy-Verified $DataDirectory (Join-Path $Backup 'data')
  $journal=@{commit=$ExpectedCommit;install=$Install;profile=$DataDirectory;backup=$Backup;old_database=$oldDb;staged_database=$nextDb;staged_program=$staged;original_database_sha256=(Get-FileHash -LiteralPath $profileDb).Hash;state='backed_up';data_check=($dataEvidence|ConvertFrom-Json)}
  $journal|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Backup 'deployment.json') -Encoding utf8
  [IO.File]::Copy($snapshotDb,$nextDb,$false)
  if((Get-FileHash -LiteralPath $snapshotDb).Hash -ne (Get-FileHash -LiteralPath $nextDb).Hash){throw '升级副本复制校验失败。'}
  $journal.state='switching_program'; $journal|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Backup 'deployment.json') -Encoding utf8
  [void](Assert-Physical $Install); [void](Assert-Physical $oldProgram); [void](Assert-Physical $staged)
  [IO.Directory]::Move($Install,$oldProgram); $oldMoved=$true
  [IO.Directory]::Move($staged,$Install); $newMoved=$true
  if($RehearsalFailureAfterInstall){throw '故障注入：程序切换后回滚。'}
  $journal.state='switching_database'; $journal|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Backup 'deployment.json') -Encoding utf8
  [void](Assert-Physical $profileDb); [void](Assert-Physical $nextDb)
  [IO.File]::Replace($nextDb,$profileDb,$oldDb); $dbReplaced=$true
  if($RehearsalFailureAfterDatabase){throw '故障注入：数据库切换后回滚。'}
  [void](Compare-Data (Join-Path $Backup 'data\vivian.db') $profileDb)
  if((Get-FileHash -LiteralPath (Join-Path $Candidate 'VIvian.exe')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $Install 'VIvian.exe')).Hash){throw '安装程序校验失败。'}
  $journal.state='installed'; $journal.installed_database_sha256=(Get-FileHash -LiteralPath $profileDb).Hash
  $journal|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $Backup 'deployment.json') -Encoding utf8
  Write-Output "更新成功；备份：$Backup"
} catch {
  $originalFailure=$_; $rollbackErrors=[Collections.Generic.List[string]]::new(); $databaseRestored=-not $dbReplaced
  if($dbReplaced){
    try{[IO.File]::Replace($oldDb,$profileDb,$profileDb+'.failed-'+$id); $databaseRestored=$true}
    catch{$rollbackErrors.Add('旧数据库恢复失败；保留新程序，避免把旧程序配到新库。')}
  }
  if($databaseRestored){
    if($newMoved){
      try{[IO.Directory]::Move($Install,(Join-Path $Backup 'failed-candidate'))}
      catch{$rollbackErrors.Add('候选程序移动失败；旧程序仍在备份目录。')}
    }
    if($oldMoved -and -not(Test-Path -LiteralPath $Install)){
      try{[IO.Directory]::Move($oldProgram,$Install)}catch{$rollbackErrors.Add('旧程序恢复失败；请从备份恢复。')}
    }
  }
  if(Test-Path -LiteralPath $Backup){
    try{(@('部署失败；请检查 deployment.json 与保留文件。')+@($rollbackErrors))|Set-Content -LiteralPath (Join-Path $Backup 'rollback-status.txt')}
    catch{$rollbackErrors.Add('回滚说明写入失败。')}
  }
  if($rollbackErrors.Count -gt 0){throw ('自动回滚未全部完成。备份：'+$Backup+'；'+($rollbackErrors -join ' '))}
  throw $originalFailure
} finally {
  if($desktop){$desktop.Dispose()}
  if($owned){$mutex.ReleaseMutex()}
  $mutex.Dispose()
}

