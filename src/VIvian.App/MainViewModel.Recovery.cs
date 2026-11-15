using System.IO;
using System.Windows;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Processes;

namespace VIvian.App;

public sealed partial class MainViewModel
{
    internal static HomeStatusAction RecoveryActionFor(Exception error) => error switch
    {
        DesktopRecoveryRequiredException => HomeStatusAction.RecoverDesktop,
        SettingsConflictException => HomeStatusAction.ReloadSettings,
        _ => HomeStatusAction.ExportDiagnostics,
    };

    private async Task ReloadEngineSettingsAsync()
    {
        if (!CanOperateWorkspace || CanStopAll) return;
        if (MessageBox.Show("重新读取已保存的安装路径，放弃当前尚未保存的路径修改？账号和任务数据不受影响。",
            "重载最新配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var saved in await _settings.LoadSettingsAsync())
        {
            var card = Engines.Single(x => x.Id == saved.Game);
            card.InstallPath = card.SavedInstallPath = saved.InstallPath;
            card.Revision = saved.Revision;
        }
        await CheckEnginesCoreAsync();
    }

    private async Task RepairEnginePathsAsync(GameId? game = null)
    {
        if (!CanOperateWorkspace || CanStopAll) return;
        var card = game is { } id ? Engines.Single(c => c.Id == id) : Engines.FirstOrDefault(c =>
            c.InstallPath != c.SavedInstallPath || c.Report is null || c.Report.Checks.Any(x => x.State == CheckState.Failed));
        if (card is null) { await CheckEnginesCoreAsync(); return; }
        await RepairEnginePathAsync(card, () =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = $"选择 {card.Name} 的引擎安装目录" };
            return picker.ShowDialog() == true ? picker.FolderName : null;
        });
    }

    internal async Task RepairEnginePathAsync(EngineCard card, Func<string?> chooseFolder)
    {
        if (!CanOperateWorkspace || CanStopAll) return;
        var previous = card.InstallPath;
        var executable = card.Id switch { GameId.Genshin => "BetterGI.exe", GameId.StarRail => "m7a.exe", _ => "OneDragon-RuntimeLauncher.exe" };
        var candidate = File.Exists(Path.Combine(previous, executable)) ? previous :
            File.Exists(Path.Combine(card.SavedInstallPath, executable)) ? card.SavedInstallPath : chooseFolder();
        if (candidate is null) return;
        var savedSuccessfully = false;
        try
        {
            card.InstallPath = candidate;
            await InspectAsync(card);
            if (card.Report?.Checks.Any(c => c.State == CheckState.Failed) != false)
            {
                ShowStatus($"{card.Name} 安装核验未通过：{card.Evidence}。原路径保持不变，请检查安装内容。", HomeStatusAction.ConfigureEngines);
                return;
            }
            if (candidate != card.SavedInstallPath)
            {
                var saved = await _settings.SaveSettingAsync(new EngineSetting(card.Id, candidate, card.Revision));
                card.Revision = saved.Revision; card.SavedInstallPath = saved.InstallPath;
            }
            savedSuccessfully = true;
            await CheckEnginesCoreAsync();
        }
        finally
        {
            if (!savedSuccessfully)
            {
                card.InstallPath = previous;
                // The status above describes the rejected candidate; restore the original card evidence too.
                await InspectAsync(card);
            }
        }
    }

    private async Task ConfirmHomeCleanupAsync()
    {
        if (!Queue.CanAcknowledge || CanStopAll) return;
        if (Queue.Unresolved.Any(r => r.Game == GameId.Genshin && !r.IsSimulation))
        { ShowStatus("原神需要核对并恢复桌面交接。", HomeStatusAction.RecoverDesktop); return; }
        if (!Queue.HasUnresolved) { ShowStatus("没有待确认的运行记录。"); return; }
        if (MessageBox.Show("请确认上次运行的游戏、引擎和输入宿主均已退出。\n确认后记录清理状态；不会把失败任务改为成功，也不会自动重跑。",
            "确认运行已清理", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Queue.AcknowledgeAllAsync();
        ShowStatus(Queue.Status, Queue.HasUnresolved ? HomeStatusAction.ConfirmCleanup :
            _control.HasPendingConfigurationRestore ? HomeStatusAction.RestoreConfiguration : HomeStatusAction.None);
    }
}
