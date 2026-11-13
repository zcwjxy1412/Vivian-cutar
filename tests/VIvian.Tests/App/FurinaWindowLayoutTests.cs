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
            var home = (FrameworkElement)main.Content;
            foreach (var (width, height) in new[] { (1600, 900), (960, 500) })
            {
                main.Width = width; main.Height = height; main.UpdateHomeLayout();
                Assert.Equal(width < 1280, main.IsCompactLayout);
                Render(home, width, height, Path.Combine(output, $"home-content-{width}x{height}.png"));
                Assert.Equal(width < 1280 ? 2 : 4, ((System.Windows.Controls.Primitives.UniformGrid)main.FindName("HomeSummary")).Columns);                var stop = (Button)main.FindName("PersistentStopButton");
                var stopBounds = stop.TransformToAncestor(home).TransformBounds(new Rect(stop.RenderSize));
                Assert.True(stopBounds.Bottom <= height && stopBounds.Right <= width, $"Stop clipped: {stopBounds}");
            }
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
            Assert.NotNull(input.Template.FindName("PART_ContentHost", input));
            Assert.True(input.AcceptsReturn);
            Assert.True(input.MaxHeight < double.PositiveInfinity);
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

    private static void Render(FrameworkElement element, int width, int height, string path)
    {
        element.Width = double.NaN; element.Height = double.NaN;
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
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
