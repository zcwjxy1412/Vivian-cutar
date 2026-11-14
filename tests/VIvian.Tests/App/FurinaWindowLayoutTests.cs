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
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "Window content render timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void InspectStatusActions(MainWindow main, MainViewModel vm, string fixture, string output)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var message = (Button)main.FindName("StatusMessageButton");
        var recheck = (Button)main.FindName("RecheckStatusButton");
        Assert.True(ToolTipService.GetShowOnDisabled(recheck));
        ((FrameworkElement)main.Content).Measure(new Size(1600, 900));
        ((FrameworkElement)main.Content).Arrange(new Rect(0, 0, 1600, 900));
        vm.ShowStatus("DEMO 未分类错误"); FlushBindings();
        Assert.False(message.IsEnabled);
        main.RunStatusActionAsync().GetAwaiter().GetResult();
        Assert.Equal("DEMO 未分类错误", vm.Status);
        foreach (var (name, action, check) in new[] {
            ("RecoverDesktopCommand", HomeStatusAction.RecoverDesktop, false),
            ("RestoreConfigurationCommand", HomeStatusAction.RestoreConfiguration, false),
            ("CheckEnginesCommand", HomeStatusAction.None, true) })
        {
            var field = typeof(MainViewModel).GetField("<" + name + ">k__BackingField", hidden)!;
            var original = field.GetValue(vm);
            var release = new TaskCompletionSource(); var calls = 0;
            var command = new AsyncCommand(async () => { calls++; await release.Task; vm.ShowStatus("DEMO 仍需人工核对", HomeStatusAction.ReviewRuns); }, ex => throw ex);
            field.SetValue(vm, command);
            try
            {
                vm.ShowStatus("DEMO 等待处理", action); FlushBindings();
                var pending = main.RunStatusActionAsync(check);
                Assert.False(pending.IsCompleted);
                Assert.True(main.IsStatusActionRunning);
                FlushBindings(); Assert.False(recheck.IsEnabled); Assert.False(message.IsEnabled);
                main.RunStatusActionAsync(check).GetAwaiter().GetResult();
                Assert.Equal(1, calls); Assert.Equal("DEMO 等待处理", vm.Status);
                release.SetResult();
                for (int i = 0; i < 20 && !pending.IsCompleted; i++) FlushBindings();
                Assert.True(pending.IsCompleted); pending.GetAwaiter().GetResult();
                Assert.False(main.IsStatusActionRunning); Assert.Equal("DEMO 仍需人工核对", vm.Status);
                FlushBindings(); Assert.True(message.IsEnabled); Assert.False(recheck.IsEnabled);
                main.RunStatusActionAsync(recheck: true).GetAwaiter().GetResult();
                Assert.Equal("DEMO 仍需人工核对", vm.Status);
            }
            finally { release.TrySetResult(); field.SetValue(vm, original); }
        }
        var checkField = typeof(MainViewModel).GetField("<CheckEnginesCommand>k__BackingField", hidden)!;
        var savedCheck = checkField.GetValue(vm);
        try
        {
            vm.ShowStatus("DEMO 未分类错误");
            checkField.SetValue(vm, new AsyncCommand(() => Task.FromException(new IOException("DEMO failure")),
                _ => vm.ShowStatus("DEMO 检查失败，需人工核对", HomeStatusAction.RestoreConfiguration)));
            main.RunStatusActionAsync(recheck: true).GetAwaiter().GetResult();
            Assert.False(main.IsStatusActionRunning);
            Assert.Equal("DEMO 检查失败，需人工核对", vm.Status);
            FlushBindings(); Assert.True(message.IsEnabled);
        }
        finally { checkField.SetValue(vm, savedCheck); }
        // Real command + real recovery manager, with only an isolated corrupt recovery record.
        var recovery = Path.Combine(fixture, "configuration-recovery", "temporary-config-recovery.json");
        Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
        File.WriteAllText(recovery, "{DEMO invalid recovery json");
        try
        {
            vm.ShowStatus("DEMO 临时配置待恢复", HomeStatusAction.RestoreConfiguration);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var pending = main.RunStatusActionAsync();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!pending.IsCompleted && DateTime.UtcNow < deadline) { FlushBindings(); Thread.Sleep(1); }
                Assert.True(pending.IsCompleted); pending.GetAwaiter().GetResult();
                FlushBindings();
                Assert.Contains("恢复记录无法读取", vm.Status);
                Assert.Equal(HomeStatusAction.RestoreConfiguration, vm.StatusAction);
                Assert.True(message.IsEnabled); Assert.False(main.IsStatusActionRunning);
                Assert.True(File.Exists(recovery));
            }
        }
        finally { File.Delete(recovery); }
        Render((FrameworkElement)main.Content, 1600, 900, Path.Combine(output, "status-recovery-blocked.png"));
        vm.ShowStatus("DEMO 待清理", HomeStatusAction.ReviewRuns);
        Assert.Equal("查看运行日志", vm.StatusActionText);
        vm.Queue.Unresolved.Add(new RunRow(Guid.NewGuid(), "DEMO 未确认记录", "DEMO", true, true));
        typeof(ObservableObject).GetMethod("Raise", hidden)!.Invoke(vm.Queue, [nameof(QueueViewModel.HasUnresolved)]);
        FlushBindings(); Assert.Equal("核对待清理记录", ((Button)main.FindName("StatusActionButton")).Content);
        main.RunStatusActionAsync().GetAwaiter().GetResult();
        Assert.Same(main.FindName("RunLogTab"), ((TabControl)main.FindName("Navigation")).SelectedItem);
        Assert.Single(vm.Queue.Unresolved); // Navigation never acknowledges cleanup.
        Render((FrameworkElement)main.Content, 1600, 900, Path.Combine(output, "status-pending-cleanup.png"));
        Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("PendingRunCleanupPanel")).Visibility);
        vm.Queue.Unresolved.Clear();
        typeof(ObservableObject).GetMethod("Raise", hidden)!.Invoke(vm.Queue, [nameof(QueueViewModel.HasUnresolved)]);
        FlushBindings(); Assert.Equal("查看运行日志", ((Button)main.FindName("StatusActionButton")).Content);
        vm.ShowStatus("DEMO 选择账号", HomeStatusAction.SelectStarRailAccounts); FlushBindings();
        main.RunStatusActionAsync().GetAwaiter().GetResult();
        Assert.Same(main.FindName("AccountManagementTab"), ((TabControl)main.FindName("Navigation")).SelectedItem);
        typeof(MainViewModel).GetProperty("BatchRunning")!.SetValue(vm, true); FlushBindings();
        Assert.False(recheck.IsEnabled); Assert.False(message.IsEnabled);
        ((TabControl)main.FindName("Navigation")).SelectedItem = main.FindName("HomeTab");
        main.RunStatusActionAsync().GetAwaiter().GetResult();
        Assert.Same(main.FindName("HomeTab"), ((TabControl)main.FindName("Navigation")).SelectedItem);
        typeof(MainViewModel).GetProperty("BatchRunning")!.SetValue(vm, false);
        typeof(MainViewModel).GetMethod("Busy", hidden)!.Invoke(vm, [true]); FlushBindings();
        Assert.False(recheck.IsEnabled);
        typeof(MainViewModel).GetMethod("Busy", hidden)!.Invoke(vm, [false]);
        vm.ShowStatus("就绪 · DEMO 状态入口检查完成"); FlushBindings();
        SynchronizationContext.SetSynchronizationContext(previousContext);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
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
            Assert.NotNull(main.FindName("StatusMessageButton"));
            Assert.NotNull(main.FindName("RecheckStatusButton"));
            InspectStatusActions(main, vm, fixture.FullName, output);
            Assert.Equal(256, Assert.IsAssignableFrom<BitmapSource>(main.Icon).PixelWidth);
            var repair = (Expander)main.FindName("ReleaseRepairExpander");
            Assert.False(repair.IsExpanded);
            ((TextBlock)main.FindName("PrivilegeValue")).Text = "管理员：打开时已授权，批次内不再弹窗";
            Assert.Equal(4, ((System.Windows.Controls.Primitives.UniformGrid)main.FindName("HomeSummary")).Columns);
            var home = (FrameworkElement)main.Content;
            foreach (var (width, height) in new[] { (1600, 900), (1280, 800), (1440, 900), (960, 500) })
            {
                main.Width = width; main.Height = height; main.UpdateHomeLayout();
                Assert.Equal(width < 1280, main.IsCompactLayout);
                Render(home, width, height, Path.Combine(output, $"home-content-{width}x{height}.png"));
                var label = (TextBlock)main.FindName("PrivilegeLabel");
                var value = (TextBlock)main.FindName("PrivilegeValue");
                Assert.True(label.TranslatePoint(new Point(label.ActualWidth, 0), home).X <= value.TranslatePoint(new Point(), home).X);
                Assert.Equal(3, Descendants(home).OfType<FurinaRoundedClipBorder>().Count());
                AssertMedallionTextContrast((Button)main.FindName("DailyRunButton"), Path.Combine(root, "src", "VIvian.App", "Assets", "Furina", "run-medallion.png"));
                Assert.Equal(1536, Assert.IsAssignableFrom<BitmapSource>(((FurinaOrnament)main.FindName("BrandCrown")).WaterImage).PixelWidth);
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
            navigation.SelectedItem = main.FindName("EngineEnvironmentTab");
            repair.IsExpanded = true;
            Render(home, 1600, 900, Path.Combine(output, "release-repair-expanded.png"));
            Assert.Contains(Descendants(repair).OfType<Button>(), button => ReferenceEquals(button.Command, vm.VerifyReleaseCommand));
            Assert.Contains(Descendants(repair).OfType<TextBox>(), box => System.Windows.Data.BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "ReleaseArchivePath");
            var engineScroll = (ScrollViewer)((TabItem)navigation.SelectedItem).Content;
            engineScroll.ScrollToBottom(); home.UpdateLayout();
            Render(home, 1600, 900, Path.Combine(output, "release-repair-expanded-bottom.png"));
            repair.IsExpanded = false;
            engineScroll.ScrollToBottom(); home.UpdateLayout();
            Render(home, 1600, 900, Path.Combine(output, "release-repair-collapsed-bottom.png"));
            engineScroll.ScrollToTop();
            // Every navigation page and each game/account subpage at a fixed physical budget.
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var width = (int)(1920 / scale); var height = (int)(1000 / scale);
                main.Width = width; main.Height = height; main.UpdateHomeLayout();
                VisualTreeHelper.SetRootDpi(main, new DpiScale(scale, scale));
                for (int tab = 0; tab < navigation.Items.Count; tab++)
                {
                    navigation.SelectedIndex = tab;
                    var children = tab == 1 ? (TabControl)main.FindName("GameTabs")
                        : tab == 2 ? (TabControl)main.FindName("AccountTabs") : null;
                    for (int child = 0; child < (children?.Items.Count ?? 1); child++)
                    {
                        if (children is not null) children.SelectedIndex = child;
                        Render(home, width, height, Path.Combine(output, $"court-page-{tab}-sub-{child}-dpi-{scale * 100:0}.png"), scale);
                        AuditOffscreenText(home);
                        var stop = (Button)main.FindName("PersistentStopButton");
                        var bounds = stop.TransformToAncestor(home).TransformBounds(new Rect(stop.RenderSize));
                        Assert.True(new Rect(0, 0, width, height).Contains(bounds), $"Stop clipped on page {tab}/{child} at {scale}: {bounds}");
                        if (tab == 1 && child < 2)
                        {
                            var pageScroll = (ScrollViewer)main.FindName(child == 0 ? "StarRailPageScroll" : "GenshinPageScroll");
                            pageScroll.ScrollToBottom(); home.UpdateLayout();
                            Assert.True(Math.Abs(pageScroll.VerticalOffset - pageScroll.ScrollableHeight) < 1, "Game page bottom is unreachable.");
                            Render(home, width, height, Path.Combine(output, $"court-page-{tab}-sub-{child}-dpi-{scale * 100:0}-bottom.png"), scale);
                            pageScroll.ScrollToTop(); home.UpdateLayout();
                        }
                    }
                    if (children is not null) children.SelectedIndex = 0;
                }
            }
            VisualTreeHelper.SetRootDpi(main, new DpiScale(1, 1));
            main.Width = 1600; main.Height = 900; main.UpdateHomeLayout();
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
            Assert.Equal(1536, Assert.IsAssignableFrom<BitmapSource>(((FurinaOrnament)assistant.FindName("AssistantCrown")).WaterImage).PixelWidth);
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
            AssertInputLinesVisible(input);
            vm.Assistant.ToggleSettingsCommand.Execute(null);
            FlushBindings();
            Render((FrameworkElement)assistant.Content, 360, 400, Path.Combine(output, "assistant-tiny-settings-input.png"));
            AssertInputLinesVisible(input);
            vm.Assistant.ToggleSettingsCommand.Execute(null);
            FlushBindings();
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
            vm.Assistant.Input = "首行中文可读：请核对账号。\n" + new string('测', 160) + "\n末行中文可读：不会执行。";
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                VisualTreeHelper.SetRootDpi(assistant, new DpiScale(scale, scale));
                foreach (var state in new[] { "message", "plan", "settings" })
                {
                    if (state == "plan")
                    {
                        vm.Assistant.Messages.Clear();
                        var longCard = new ProposalCard(Guid.NewGuid(), demoTarget, "离屏演示账号",
                            string.Concat(Enumerable.Range(1, 20).Select(n => $"{n}. 合成任务：只用于滚动布局验证。\n")),
                            "长计划 · 合成演示", "确认（仅展示）", _ => throw new InvalidOperationException("Demo must never execute."));
                        vm.Assistant.Messages.Add(new ChatItem(false, "仅验证排版，不会执行。", [longCard]));
                    }
                    if (state == "message")
                    {
                        vm.Assistant.Messages.Clear();
                        vm.Assistant.Messages.Add(new ChatItem(false, "合成长消息，非执行回执。\n" + string.Concat(Enumerable.Repeat("中文长消息换行与滚动验证。\n", 30))));
                    }
                    if (state == "settings") vm.Assistant.ToggleSettingsCommand.Execute(null);
                    FlushBindings();
                    var assistantSurface = (FrameworkElement)assistant.Content;
                    var path = Path.Combine(output, $"assistant-tiny-{state}-{scale * 100:0}.png");
                    Render(assistantSurface, 360, 400, path, scale);
                    AssertInputLinesVisible(input);
                    var scroll = (ScrollViewer)assistant.FindName(state == "settings" ? "SettingsScroll" : "ChatScroll");
                    var contentBounds = scroll.TransformToAncestor(assistantSurface).TransformBounds(new Rect(scroll.RenderSize));
                    var editorBounds = input.TransformToAncestor(assistantSurface).TransformBounds(new Rect(input.RenderSize));
                    Assert.True(scroll.ViewportHeight >= 22);
                    Assert.True(contentBounds.Bottom <= editorBounds.Top, $"{state} overlaps input: {contentBounds} / {editorBounds}");
                    scroll.ScrollToEnd(); assistantSurface.UpdateLayout();
                    Assert.True(scroll.ScrollableHeight > 0, $"{state} fixture did not exercise scrolling.");
                    Assert.True(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, $"{state} bottom unreachable.");
                    AssertInputLinesVisible(input);
                    Render(assistantSurface, 360, 400, path.Replace(".png", "-end.png"), scale);
                    if (state != "message")
                    {
                        var pending = new Stack<DependencyObject>(); pending.Push(scroll);
                        Button? action = null;
                        while (pending.TryPop(out var visual))
                        {
                            if (visual is Button b && Equals(b.Content, state == "settings" ? "保存" : "确认（仅展示）")) { action = b; break; }
                            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++) pending.Push(VisualTreeHelper.GetChild(visual, i));
                        }
                        Assert.NotNull(action);
                        action.BringIntoView(); assistantSurface.UpdateLayout();
                        var actionBounds = action.TransformToAncestor(scroll).TransformBounds(new Rect(action.RenderSize));
                        Assert.True(actionBounds.Top >= -1 && actionBounds.Bottom <= scroll.ActualHeight + 1,
                            $"{state} action cannot be fully shown: {actionBounds} / {scroll.ActualHeight}");
                        AssertInputLinesVisible(input);
                        Render(assistantSurface, 360, 400, path.Replace(".png", "-action.png"), scale);
                    }
                    if (state == "settings") vm.Assistant.ToggleSettingsCommand.Execute(null);
                }
            }
            RenderPopulatedDemo(main, vm, output);
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

    private static void RenderPopulatedDemo(MainWindow main, MainViewModel vm, string output)
    {
        // Synthetic display state only: bypass selection setters so no engine/file reads or saves run.
        static void SeedField(object target, string name, object value) =>
            (target.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException(name)).SetValue(target, value);
        static void SeedStatus(object target, string name, string value) => target.GetType().GetProperty(name)!.SetValue(target, value);
        static void RefreshDemoBindings(ObservableObject target, params string[] names)
        {
            var raise = typeof(ObservableObject).GetMethod("Raise", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            foreach (var name in names) raise.Invoke(target, [name]);
        }
        static GameAccount Account(GameId game, int order) => new(Guid.NewGuid(), game, $"DEMO · {game} 账号 {order + 1}",
            "国服", null, "", true, false, order, "DEMO 未登录", "unverified", null, "DEMO 仅排版样例", 1);
        foreach (var game in new[] { GameId.StarRail, GameId.Genshin, GameId.ZenlessZoneZero })
        for (int i = 0; i < 3; i++)
        {
            var account = Account(game, i);
            if (game == GameId.StarRail) vm.Accounts.Rows.Add(new(account, null, new(account.Id, true, 1)));
            else if (game == GameId.Genshin) vm.GenshinConfiguration.Accounts.Add(account);
            else vm.Accounts.ZenlessRows.Add(account);
        }
        SeedField(vm.Accounts, "_configurationRow", vm.Accounts.Rows[0]);
        vm.Accounts.SelectedRow = vm.Accounts.Rows[0];
        foreach (var field in EngineCatalog.March7th.Parameters)
            vm.Configuration.Parameters.Add(new(new(field, field.DefaultValue, "DEMO 已载入默认值")));
        SeedStatus(vm.Configuration, "Status", "DEMO · 全部配置字段已载入；仅隔离排版，不连接引擎或保存。");
        var genshinValues = VIvian.Infrastructure.Engines.BetterGi.BetterGiAccountPlans.Parameters
            .Select(field => new ConfigurationValue(field, field.DefaultValue, "DEMO 已载入默认值")).ToArray();
        foreach (var value in genshinValues) vm.GenshinConfiguration.Parameters.Add(new(value));
        var genshin = vm.GenshinConfiguration.Accounts[0];
        SeedField(vm.GenshinConfiguration, "_selectedAccount", genshin);
        SeedField(vm.GenshinConfiguration, "_snapshot", new AccountConfigurationView(new(GameId.Genshin, genshin.Id), genshin.Name,
            "DEMO", genshinValues, null));
        SeedStatus(vm.GenshinConfiguration, "Status", "DEMO · 任务表单默认值已载入；未读取真实账号或源配置。");
        SeedField(vm.Accounts, "_selectedZenless", vm.Accounts.ZenlessRows[0]);
        vm.Accounts.ZenlessName = vm.Accounts.ZenlessRows[0].Name;
        SeedStatus(vm.Accounts, "ZenlessReadiness", "DEMO · 常用任务目录完整展示；未连接源档案，因此保持只读。");
        SeedStatus(vm.Accounts, "ZenlessStatus", "DEMO · 仅检查排版，不表示账号或引擎已准备完成。");
        SeedStatus(vm.GenshinConfiguration.Source, "Status", "DEMO · 未连接 BetterGI，展示本机任务字段样例。");
        RefreshDemoBindings(vm.Accounts, "ConfigurationRow", "SelectedStarRail", "SelectedZenless", "HasZenlessAccount");
        RefreshDemoBindings(vm.GenshinConfiguration, "SelectedAccount", "VisiblePlanParameters", "DetailedPlanParameters", "CanEdit");
        for (int i = 0; i < 8; i++)
        {
            vm.Queue.Runs.Add(new(Guid.NewGuid(), $"DEMO · 隔离运行记录 {i + 1} · 状态未知（非真实运行）", "11-14 12:00", true, false));
            vm.Queue.LogLines.Add($"DEMO 12:00:{i:00} · 仅供排版检查：任务步骤、较长诊断说明和换行布局，不是引擎执行证据。");
        }
        vm.History.Add(new("DEMO", DateTimeOffset.UnixEpoch, "DEMO · 基础自检展示数据，不是真实检查结果。"));
        vm.RecentRuns.Add(new("DEMO · 星铁", "11-14 12:00", "未知 · 仅布局样例", false, "—"));
        var navigation = (TabControl)main.FindName("Navigation");
        var surface = (FrameworkElement)main.Content;
        foreach (var (width, height, scale) in new[] { (1600, 900, 1.0), (960, 500, 2.0) })
        {
            main.Width = width; main.Height = height; main.UpdateHomeLayout();
            VisualTreeHelper.SetRootDpi(main, new DpiScale(scale, scale));
            ((TextBlock)main.FindName("PersistentStatusText")).SetCurrentValue(TextBlock.TextProperty,
                "DEMO · 隔离合成展示数据；无真实账号、无引擎运行、未保存设置。");
            for (int page = 0; page < navigation.Items.Count; page++)
            {
                navigation.SelectedIndex = page;
                var tabs = page == 1 ? (TabControl)main.FindName("GameTabs") : page == 2 ? (TabControl)main.FindName("AccountTabs") : null;
                for (int sub = 0; sub < (tabs?.Items.Count ?? 1); sub++)
                {
                    if (tabs is not null) tabs.SelectedIndex = sub;
                    FlushBindings();
                    var path = Path.Combine(output, $"court-DEMO-page-{page}-sub-{sub}-{width}x{height}");
                    Render(surface, width, height, path + "-part-00.png", scale);
                    var headingName = page switch { 1 => "GamePageHeading", 2 => "AccountPageHeading", 6 => "SettingsPageHeading", _ => null };
                    if (headingName is not null)
                    {
                        var heading = (TextBlock)main.FindName(headingName);
                        Assert.InRange(heading.TranslatePoint(new Point(), surface).X, (width < 1280 ? 112 : 216) - 1, (width < 1280 ? 112 : 216) + 1);
                    }
                    if (page == 1 && sub == 1)
                        Assert.True(((ItemsControl)main.FindName("GenshinPlanFields")).Items.Count > 0, "DEMO Genshin form did not load fields.");
                    var scroll = page == 1 ? ((TabItem)tabs!.SelectedItem).Content as ScrollViewer
                        : ((TabItem)navigation.SelectedItem).Content as ScrollViewer;
                    if (scroll is null) continue;
                    scroll.ScrollToTop(); surface.UpdateLayout();
                    Render(surface, width, height, path + "-part-00.png", scale);
                    Assert.True(scroll.ViewportHeight > 0);
                    var step = scroll.ViewportHeight * .85;
                    var parts = (int)Math.Ceiling(scroll.ScrollableHeight / step);
                    Assert.InRange(parts, 0, 60);
                    for (int part = 1; part <= parts; part++)
                    {
                        scroll.ScrollToVerticalOffset(Math.Min(part * step, scroll.ScrollableHeight)); surface.UpdateLayout();
                        Render(surface, width, height, path + $"-part-{part:00}.png", scale);
                    }
                    Assert.True(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);
                    scroll.ScrollToTop(); surface.UpdateLayout();
                }
            }
        }
    }

    private static void AssertMedallionTextContrast(Button button, string path)
    {
        var image = new FormatConvertedBitmap(new BitmapImage(new Uri(path)), PixelFormats.Bgra32, null, 0);
        var stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        // Use both actual text bounds: fixed font sizes occupy different image regions at 168/218 DIP.
        var core = Assert.IsType<StackPanel>(button.Template.FindName("Core", button));
        var art = Assert.IsType<FurinaOrnament>(button.Template.FindName("MedallionArt", button));
        Assert.Equal(image.PixelWidth, Assert.IsAssignableFrom<BitmapSource>(art.WaterImage).PixelWidth);
        Assert.Equal(BitmapScalingMode.HighQuality, RenderOptions.GetBitmapScalingMode(art));
        foreach (var text in core.Children.OfType<TextBlock>())
        {
            var bounds = new Rect(text.TranslatePoint(new Point(), art), text.RenderSize);
            var left = (int)Math.Floor(bounds.Left / art.ActualWidth * image.PixelWidth);
            var right = (int)Math.Ceiling(bounds.Right / art.ActualWidth * image.PixelWidth);
            var top = (int)Math.Floor(bounds.Top / art.ActualHeight * image.PixelHeight);
            var bottom = (int)Math.Ceiling(bounds.Bottom / art.ActualHeight * image.PixelHeight);
            Assert.InRange(left, 0, image.PixelWidth - 1); Assert.InRange(right, left + 1, image.PixelWidth);
            Assert.InRange(top, 0, image.PixelHeight - 1); Assert.InRange(bottom, top + 1, image.PixelHeight);
            for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
            {
                var offset = y * stride + x * 4;
                // White behind translucent artwork is a conservative upper bound on luminance.
                var alpha = pixels[offset + 3] / 255.0;
                byte Composite(byte channel) => (byte)Math.Round(channel * alpha + 255 * (1 - alpha));
                var color = Color.FromRgb(Composite(pixels[offset + 2]), Composite(pixels[offset + 1]), Composite(pixels[offset]));
                Assert.True(ContrastAudit.Ratio(Colors.White, color) >= 4.5,
                    $"Medallion text {text.Text} at {button.ActualWidth} DIP has bright background at {x},{y}.");
            }
        }
    }

    private static void AssertInputLinesVisible(TextBox input)
    {
        var host = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
        host.ApplyTemplate();
        var presenter = Assert.IsType<ScrollContentPresenter>(host.Template.FindName("PART_ScrollContentPresenter", host));
        var viewport = presenter.TransformToAncestor(input).TransformBounds(new Rect(presenter.RenderSize));
        Assert.True(viewport.Height >= input.FontSize * input.FontFamily.LineSpacing,
            $"Input viewport too short: {viewport}; font line {input.FontSize * input.FontFamily.LineSpacing}.");
        foreach (var index in new[] { 0, input.Text.Length - 1 })
        {
            input.ScrollToLine(input.GetLineIndexFromCharacterIndex(index));
            input.UpdateLayout();
            var glyph = input.GetRectFromCharacterIndex(index);
            Assert.False(glyph.IsEmpty);
            Assert.True(glyph.Top >= viewport.Top - 1 && glyph.Bottom <= viewport.Bottom + 1,
                $"Input glyph {index} clipped: {glyph}; viewport {viewport}.");
        }
    }

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
