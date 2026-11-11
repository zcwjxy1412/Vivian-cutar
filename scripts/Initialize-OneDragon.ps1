param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$PinnedSource
)

$ErrorActionPreference = 'Stop'
$pin = '5f2e473bbc5b100ae2ea482dd25be0277bbca87c'
$branch = 'vivian-v2.6.0'
$root = (Resolve-Path -LiteralPath $InstallRoot).Path
$source = (Resolve-Path -LiteralPath $PinnedSource).Path
$repo = Split-Path $PSScriptRoot -Parent
$hook = Join-Path $repo 'src/VIvian.Infrastructure/Engines/OneDragon/vivian_handoff.py'
$launcher = Join-Path $root 'OneDragon-RuntimeLauncher.exe'
if ((Get-FileHash -LiteralPath $launcher).Hash -ne 'D4A8E2780EACC6754A88810D1D6B2FCCDF4DD60A1E00725A12B5EBE8589158C1') {
    throw 'OneDragon 启动器与已核验的 v2.6.0 WithRuntime-Full 发行包不同。'
}
$tag = git -C $source rev-parse 'refs/tags/v2.6.0'
if ($LASTEXITCODE -ne 0 -or $tag -ne $pin) { throw '源目录须包含固定版 v2.6.0。' }
$lock = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.vivian-m6/desktop-operation.lock'
if ((Get-Item -LiteralPath $lock -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '桌面锁路径包含链接。' }
$desktop = [IO.File]::Open($lock, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$archive = Join-Path ([IO.Path]::GetTempPath()) ('vivian-od-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    if ($desktop.Length -ne 0) { throw '请先完成上次桌面交接。' }
    if (Get-Process -Name 'OneDragon-RuntimeLauncher','OneDragon-Launcher','ZenlessZoneZero' -ErrorAction SilentlyContinue) {
        throw '请先关闭 OneDragon 和绝区零。'
    }
    git -C $source archive --format=zip "--output=$archive" $pin
    if ($LASTEXITCODE -ne 0) { throw '无法导出固定版 OneDragon 源码。' }
    $env:VIVIAN_ZZZ_PREP_ROOT = $root
    $env:VIVIAN_ZZZ_PREP_ARCHIVE = $archive
    $env:VIVIAN_ZZZ_PREP_HOOK = $hook
    @'
import hashlib
import os
import pathlib
import re
import stat
import zipfile

root = pathlib.Path(os.environ["VIVIAN_ZZZ_PREP_ROOT"])
entry = pathlib.PurePosixPath("src/zzz_od/application/zzz_application_launcher.py")
version = pathlib.PurePosixPath("src/one_dragon/version.py")

def lf(data):
    return data.replace(b"\r\n", b"\n")

def safe_path(relative):
    path = root
    for part in relative.parts:
        path = path / part
        if path.is_symlink() or path.is_junction():
            raise RuntimeError("OneDragon path contains a link: " + str(relative))
    return path

if root.is_symlink() or root.is_junction() or root.resolve() != root:
    raise RuntimeError("OneDragon installation path contains a link")
hook = lf(pathlib.Path(os.environ["VIVIAN_ZZZ_PREP_HOOK"]).read_bytes())
pending = []
with zipfile.ZipFile(os.environ["VIVIAN_ZZZ_PREP_ARCHIVE"]) as archive:
    original = lf(archive.read(entry.as_posix()))
    assert hashlib.sha256(original).hexdigest() == "82f44b2e51e0fe794836b66e1de1228375a6be91b93b6641d1a78c50734e770f"
    installed = original.rstrip(b"\n") + b"\n\n" + hook.rstrip(b"\n") + b"\n"
    compile(installed, entry.as_posix(), "exec")
    for member in archive.infolist():
        path = pathlib.PurePosixPath(member.filename)
        if member.is_dir():
            continue
        if path.is_absolute() or ".." in path.parts or stat.S_ISLNK(member.external_attr >> 16):
            raise RuntimeError("unexpected archive path: " + member.filename)
        dest = safe_path(path)
        if not dest.exists():
            pending.append((member, dest))
            continue
        data = dest.read_bytes()
        reference = archive.read(member)
        if path == version and lf(data).strip() == b"__version__ = 'v2.6.0'":
            continue
        if path == entry and data == installed:
            continue
        if data != reference and lf(data) != lf(reference):
            raise RuntimeError("existing installed source differs: " + member.filename)

    for member, dest in pending:
        dest.parent.mkdir(parents=True, exist_ok=True)
        with dest.open("xb") as file:
            file.write(archive.read(member))

env = safe_path(pathlib.PurePosixPath("config/env.yml"))
if env.exists():
    data = env.read_text(encoding="utf-8-sig")
    update = re.findall(r"^auto_update_code:\s*(\w+)\s*$", data, re.M)
    branch = re.findall(r"^git_branch:\s*([\w.-]+)\s*$", data, re.M)
    if update != ["false"] or branch != ["vivian-v2.6.0"]:
        raise RuntimeError("Existing env.yml must select vivian-v2.6.0 and set auto_update_code to false")
else:
    env.write_text("auto_update_code: false\ngit_branch: vivian-v2.6.0\n", encoding="utf-8", newline="\n")

target = safe_path(entry)
backup = target.with_suffix(".py.vivian-original")
if not backup.exists():
    with backup.open("xb") as file:
        file.write(original)
elif lf(backup.read_bytes()) != original:
    raise RuntimeError("Existing launcher backup is different")
if target.read_bytes() != installed:
    tmp = target.with_suffix(".py.vivian-tmp")
    with tmp.open("xb") as file:
        file.write(installed)
    os.replace(tmp, target)
print(f"Restored {len(pending)} pinned files, launcher bridge {hashlib.sha256(installed).hexdigest()}")
'@ | python -X utf8 -
    if ($LASTEXITCODE -ne 0) { throw '准备 OneDragon 固定源码失败。' }
    if (!(Test-Path -LiteralPath (Join-Path $root '.git'))) {
        git -C $root -c core.autocrlf=false init -b $branch
        if ($LASTEXITCODE -ne 0) { throw '无法初始化现有 OneDragon 目录。' }
        git -C $root fetch --no-tags --depth=1 $source 'refs/tags/v2.6.0'
        if ($LASTEXITCODE -ne 0) { throw '无法读取已核验的 OneDragon Git 对象。' }
        git -C $root update-ref "refs/heads/$branch" $pin
        if ($LASTEXITCODE -ne 0) { throw '无法设置 OneDragon 固定分支。' }
        git -C $root -c core.autocrlf=false reset --mixed $pin
        if ($LASTEXITCODE -ne 0) { throw '无法建立 OneDragon 源码索引。' }
    }
    $actual = git -C $root rev-parse HEAD
    $active = git -C $root symbolic-ref --short HEAD
    if ($actual -ne $pin -or $active -ne $branch) { throw 'OneDragon 当前源码和固定分支不一致。' }
    Write-Output "OneDragon source prepared at $root"
}
finally {
    $desktop.Dispose()
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
}
