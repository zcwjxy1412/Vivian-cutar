using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using VIvian.Infrastructure.Assistant;

namespace VIvian.App;

public sealed class ProposalCard : ObservableObject
{
    private readonly ProposalView _view;
    public ProposalCard(ProposalView view, Func<ProposalCard, Task> confirm)
    {
        _view = view;
        _state = view.Valid ? "等待你确认" : "暂时不能写入：" + view.Reason;
        ConfirmCommand = new(() => confirm(this), ex => State = ex.Message);
    }
    public string Id => _view.Id;
    public string Lines => string.Join("\n", _view.Changes.Select(c => $"{c.Title}：{c.Old} → {c.New}"));
    public string Reason => _view.Reason;
    private string _state;
    public string State { get => _state; set => Set(ref _state, value); }
    private bool _canConfirm = true;
    public bool CanConfirm { get => _canConfirm; set => Set(ref _canConfirm, value); }
    public AsyncCommand ConfirmCommand { get; }
}

public sealed class ChatItem(bool fromUser, string text, IReadOnlyList<ProposalCard>? proposals = null)
{
    public bool FromUser { get; } = fromUser;
    public string Text { get; } = text;
    public IReadOnlyList<ProposalCard> Proposals { get; } = proposals ?? [];
    public HorizontalAlignment Align => FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Visibility AvatarVisibility => FromUser ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// The floating assistant. Talks to the user's own OpenAI-compatible endpoint; every action goes through
/// <see cref="AssistantTools"/>, and configuration is written only when the user presses “确认修改”.
/// </summary>
public sealed class AssistantViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly AssistantSettingsStore _settings;
    private readonly AssistantTools _tools;
    private readonly Func<bool> _isRunning;
    private readonly string _auditDirectory;
    private readonly Action _afterApply;
    private AssistantSession _session;
    private AssistantEndpoint _endpoint;

    public ThemeAssets Theme { get; }
    public ArtSlot Avatar { get; }
    public string Name => Theme.AssistantName;
    public ObservableCollection<ChatItem> Messages { get; } = [];
    public IReadOnlyList<string> QuickCommands { get; } = ["帮我检查星铁日常的配置", "清体力改成饰品提取", "最近一次运行结果怎样", "三款游戏现在的状态"];

    private string _input = "";
    public string Input { get => _input; set => Set(ref _input, value); }
    private bool _busy;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(StatusLine)); } }
    public string StatusLine => IsBusy ? "思考中…" : _endpoint.IsComplete ? $"在线 · {_endpoint.Model}" : "未连接 · 点右上角齿轮设置 AI 接口";

    private bool _showSettings;
    public bool ShowSettings { get => _showSettings; set { Set(ref _showSettings, value); Raise(nameof(SettingsVisibility)); } }
    public Visibility SettingsVisibility => ShowSettings ? Visibility.Visible : Visibility.Collapsed;
    public string BaseUrl { get; set; }
    public string Model { get; set; }
    /// <summary>Write-only in the UI; an empty box keeps the stored key.</summary>
    public string NewApiKey { get; set; } = "";
    private string _settingsStatus = "密钥只保存在本机，并用 Windows 账户加密；诊断包与日志不会包含它。";
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }

    public AsyncCommand SendCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public RelayCommand<string> QuickCommand { get; }

    public AssistantViewModel(ThemeAssets theme, AssistantSettingsStore settings, AssistantTools tools, Func<bool> isRunning,
        string auditDirectory, Action afterApply)
    {
        Theme = theme; Avatar = new ArtSlot(theme.Assistant); _settings = settings; _tools = tools;
        _isRunning = isRunning; _auditDirectory = auditDirectory; _afterApply = afterApply;
        _endpoint = settings.Load();
        BaseUrl = _endpoint.BaseUrl; Model = _endpoint.Model;
        _session = new AssistantSession(new ChatCompletionsClient(Http), tools, theme.AssistantName);
        SendCommand = new(() => SendAsync(Input), ex => Add(false, "出错了：" + ex.Message));
        ToggleSettingsCommand = new(() => ShowSettings = !ShowSettings);
        SaveSettingsCommand = new(SaveSettingsAsync, ex => SettingsStatus = "保存失败：" + ex.Message);
        QuickCommand = new(text => _ = SendAsync(text ?? ""));
        Add(false, $"旅行者，欢迎回来！我是{theme.AssistantName}。\n我可以帮你查看三款游戏的任务和配置、解释每个参数、把你的要求整理成修改提案（你确认后才会写入），以及查看运行结果。\n" +
            (_endpoint.IsComplete ? "有什么需要我帮忙的吗？" : "先点右上角的齿轮，填好 AI 接口地址、模型和密钥就能开始聊天啦。"));
    }

    private void Add(bool fromUser, string text, IReadOnlyList<ProposalCard>? cards = null) =>
        Messages.Add(new ChatItem(fromUser, text, cards));

    private async Task SendAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || IsBusy) return;
        Input = "";
        Add(true, text);
        if (!_endpoint.IsComplete) { Add(false, "我还没连上 AI 接口。点右上角的齿轮设置一下吧。"); ShowSettings = true; return; }
        IsBusy = true;
        try
        {
            var reply = await _session.AskAsync(_endpoint, text, CancellationToken.None);
            var cards = _session.LastProposals.Select(p => new ProposalCard(p, ConfirmAsync)).ToList();
            Add(false, reply, cards);
        }
        catch (Exception ex) when (ex is AssistantException or HttpRequestException or TaskCanceledException)
        { Add(false, "没能连上 AI 接口：" + ex.Message); }
        finally { IsBusy = false; }
    }

    private Task ConfirmAsync(ProposalCard card)
    {
        if (MessageBox.Show("将写入星铁助手的配置：\n\n" + card.Lines + "\n\n写入前自动备份，写入后读回核对。", $"{Name} · 确认修改",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return Task.CompletedTask;
        try
        {
            _tools.ApplyConfirmed(card.Id, _auditDirectory, _isRunning());
            card.State = "已写入并读回核对 ✓"; card.CanConfirm = false;
            _afterApply();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { card.State = "没有写入：" + ex.Message; }
        return Task.CompletedTask;
    }

    private Task SaveSettingsAsync()
    {
        var key = NewApiKey.Length > 0 ? NewApiKey.Trim() : _endpoint.ApiKey;
        var endpoint = new AssistantEndpoint(BaseUrl.Trim(), Model.Trim(), key);
        if (!endpoint.IsComplete) { SettingsStatus = "请填写以 https:// 开头的接口地址、模型名和密钥。"; return Task.CompletedTask; }
        _settings.Save(endpoint);
        _endpoint = endpoint; NewApiKey = ""; Raise(nameof(NewApiKey)); Raise(nameof(StatusLine));
        SettingsStatus = "已保存。可以开始聊天了。";
        ShowSettings = false;
        return Task.CompletedTask;
    }
}
