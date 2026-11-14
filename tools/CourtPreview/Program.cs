using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using VIvian.Infrastructure;

namespace CourtPreview;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is not (["--verify"] or ["--interactive"])) { Console.Error.WriteLine("Use --verify (no windows) or --interactive (manual launch only)."); return 2; }
        if (VIvian.Core.Engines.RealExecutionPolicy.BuildPermitsRealExecution) throw new InvalidOperationException("Preview must be a non-execution build.");
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("Run this visual host with ordinary permissions, not as administrator.");
        var root = Directory.CreateTempSubdirectory("furina-court-demo-").FullName;
        Console.WriteLine("DEMO temporary data: " + root);
        Application? app = null;
        TextWriterTraceListener? trace = null;
        try
        {
            VerifyInputHashes();
            // An empty, unique SQLite fixture only. No production repositories/services are retained.
            new LocalDatabase(Path.Combine(root, "vivian.db")).InitializeAsync().GetAwaiter().GetResult();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources = PreviewMarkup.Resources();
            trace = new TextWriterTraceListener(Path.Combine(root, "binding-errors.log"));
            PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            var main = PreviewMarkup.Load("MainWindow.xaml", DemoData.MainData(root));
            var assistant = PreviewMarkup.Load("AssistantWindow.xaml", DemoData.Assistant(root));
            app.MainWindow = main;
            main.UpdateLayoutResources();
            // The sole enabled Button is owned by this host, and can only show this synthetic assistant.
            var assistantButton = new Button { Content = "查看 DEMO 助手（不发送）", HorizontalAlignment = HorizontalAlignment.Right };
            assistantButton.Click += (_, _) => { if (!assistant.IsVisible) assistant.Show(); else assistant.Activate(); };
            ((DockPanel)main.Content).Children.Insert(1, assistantButton); DockPanel.SetDock(assistantButton, Dock.Bottom);
            assistant.Closing += (_, e) => { if (main.IsVisible) { e.Cancel = true; assistant.Hide(); } };
            if (args[0] == "--verify")
            {
                var evidence = Path.Combine(AppContext.BaseDirectory, "verification"); Directory.CreateDirectory(evidence);
                File.Delete(Path.Combine(evidence, "verification.ok"));
                var before = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "vivian.db")));
                Verify(main, assistant, assistantButton, evidence);
                if (!before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "vivian.db")))))
                    throw new InvalidOperationException("UI verification modified the empty database.");
                trace.Flush(); trace.Close(); PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
                File.Copy(Path.Combine(root, "binding-errors.log"), Path.Combine(evidence, "binding-errors.log"), true);
                File.WriteAllLines(Path.Combine(evidence, "removed-entries.txt"), PreviewMarkup.Changes);
                File.WriteAllText(Path.Combine(evidence, "verification.ok"), "PASS: no HWND; safety negatives, all pages/subpages, scroll bottoms, local input/collapse, zero binding errors, database unchanged.\n");
                Console.WriteLine("PASS: offline safety/layout checks; no window was shown. Evidence: " + evidence);
            }
            else
            {
                Verify(main, assistant, assistantButton, Path.Combine(root, "checks"));
                main.Width = 1600; main.Height = 900; main.UpdateLayoutResources();
                Console.WriteLine("Only local navigation/scroll/expand/text/resize/close is available. All business actions disabled.");
                File.WriteAllLines(Path.Combine(root, "removed-entries.txt"), PreviewMarkup.Changes);
                app.Run(main);
                trace.Flush(); trace.Close();
            }
            assistant.Close(); main.Close();
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            if (trace is not null) { PresentationTraceSources.DataBindingSource.Listeners.Remove(trace); trace.Close(); }
            app?.Shutdown(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("furina-court-demo-"))
                throw new InvalidOperationException("Refuse cleanup outside own temporary child.");
            try { Directory.Delete(full, true); }
            catch (DirectoryNotFoundException) { /* External deletion is already clean. */ }
            Console.WriteLine("Temporary DEMO data deleted: " + full);
        }
    }
    private static void VerifyInputHashes()
    {
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "input-sha256.json")))!;
        foreach (var (relative, expected) in manifest)
        {
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, relative))));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixed candidate input changed: " + relative);
        }
    }
    private static void Verify(PreviewWindow main, PreviewWindow assistant, Button hostButton, string evidence)
    {
        Directory.CreateDirectory(evidence);
        // Negative checks prove the auditor rejects both enabled business buttons and commands.
        ExpectRejected(new Button { Content = "forbidden" });
        ExpectRejected(new Button { IsEnabled = false, Command = new RoutedCommand("Forbidden", typeof(Program)) });
        void ExpectRejected(Button button)
        {
            try { Audit(button, hostButton); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Safety auditor accepted a forbidden control.");
        }
        var sample = System.Xml.Linq.XDocument.Parse("<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' x:Class='Unsafe'><StackPanel><Button Click='Unsafe' Command='{Binding Dangerous}'/><TextBox Text='{Binding LocalText, UpdateSourceTrigger=PropertyChanged}' KeyDown='Unsafe'><TextBox.InputBindings><KeyBinding Key='Enter' Command='{Binding Dangerous}'/></TextBox.InputBindings></TextBox></StackPanel></Window>");
        PreviewMarkup.Sanitize(sample, "negative-fixture");
        if (sample.Descendants().Attributes().Any(a => a.Name.LocalName is "Class" or "Click" or "KeyDown" or "Command") ||
            sample.Descendants().Any(e => e.Name.LocalName.EndsWith(".InputBindings")) ||
            sample.Descendants().Single(e => e.Name.LocalName == "TextBox").Attribute("Text")?.Value != "{Binding LocalText, UpdateSourceTrigger=PropertyChanged}")
            throw new InvalidOperationException("Sanitizer removed display binding or retained a behavior.");
        var nav = (TabControl)main.FindName("Navigation");
        foreach (var (width, height) in new[] { (1600, 900), (960, 500) })
        {
            main.Width = width; main.Height = height; main.UpdateLayoutResources();
            if (main.FindName("HomeSummary") is not UniformGrid || main.IsCompactLayout != (width < 1280) ||
                (double)main.Resources["Furina.PageWidth"] != (width == 1600 ? 1100 : 776))
                throw new InvalidOperationException("Explicit layout initialization failed before rendering.");
            for (int page = 0; page < nav.Items.Count; page++)
            {
                nav.SelectedIndex = page;
                var children = page == 1 ? (TabControl)main.FindName("GameTabs") : page == 2 ? (TabControl)main.FindName("AccountTabs") : null;
                if (children is not null && children.Items.Count != 3) throw new InvalidOperationException("Game/account subpages missing.");
                for (int sub = 0; sub < (children?.Items.Count ?? 1); sub++)
                {
                    if (children is not null) children.SelectedIndex = sub;
                    Render(main, width, height, Path.Combine(evidence, $"demo-page-{page}-sub-{sub}-{width}x{height}.png"));
                    Audit(main, hostButton);
                    var scroll = page == 1 ? ((TabItem)children!.SelectedItem).Content as ScrollViewer
                        : ((TabItem)nav.SelectedItem).Content as ScrollViewer;
                    if (scroll is not null)
                    {
                        scroll.ScrollToBottom(); ((FrameworkElement)main.Content).UpdateLayout();
                        if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 1) throw new InvalidOperationException("Page bottom not reachable.");
                        Render(main, width, height, Path.Combine(evidence, $"demo-page-{page}-sub-{sub}-{width}x{height}-bottom.png"));
                        Audit(main, hostButton); scroll.ScrollToTop();
                    }
                }
            }
        }
        Render(assistant, 460, 720, Path.Combine(evidence, "demo-assistant-460x720.png")); Audit(assistant, hostButton);
        Render(assistant, 360, 460, Path.Combine(evidence, "demo-assistant-360x460.png")); Audit(assistant, hostButton);
        var expander = (Expander)assistant.FindName("QuickCommandsExpander");
        expander.IsExpanded = false; if (expander.IsExpanded) throw new InvalidOperationException("Local collapse failed."); expander.IsExpanded = true;
        var input = (TextBox)assistant.FindName("InputBox"); input.Text = "DEMO local edit";
        if (!input.IsEnabled || input.Text != "DEMO local edit" || input.InputBindings.Count != 0) throw new InvalidOperationException("Local input contract failed.");
        if (nav.Items.Count != 8) throw new InvalidOperationException("Navigation pages missing.");
        if (new System.Windows.Interop.WindowInteropHelper(main).Handle != IntPtr.Zero ||
            new System.Windows.Interop.WindowInteropHelper(assistant).Handle != IntPtr.Zero || main.IsVisible || assistant.IsVisible) throw new InvalidOperationException("Verification must never show windows.");
        File.WriteAllLines(Path.Combine(evidence, "binding-status-errors.txt"), BindingProblems);
        if (BindingProblems.Count != 0) throw new InvalidOperationException("Unresolved display bindings; see binding-status-errors.txt");
        nav.SelectedIndex = 0;
    }
    private static bool IsScrollCommand(ICommand command) => command is RoutedCommand routed &&
        (routed.OwnerType == typeof(ScrollBar) || routed.OwnerType == typeof(ScrollViewer));
    private static IEnumerable<DependencyObject> AllNodes(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(); var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (!seen.Add(node)) continue;
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>()) pending.Push(child);
            if (node is Visual)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
    private static readonly HashSet<string> BindingProblems = [];
    private static void Audit(DependencyObject root, Button hostButton)
    {
        foreach (var node in AllNodes(root))
        {
            if (node is ICommandSource source && source.Command is not null && !IsScrollCommand(source.Command)) throw new InvalidOperationException("Business command survived: " + node.GetType().Name);
            if (node is ButtonBase button && !ReferenceEquals(button, hostButton) && button.IsEnabled && !(button is RepeatButton && button.Command is { } command && IsScrollCommand(command)) && button.TemplatedParent is not Expander) throw new InvalidOperationException("Business button enabled: " + button.Content);
            if (node is UIElement ui && (ui.InputBindings.Count != 0 || ui.CommandBindings.Count != 0)) throw new InvalidOperationException("Input/command route survived.");
            var locals = node.GetLocalValueEnumerator();
            while (locals.MoveNext())
            {
                var entry = locals.Current;
                if (BindingOperations.GetBindingExpressionBase(node, entry.Property) is { } binding && binding.Status is BindingStatus.PathError or BindingStatus.UpdateTargetError)
                    BindingProblems.Add($"{node.GetType().Name}.{entry.Property.Name}: {(binding.ParentBindingBase as Binding)?.Path?.Path} on {(node as FrameworkElement)?.DataContext?.GetType().Name}");
            }
            if (node is FrameworkElement fe && fe.DataContext is { } context && context.GetType().Name.EndsWith("ViewModel")) throw new InvalidOperationException("Business ViewModel found.");
        }
    }
    private static void Render(Window window, int width, int height, string path)
    {
        window.Width = width; window.Height = height;
        var content = (FrameworkElement)window.Content;
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
