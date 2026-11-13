using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using VIvian.App;
using VIvian.Core.Execution;
using VIvian.Core;
using VIvian.Core.Engines;
using VIvian.Infrastructure.Assistant;
using VIvian.Infrastructure;
using VIvian.Infrastructure.Accounts;
using VIvian.Infrastructure.Simulation;
using Xunit;

namespace VIvian.Tests.App;

public sealed class FurinaWindowLayoutTests
{
    [Fact]
    public void ActualWindowContentRendersCompactAndWideWithoutShowingOrActivatingDesktopWindows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { InspectWindows(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Window content render timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void InspectWindows()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "VIvian.App"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var root = directory.FullName;
        var output = Path.Combine(root, "artifacts", "tests-b", "visual");
        Directory.CreateDirectory(output);
        var fixture = Directory.CreateTempSubdirectory("furina-ui-fixture-");
        Application? app = null;
        MainWindow? main = null;
        AssistantWindow? assistant = null;
        try
        {
            // Load the owning App resource collection without running App startup or showing a HWND.
            var appMarkup = XDocument.Load(Path.Combine(root, "src", "VIvian.App", "App.xaml"));
            XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = appMarkup.Root!.Element(wpf + "Application.Resources")!.Element(wpf + "ResourceDictionary")!;
            resources.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var markup = resources.ToString().Replace("Source=\"Themes/Furina/Theme.xaml\"", "Source=\"/VIvian;component/Themes/Furina/Theme.xaml\"");
            app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = (ResourceDictionary)XamlReader.Parse(markup) };
            var db = new LocalDatabase(Path.Combine(fixture.FullName, "vivian.db"));
            db.InitializeAsync().GetAwaiter().GetResult();
            var paths = new AppPaths(fixture.FullName);
            var supervisor = new RefusingProcessSupervisor();
            var queue = new QueueViewModel(new QueueScheduler(db, supervisor), db, paths);
            var accounts = new AccountViewModel(db, new AccountRecognitionService(db, new LoginScreenReader()), NoScanWindowShield.Instance);
            var vm = new MainViewModel(db, db, db, db, paths, queue, supervisor, accounts,
                new MultiAccountOrchestrator(db, null!), new GamePathProvider());
            main = new MainWindow(vm);
            Assert.Equal(4, ((System.Windows.Controls.Primitives.UniformGrid)main.FindName("HomeSummary")).Columns);
            var home = (FrameworkElement)main.Content;
            foreach (var (width, height) in new[] { (1600, 900), (1280, 800), (1440, 900), (960, 500) })
            {
                main.Width = width; main.Height = height; main.UpdateHomeLayout();
                Assert.Equal(width < 1280, main.IsCompactLayout);
                Render(home, width, height, Path.Combine(output, $"home-content-{width}x{height}.png"));
                Assert.Equal(width < 1500 ? 2 : 4, ((System.Windows.Controls.Primitives.UniformGrid)main.FindName("HomeSummary")).Columns);                var stop = (Button)main.FindName("PersistentStopButton");
                var stopBounds = stop.TransformToAncestor(home).TransformBounds(new Rect(stop.RenderSize));
                Assert.True(stopBounds.Bottom <= height && stopBounds.Right <= width, $"Stop clipped: {stopBounds}");
                if (width >= 1280)
                {
                    var character = (Button)main.FindName("HomeCharacterPanel");
                    var point = character.TranslatePoint(new Point(character.ActualWidth / 2, character.ActualHeight / 2), home);
                    DependencyObject? hit = null;
                    VisualTreeHelper.HitTest(home,
                        node => node is UIElement { IsHitTestVisible: false } ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
                        result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
                    while (hit is not null && hit != character) hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(character, hit);
                }
            }
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var width = (int)(1920 / scale); var height = (int)(1000 / scale);
                main.Width = width; main.Height = height; main.UpdateHomeLayout();
                VisualTreeHelper.SetRootDpi(main, new DpiScale(scale, scale));
                Render(home, width, height, Path.Combine(output, $"home-dpi-{scale * 100:0}.png"), scale);
                var stop = (Button)main.FindName("PersistentStopButton");
                var bounds = stop.TransformToAncestor(home).TransformBounds(new Rect(stop.RenderSize));
                Assert.True(new Rect(0, 0, width, height).Contains(bounds), $"Stop at {scale}: {bounds}");
            }
            VisualTreeHelper.SetRootDpi(main, new DpiScale(1, 1));
            main.Width = 1600; main.Height = 900; main.UpdateHomeLayout();
            var navigation = (TabControl)main.FindName("Navigation");
            for (int tab = 0; tab < navigation.Items.Count; tab++)
            {
                navigation.SelectedIndex = tab;
                Render(home, 1600, 900, Path.Combine(output, $"page-{tab}-1600x900.png"));
                AuditOffscreenText(home);
            }
            navigation.SelectedIndex = 0;
            vm.ReportAssistantInputBlocked("测试提示：运行中暂不能进入聊天。");
            FlushBindings();
            Assert.Equal(vm.Status, ((TextBlock)main.FindName("PersistentStatusText")).Text);
            var gate = new FakeGate();
            assistant = new AssistantWindow(vm.Assistant, gate, vm);
            var input = (TextBox)assistant.FindName("InputBox");
            Assert.False(assistant.ShowActivated);
            Assert.False(assistant.Topmost);
            Assert.False(input.IsEnabled);
            Assert.Equal(0, gate.Acquisitions);
            Assert.True(assistant.Interaction.TryEnter());
            FlushBindings();
            Render((FrameworkElement)assistant.Content, 460, 720, Path.Combine(output, "assistant-reference-460x720.png"));
            AuditOffscreenText((FrameworkElement)assistant.Content);
            Assert.True(input.IsEnabled);
            assistant.LockInput();
            FlushBindings();
            Assert.False(input.IsEnabled);
            Assert.Equal(1, gate.Releases);
            Assert.True(assistant.Interaction.TryEnter());
            FlushBindings();
            Assert.True(input.IsEnabled); // Releasing must not destroy the IsEnabled binding.
            queue.Unresolved.Add(new RunRow(Guid.NewGuid(), "模拟恢复待确认", "", false, true));
            FlushBindings();
            Assert.False(input.IsEnabled);
            Assert.Equal(2, gate.Releases);
            queue.Unresolved.Clear();
            Assert.True(assistant.Interaction.TryEnter());
            FlushBindings();
            vm.Assistant.Input = "离屏验证文本：多行输入需要保留换行与滚动。\n" + new string('测', 160);
            vm.Assistant.Messages.Add(new ChatItem(false, "布局验证示例，非执行回执。" + new string('长', 220)));
            FlushBindings();
            Render((FrameworkElement)assistant.Content, 460, 720, Path.Combine(output, "assistant-content-460x720.png"));
            Render((FrameworkElement)assistant.Content, 360, 460, Path.Combine(output, "assistant-content-360x460.png"));
            vm.Assistant.ToggleSettingsCommand.Execute(null);
            FlushBindings();
            Render((FrameworkElement)assistant.Content, 360, 460, Path.Combine(output, "assistant-settings-360x460.png"));
            var inputBounds = input.TransformToAncestor((Visual)assistant.Content).TransformBounds(new Rect(input.RenderSize));
            Assert.True(inputBounds.Bottom <= 460 && inputBounds.Top >= 0, $"Input clipped: {inputBounds}");
            vm.Assistant.ToggleSettingsCommand.Execute(null);
            FlushBindings();
            ((Expander)assistant.FindName("QuickCommandsExpander")).IsExpanded = true;
            Render((FrameworkElement)assistant.Content, 360, 460, Path.Combine(output, "assistant-quick-expanded-360x460.png"));
            Assert.True(((ScrollViewer)assistant.FindName("ChatScroll")).ViewportHeight >= 22, $"Expanded quick commands leave no readable message line: {((ScrollViewer)assistant.FindName("ChatScroll")).ViewportHeight}.");
            var send = (Button)assistant.FindName("SendButton");
            var sendBounds = send.TransformToAncestor((Visual)assistant.Content).TransformBounds(new Rect(send.RenderSize));
            Assert.True(sendBounds.Bottom <= 452, $"Expanded quick commands hide Send: {sendBounds}");
            Assert.NotNull(input.Template.FindName("PART_ContentHost", input));
            Assert.True(input.AcceptsReturn);
            Assert.True(input.MaxHeight < double.PositiveInfinity);
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                VisualTreeHelper.SetRootDpi(assistant, new DpiScale(scale, scale));
                Render((FrameworkElement)assistant.Content, 360, 460, Path.Combine(output, $"assistant-dpi-{scale * 100:0}.png"), scale);
                var bounds = send.TransformToAncestor((Visual)assistant.Content).TransformBounds(new Rect(send.RenderSize));
                Assert.True(bounds.Bottom <= 452, $"Send at {scale}: {bounds}");
                Assert.True(((ScrollViewer)assistant.FindName("ChatScroll")).ViewportHeight >= 22);
            }
            VisualTreeHelper.SetRootDpi(assistant, new DpiScale(1, 1));
            Render((FrameworkElement)assistant.Content, 360, 400, Path.Combine(output, "assistant-small-workarea-360x400.png"));
            var tinySendBounds = send.TransformToAncestor((Visual)assistant.Content).TransformBounds(new Rect(send.RenderSize));
            Assert.True(tinySendBounds.Bottom <= 392, $"Small work area hides Send: {tinySendBounds}");
            Assert.True(((ScrollViewer)assistant.FindName("ChatScroll")).ViewportHeight >= 22, "Tiny work area hides every message line.");
            // Explicitly synthetic evidence for Q; no confirmation or execution is invoked here.
            vm.Assistant.Input = "";
            vm.Assistant.Messages.Clear();
            var demoTarget = new AccountConfigurationTarget(GameId.StarRail, Guid.Parse("00000000-0000-0000-0000-000000000001"));
            var demoCard = new ProposalCard(Guid.NewGuid(), demoTarget, "演示账号组",
                "1. 演示账号 A：每日实训\n2. 演示账号 B：体力与委托\n按以上顺序展示；不会启动游戏。",
                "有序计划 · 合成演示", "确认（仅展示）", _ => Task.CompletedTask);
            vm.Assistant.Messages.Add(new ChatItem(false, "离屏合成样例，不是执行回执。", [demoCard]));
            ((Expander)assistant.FindName("QuickCommandsExpander")).IsExpanded = false;
            FlushBindings();
            Render((FrameworkElement)assistant.Content, 460, 800, Path.Combine(output, "assistant-ordered-plan-DEMO-460x800.png"));
            vm.Assistant.Messages.Clear();
            vm.Assistant.Messages.Add(new ChatItem(false,
                "离屏合成样例，不是执行回执。\n\n这里展示较长说明在助手中的换行与滚动。运行前可以核对账号顺序、任务内容和游戏环境；待确认卡只表示计划仍在等待确认，并不表示任务已经开始或完成。\n\n" +
                string.Concat(Enumerable.Repeat("如需调整顺序，请先返回账号管理检查已勾选的账号。运行记录应来自真实结果，未获得回执时应保留未知状态。\n\n", 4))));
            FlushBindings();
            Render((FrameworkElement)assistant.Content, 460, 720, Path.Combine(output, "assistant-long-message-DEMO-460x720.png"));
            assistant.Close();
            var confirmationGate = new FakeGate();
            var submissions = 0;
            var tools = new AssistantTools(() => [], _ => Task.FromResult<IReadOnlyList<RunRecord>>([])) { ScheduleProposalsEnabled = true };
            var confirmingModel = new AssistantViewModel(new ThemeAssets(fixture.FullName, fixture.FullName), new AssistantSettingsStore(fixture.FullName),
                tools, _ => true, fixture.FullName, () => Task.CompletedTask, _ =>
                {
                    Assert.False(assistant!.Interaction.CanEdit);
                    Assert.False(((TextBox)assistant.FindName("InputBox")).IsEnabled);
                    Assert.Equal(1, confirmationGate.Releases);
                    submissions++;
                    return Task.FromResult("模拟已接收，非真实执行");
                }, (_, _, _) => true);
            assistant = new AssistantWindow(confirmingModel, confirmationGate);
            Assert.True(assistant.Interaction.TryEnter());
            FlushBindings();
            var target = new AccountConfigurationTarget(GameId.StarRail, Guid.NewGuid());
            var plan = new AssistantRunPlan(Guid.NewGuid(), target, "隔离样例", "revision", 1, "saved", DateTimeOffset.UtcNow.AddMinutes(1));
            var card = new ProposalCard(plan.Id, target, "隔离样例", "模拟计划", "确认测试", "确认", _ => Task.CompletedTask);
            confirmingModel.ConfirmRunAsync(card, plan).GetAwaiter().GetResult();
            confirmingModel.ConfirmRunAsync(card, plan).GetAwaiter().GetResult();
            Assert.Equal(1, submissions);
        }
        finally
        {
            assistant?.Close(); main?.Close(); app?.Shutdown();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestDirectories.DeleteWithRetry(fixture.FullName);
        }
    }

    private static void FlushBindings() => System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
        () => { }, System.Windows.Threading.DispatcherPriority.DataBind);

    private static void AuditOffscreenText(FrameworkElement root)
    {
        // Reuse the owning audit's color resolution/threshold. Only native IsVisible is unavailable without Show.
        var background = typeof(ContrastAudit).GetMethod("BackgroundBehind", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(background);
        int checkedCount = 0;
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var item))
        {
            if (item is UIElement { Visibility: not Visibility.Visible }) continue;
            if (item is TextBlock { IsEnabled: true, ActualWidth: > 0, ActualHeight: > 0 } text
                && !string.IsNullOrWhiteSpace(text.Text)
                && text.Foreground is SolidColorBrush { Color.A: > 200 } fg
                && background.Invoke(null, [text]) is Color bg)
            {
                checkedCount++;
                Assert.True(ContrastAudit.Ratio(fg.Color, bg) >= ContrastAudit.MinimumRatio,
                    $"Offscreen contrast {text.Text}: {fg.Color} on {bg}");
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) pending.Push(VisualTreeHelper.GetChild(item, i));
        }
        Assert.True(checkedCount >= 5, "Offscreen audit checked no meaningful text.");
    }

    private static void Render(FrameworkElement element, int width, int height, string path, double scale = 1)
    {
        element.Width = double.NaN; element.Height = double.NaN;
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Round(width * scale), (int)Math.Round(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private sealed class FakeGate : IAssistantInputGate
    {
        public int Acquisitions, Releases;
        public bool TryAcquire(out IDisposable? permission, out string? reason)
        {
            Acquisitions++; permission = new Release(() => Releases++); reason = null; return true;
        }
        private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }
    }
}
