#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$GameExecutable,
    [string]$GuildCountry = '枫丹',
    [Guid]$AccountId,
    [string]$AccountPlanPath,
    [switch]$PlanOnly
)

$ErrorActionPreference = 'Stop'
$archiveFile = Get-Item -LiteralPath $Archive
$game = Get-Item -LiteralPath $GameExecutable
if ($archiveFile.PSIsContainer -or $game.PSIsContainer -or $game.Name -notin 'YuanShen.exe', 'GenshinImpact.exe') {
    throw '需要官方 BetterGI 7z 以及已经安装的原神客户端 EXE。'
}
if ([string]::IsNullOrWhiteSpace($GuildCountry) -or $GuildCountry.Length -gt 16 -or $GuildCountry -match '[\p{C}]') {
    throw '请输入 BetterGI 已配置的冒险家协会国家。'
}

$name = 'VIvian 原神日常验收'
$profile = @{
    Name = $name
    TaskEnabledList = @{ '领取每日奖励' = $true }
    AdventurersGuildCountry = $GuildCountry
    DailyRewardPartyName = ''
    CompletionAction = '关闭软件'
}
$configuration = @{
    selectedOneDragonFlowConfigName = $name
    commonConfig = @{ runForVersion = '0.66.0'; isFirstRun = $false; exitToTray = $false }
    genshinStartConfig = @{
        installPath = $game.FullName
        linkedStartEnabled = $false
        startGameWithCmd = $false
        autoEnterGameEnabled = $false
        autoDisableGenshinHdrEnabled = $false
    }
    scriptConfig = @{ autoUpdateSubscribedScripts = $false; autoUpdateBeforeCommandLineRun = $false }
    notificationConfig = @{ dragonEndSummaryEnabled = $false }
}
$usingAccount = $AccountId -ne [Guid]::Empty
if ($usingAccount -ne (![string]::IsNullOrWhiteSpace($AccountPlanPath))) {
    throw '使用账号任务设置时，需要同时给出账号档案编号和设置文件。'
}
if ($usingAccount) {
    $planFile = Get-Item -LiteralPath $AccountPlanPath
    if ($planFile.PSIsContainer -or $planFile.Length -gt 65536 -or ($planFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw '已生成的原神任务档案无法读取。'
    }
    $files = Get-Content -LiteralPath $planFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    $name = $files.ProfileName
    if ($name -cne ('VIvian ' + $AccountId.ToString('N'))) { throw '生成的任务档案编号不符。' }
    $profile = $files.ProfileJson | ConvertFrom-Json -AsHashtable
    $configuration = $files.SettingsJson | ConvertFrom-Json -AsHashtable
    if ($configuration.genshinStartConfig.installPath -ine $game.FullName) { throw '生成的任务档案对应其他客户端。' }
}

$expected = '28D4756E55F32637538BF84427F5F31AFD5B153BF996530C5975BD9C8C0B9343'
if ($PlanOnly) {
    if ((Get-FileHash -LiteralPath $archiveFile.FullName -Algorithm SHA256).Hash -ne $expected) {
        throw '官方 0.66.0 发行包摘要不符。'
    }
    @{ ArchiveVerified = $true; Profile = $profile; Configuration = $configuration } | ConvertTo-Json -Depth 8
    return
}
$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (!([Security.Principal.WindowsPrincipal]::new($currentIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '请在同一 Windows 用户的管理员终端准备隔离验收目录。'
    }
} finally {
    $currentIdentity.Dispose()
}

$base = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) 'VIvian M6 BetterGI Acceptance'
$admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
if (!(Test-Path -LiteralPath $base)) {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($admins)
    $security.SetAccessRuleProtection($true, $false)
    foreach ($identity in $admins, $system) {
        $rule = [Security.AccessControl.FileSystemAccessRule]::new(
            $identity, 'FullControl', $inherit, 'None', 'Allow')
        $security.AddAccessRule($rule)
    }
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($base), $security)
}

$baseInfo = Get-Item -LiteralPath $base
$acl = [IO.FileSystemAclExtensions]::GetAccessControl($baseInfo)
$untrusted = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object {
    $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -notin $admins.Value, $system.Value
})
if (!$baseInfo.PSIsContainer -or ($baseInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    !$acl.AreAccessRulesProtected -or $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $admins.Value -or
    $untrusted.Count -ne 0) {
    throw '独立验收目录权限不符，拒绝写入。'
}

$stage = Join-Path $base ([Guid]::NewGuid().ToString('N'))
[IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($stage), $acl)
$localArchive = Join-Path $stage 'official.7z'
[IO.File]::Copy($archiveFile.FullName, $localArchive, $false)
if ((Get-FileHash -LiteralPath $localArchive -Algorithm SHA256).Hash -ne $expected) {
    throw '官方 0.66.0 发行包摘要不符，未解包。'
}
$tar = Join-Path ([Environment]::SystemDirectory) 'tar.exe'
& $tar --no-acls --no-same-permissions -xf $localArchive -C $stage
if ($LASTEXITCODE -ne 0) { throw '官方包解包失败。' }
Remove-Item -LiteralPath $localArchive

$root = Join-Path $stage 'BetterGI'
$rootInfo = Get-Item -LiteralPath $root
$rootAcl = [IO.FileSystemAclExtensions]::GetAccessControl($rootInfo)
$rootAcl.SetOwner($admins)
[IO.FileSystemAclExtensions]::SetAccessControl($rootInfo, $rootAcl)
$user = Join-Path $root 'User'
$profileDirectory = Join-Path $user 'OneDragon'
New-Item -ItemType Directory -Path $profileDirectory | Out-Null
$profilePath = Join-Path $profileDirectory "$name.json"
$settingsPath = Join-Path $user 'config.json'
$profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $profilePath -Encoding utf8
$configuration | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $settingsPath -Encoding utf8
if ($usingAccount) {
    @{
        AccountId = $AccountId.ToString('D')
        Values = $files.Values
        ProfileSha256 = (Get-FileHash -LiteralPath $profilePath -Algorithm SHA256).Hash
        SettingsSha256 = (Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'account-plan.json') -Encoding utf8
}

Set-Content -LiteralPath (Join-Path $stage 'verified-archive.txt') -Value $expected -Encoding ascii
Write-Output $stage
