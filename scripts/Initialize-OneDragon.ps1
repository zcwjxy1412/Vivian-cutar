param(
    [Parameter(Mandatory)][string]$InstallRoot,
    [Parameter(Mandatory)][string]$PinnedSource,
    [string]$RuntimeArchive
)

$ErrorActionPreference = 'Stop'
$pin = '5f2e473bbc5b100ae2ea482dd25be0277bbca87c'
$branch = 'vivian-v2.6.0'
$root = (Resolve-Path -LiteralPath $InstallRoot).Path
$source = (Resolve-Path -LiteralPath $PinnedSource).Path
$repo = Split-Path $PSScriptRoot -Parent
$hook = Join-Path $repo 'src/VIvian.Infrastructure/Engines/OneDragon/vivian_handoff.py'
$launcher = Join-Path $root 'OneDragon-RuntimeLauncher.exe'
$knownLauncher = 'D4A8E2780EACC6754A88810D1D6B2FCCDF4DD60A1E00725A12B5EBE8589158C1'
if ($RuntimeArchive) {
    $RuntimeArchive = (Resolve-Path -LiteralPath $RuntimeArchive).Path
}
elseif (!(Test-Path -LiteralPath $launcher -PathType Leaf) -or (Get-FileHash -LiteralPath $launcher).Hash -ne $knownLauncher) {
    throw 'OneDragon 启动器与固定版不同。请用 -RuntimeArchive 指定已核验的 v2.6.0 WithRuntime-Full 原包。'
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
    $env:VIVIAN_ZZZ_RUNTIME_ZIP = $RuntimeArchive
    @'
import hashlib
import os
import pathlib
import re
import shutil
import stat
import tempfile
import uuid
import zipfile

root = pathlib.Path(os.environ["VIVIAN_ZZZ_PREP_ROOT"])
entry = pathlib.PurePosixPath("src/zzz_od/application/zzz_application_launcher.py")
version = pathlib.PurePosixPath("src/one_dragon/version.py")
updates = pathlib.PurePosixPath("src/one_dragon/envs/update_service.py")

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
def pin_launcher_updates(data):
    edits = {
        '        """检查当前启动器通道是否存在可用更新。"""\n':
            '        if self.env_config.git_branch == "vivian-v2.6.0":\n            return False\n',
        '        """校验临时目录后再备份并替换启动器，失败时自动回滚。"""\n':
            '        if self.env_config.git_branch == "vivian-v2.6.0":\n'
            '            raise RuntimeError("Vivian 固定版安装由 Vivian 管理启动器版本。")\n',
        '        """启动时回滚中断的更新，或清理上次成功更新保留的备份。"""\n':
            '        if self.env_config.git_branch == "vivian-v2.6.0":\n            return\n',
    }
    text = lf(data).decode("utf-8")
    for anchor, guard in edits.items():
        if text.count(anchor) != 1:
            raise RuntimeError("Unexpected OneDragon update service layout")
        text = text.replace(anchor, anchor + guard, 1)
    compile(text, updates.as_posix(), "exec")
    return text.encode("utf-8")

pending = []
with zipfile.ZipFile(os.environ["VIVIAN_ZZZ_PREP_ARCHIVE"]) as archive:
    original = lf(archive.read(entry.as_posix()))
    pinned_updates = pin_launcher_updates(archive.read(updates.as_posix()))
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
        if (path == entry and data == installed) or (path == updates and data == pinned_updates):
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
update_target = safe_path(updates)
if update_target.read_bytes() != pinned_updates:
    update_tmp = update_target.with_suffix(".py.vivian-tmp")
    with update_tmp.open("xb") as file:
        file.write(pinned_updates)
    os.replace(update_tmp, update_target)

runtime_archive = os.environ.get("VIVIAN_ZZZ_RUNTIME_ZIP")
if runtime_archive:
    launcher_name = "OneDragon-RuntimeLauncher.exe"
    runtime_parts = [launcher_name, ".runtime"]
    with open(runtime_archive, "rb") as file:
        if hashlib.file_digest(file, "sha256").hexdigest() != "2fd5f6bfc6c01fcf6f21092dc6e18fa18e146d2614ca6ad130d5fc3057ba0ced":
            raise RuntimeError("Unknown OneDragon v2.6.0 Runtime archive")
        with zipfile.ZipFile(file) as package, tempfile.TemporaryDirectory(prefix=".vivian-restore-", dir=root) as stage:
            stage = pathlib.Path(stage)
            for member in package.infolist():
                relative = pathlib.PurePosixPath(member.filename)
                if not relative.parts or relative.parts[0] not in runtime_parts or member.is_dir():
                    continue
                if relative.is_absolute() or ".." in relative.parts or stat.S_ISLNK(member.external_attr >> 16):
                    raise RuntimeError("Unexpected runtime archive path")
                destination = stage.joinpath(*relative.parts)
                destination.parent.mkdir(parents=True, exist_ok=True)
                with package.open(member) as source_file, destination.open("xb") as output:
                    shutil.copyfileobj(source_file, output)
            if hashlib.sha256((stage / launcher_name).read_bytes()).hexdigest() != "d4a8e2780eacc6754a88810d1d6b2fccdf4dd60a1e00725a12b5ebe8589158c1":
                raise RuntimeError("Unexpected launcher from runtime archive")
            if not (stage / ".runtime").is_dir():
                raise RuntimeError("Runtime archive is incomplete")
            destinations = [safe_path(pathlib.PurePosixPath(name)) for name in runtime_parts]
            backup = root / (".vivian-runtime-before-" + uuid.uuid4().hex)
            backup.mkdir()
            backed_up = []
            restored = []
            try:
                for dest in destinations:
                    if dest.exists():
                        dest.rename(backup / dest.name)
                        backed_up.append(dest)
                for dest in destinations:
                    (stage / dest.name).rename(dest)
                    restored.append(dest)
            except OSError:
                for dest in reversed(restored):
                    dest.rename(stage / dest.name)
                for dest in reversed(backed_up):
                    (backup / dest.name).rename(dest)
                raise
            print(f"Restored verified launcher and runtime. Previous files: {backup}")
print(f"Restored {len(pending)} pinned files, launcher bridge {hashlib.sha256(installed).hexdigest()}, update lock {hashlib.sha256(pinned_updates).hexdigest()}")
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
