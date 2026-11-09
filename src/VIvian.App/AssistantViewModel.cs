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
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(90),
        MaxResponseContentBufferSize = 1024 * 1024,
    };
    private readonly ChatCompletionsClient _client = new(Http);
    private CancellationTokenSource? _request;
    private string? _connectionResult;
    private int _settingsRevision;
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
    public string StatusLine => !DraftEndpoint.IsComplete ? "未配置" : _connectionResult ?? "未测试";

    private bool _showSettings;
    public bool ShowSettings { get => _showSettings; set { Set(ref _showSettings, value); Raise(nameof(SettingsVisibility)); } }
    public Visibility SettingsVisibility => ShowSettings ? Visibility.Visible : Visibility.Collapsed;
    private string _baseUrl;
    public string BaseUrl { get => _baseUrl; set { if (_baseUrl == value) return; Set(ref _baseUrl, value); InvalidateConnection(); } }
    private string _model;
    public string Model { get => _model; set { if (_model == value) return; Set(ref _model, value); InvalidateConnection(); } }
    private string _newApiKey = "";
    public string NewApiKey { get => _newApiKey; set { if (_newApiKey == value) return; Set(ref _newApiKey, value); InvalidateConnection(); } }
    private AssistantEndpoint DraftEndpoint => new(BaseUrl.Trim(), Model.Trim(),
        NewApiKey.Length == 0 ? _endpoint.ApiKey : NewApiKey.Trim());
    private string _settingsStatus = "密钥只保存在本机，并用 Windows 账户加密；诊断包与日志不会包含它。";
    public string SettingsStatus { get => _settingsStatus; private set => Set(ref _settingsStatus, value); }

    public AsyncCommand SendCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public AsyncCommand SaveSettingsCommand { get; }
    public AsyncCommand TestConnectionCommand { get; }
    public RelayCommand CancelRequestCommand { get; }
    public RelayCommand<string> QuickCommand { get; }

    public AssistantViewModel(ThemeAssets theme, AssistantSettingsStore settings, AssistantTools tools, Func<bool> isRunning,
        string auditDirectory, Action afterApply)
    {
        Theme = theme; Avatar = new ArtSlot(theme.Assistant); _settings = settings; _tools = tools;
        _isRunning = isRunning; _auditDirectory = auditDirectory; _afterApply = afterApply;
        _endpoint = settings.Load();
        _baseUrl = _endpoint.BaseUrl; _model = _endpoint.Model;
        _session = new AssistantSession(_client, tools, theme.AssistantName);
        SendCommand = new(() => SendAsync(Input), _ => Add(false, "助手请求失败，可以重试。"));
        ToggleSettingsCommand = new(() => ShowSettings = !ShowSettings);
        SaveSettingsCommand = new(SaveSettingsAsync, _ => SettingsStatus = "保存失败，请检查本机数据目录权限。");
        TestConnectionCommand = new(TestConnectionAsync, _ => SettingsStatus = "测试失败，请检查接口设置。");
        CancelRequestCommand = new(CancelRequest);
        QuickCommand = new(text => { Input = text ?? ""; SendCommand.Execute(null); });
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
        if (!_endpoint.IsComplete || DraftEndpoint != _endpoint)
        {
            Add(false, "请先保存 AI 接口设置。");
            ShowSettings = true;
            return;
        }
        using var request = new CancellationTokenSource();
        _request = request;
        IsBusy = true;
        try
        {
            var reply = await _session.AskAsync(_endpoint, text, request.Token);
            if (request.IsCancellationRequested) return;
            var cards = _session.LastProposals.Select(p => new ProposalCard(p, ConfirmAsync)).ToList();
            Add(false, reply, cards);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (OperationCanceledException) { Add(false, "助手请求超时，可以重试。"); }
        catch (AssistantException ex) { Add(false, ex.Message); }
        finally { _request = null; IsBusy = false; }
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
        if (IsBusy) return Task.CompletedTask;
        var endpoint = DraftEndpoint;
        if (!endpoint.IsComplete)
        {
            SettingsStatus = "请填写有效的接口地址、模型名和密钥。远程接口需要 HTTPS。";
            return Task.CompletedTask;
        }
        _settings.Save(endpoint);
        if (endpoint != _endpoint)
            _session = new AssistantSession(_client, _tools, Name);
        _endpoint = endpoint;
        _newApiKey = "";
        Raise(nameof(NewApiKey));
        Raise(nameof(StatusLine));
        SettingsStatus = "设置已保存。";
        return Task.CompletedTask;
    }

    public void CancelRequest() => _request?.Cancel();

    private void InvalidateConnection()
    {
        _settingsRevision++;
        _connectionResult = null;
        SettingsStatus = "连接参数已变更，请重新测试。";
        Raise(nameof(StatusLine));
        CancelRequest();
    }

    private async Task TestConnectionAsync()
    {
        if (IsBusy) return;
        var endpoint = DraftEndpoint;
        if (!endpoint.IsComplete)
        {
            SettingsStatus = "请填写有效的接口地址、模型名和密钥。远程接口需要 HTTPS。";
            return;
        }
        var revision = _settingsRevision;
        using var request = new CancellationTokenSource();
        _request = request;
        IsBusy = true;
        _connectionResult = "测试中…";
        SettingsStatus = "正在请求所填模型…";
        Raise(nameof(StatusLine));
        try
        {
            await _client.TestConnectionAsync(endpoint, request.Token);
            if (revision == _settingsRevision && !request.IsCancellationRequested)
            {
                _connectionResult = "测试成功";
                SettingsStatus = "模型已响应。保存设置后即可聊天。";
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (AssistantException ex)
        {
            if (revision == _settingsRevision && !request.IsCancellationRequested)
            {
                _connectionResult = "测试失败";
                SettingsStatus = ex.Message;
            }
        }
        finally
        {
            if (revision == _settingsRevision && _connectionResult == "测试中…")
            {
                _connectionResult = null;
                SettingsStatus = request.IsCancellationRequested ? "测试已取消。" : "测试失败，请重试。";
            }
            _request = null;
            IsBusy = false;
        }
    }
}
