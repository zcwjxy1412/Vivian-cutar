using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using System.Xml.Linq;

namespace CourtPreview;

internal static class PreviewMarkup
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    internal static readonly List<string> Changes = [];
    // Unknown event handlers fail loose-XAML loading (there is deliberately no code-behind).
    private static readonly HashSet<string> Events = new[] { typeof(UIElement).Assembly, typeof(Window).Assembly }.SelectMany(a => a.GetTypes())
        .Where(t => t.IsPublic && typeof(DependencyObject).IsAssignableFrom(t))
        .SelectMany(t => t.GetEvents()).Select(e => e.Name).ToHashSet();
    internal static ResourceDictionary Resources()
    {
        var source = Read("App.xaml");
        var resource = source.Root!.Element(Wpf + "Application.Resources")!.Element(Wpf + "ResourceDictionary")!;
        resource.SetAttributeValue(XNamespace.Xmlns + "x", X.NamespaceName);
        var dictionary = resource.Descendants(Wpf + "ResourceDictionary").Single(e => e.Attribute("Source") is not null);
        if (dictionary.Attribute("Source")!.Value != "Themes/Furina/Theme.xaml") throw new InvalidOperationException("Unexpected theme source.");
        dictionary.SetAttributeValue("Source", "/VIvian;component/Themes/Furina/Theme.xaml");
        return (ResourceDictionary)XamlReader.Parse(resource.ToString());
    }
    private static XDocument Read(string name) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Markup", name), LoadOptions.SetLineInfo);
    internal static PreviewWindow Load(string name, object data)
    {
        System.Reflection.Assembly.Load("XamlAnimatedGif");
        var document = Read(name);
        Sanitize(document, name);
        var window = (PreviewWindow)XamlReader.Parse(document.ToString());
        window.DataContext = data;
        window.Title = "视觉验收 · DEMO · 业务禁用 — " + (name == "MainWindow.xaml" ? "furina" : "助手");
        // Native frame supplies local drag/resize/close without production code-behind.
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.AllowsTransparency = false;
        window.ShowInTaskbar = true;
        window.AllowDrop = false;
        if (name == "AssistantWindow.xaml")
        {
            ((FrameworkElement)window.FindName("EditingSurface")).IsEnabled = true;
            ((TextBlock?)Walk(window).OfType<TextBlock>().FirstOrDefault(t => t.Text == "Enter 换行 · Ctrl+Enter 发送"))?.SetCurrentValue(TextBlock.TextProperty, "仅本地输入 · 不保存、不发送");
        }
        // Always visible, including when page content scrolls. No binding can hide this label.
        var content = (UIElement)window.Content;
        window.Content = null;
        var frame = new DockPanel();
        var label = new TextBlock { Text = "视觉验收 · DEMO · 业务禁用 · 不联网/不保存/不执行", Background = System.Windows.Media.Brushes.DarkRed,
            Foreground = System.Windows.Media.Brushes.White, Padding = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(label, Dock.Top); frame.Children.Add(label); frame.Children.Add(content); window.Content = frame;
        return window;
    }
    internal static void Sanitize(XDocument document, string name)
    {
        var root = document.Root!;
        root.Attribute(X + "Class")?.Remove();
        Changes.Add(name + ": root code-behind removed; plain PreviewWindow substituted");
        root.SetAttributeValue(XNamespace.Xmlns + "preview", "clr-namespace:CourtPreview;assembly=CourtPreview");
        root.Name = XName.Get("PreviewWindow", "clr-namespace:CourtPreview;assembly=CourtPreview");
        foreach (var ns in root.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == "clr-namespace:VIvian.App").ToArray())
            ns.Value += ";assembly=VIvian";
        foreach (var node in root.DescendantsAndSelf().ToArray())
        {
            if (node.Name.NamespaceName == "clr-namespace:VIvian.App")
                node.Name = XName.Get(node.Name.LocalName, "clr-namespace:VIvian.App;assembly=VIvian");
            var line = ((IXmlLineInfo)node).LineNumber;
            if (node.Name.LocalName.EndsWith(".InputBindings") || node.Name.LocalName.EndsWith(".CommandBindings") ||
                node.Name.LocalName is "EventSetter" or "EventTrigger")
            { Changes.Add($"{name}:{line}: remove {node}"); node.Remove(); continue; }
            foreach (var attribute in node.Attributes().Where(a => !a.IsNamespaceDeclaration).ToArray())
            {
                var key = attribute.Name.LocalName;
                if (Events.Contains(key) || key is "Command" or "CommandParameter" or "CommandTarget" or "NavigateUri")
                { Changes.Add($"{name}:{line}: remove {key}={attribute.Value}"); attribute.Remove(); }
            }
            if (node.Name.LocalName is "Button" or "CheckBox" or "RadioButton" or "ComboBox" or "PasswordBox" or "Hyperlink")
            {
                Changes.Add($"{name}:{line}: force {node.Name.LocalName} disabled (product state: {node.Attribute("IsEnabled")?.Value ?? "default"})");
                node.SetAttributeValue("IsEnabled", "False");
            }
        }
        // Settings are intentionally not offered: no endpoint, key or model configuration.
        var settings = root.Descendants().SingleOrDefault(e => e.Attribute(X + "Name")?.Value == "SettingsScroll");
        if (settings is not null) { Changes.Add(name + ": remove API settings subtree"); settings.Remove(); }
    }
    internal static IEnumerable<DependencyObject> Walk(DependencyObject node)
    {
        yield return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var descendant in Walk(child)) yield return descendant;
    }
}

public sealed class PreviewWindow : Window
{
    public object Interaction { get; } = DemoData.Node(("Hint", "DEMO · 仅本地输入，不发送"), ("CanEdit", true));
    public static readonly DependencyProperty IsCompactLayoutProperty = DependencyProperty.Register(nameof(IsCompactLayout), typeof(bool), typeof(PreviewWindow));
    public bool IsCompactLayout { get => (bool)GetValue(IsCompactLayoutProperty); set => SetValue(IsCompactLayoutProperty, value); }
    public PreviewWindow() => SizeChanged += (_, _) => UpdateLayoutResources();
    public void UpdateLayoutResources()
    {
        if (FindName("HomeSummary") is not System.Windows.Controls.Primitives.UniformGrid summary) return;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var compact = width < 1280;
        var character = compact ? 0 : Math.Clamp(width - 1120, 240, 360);
        var controls = width < 1440 ? 240 : 280;
        var middle = width - (compact ? 72 : 176) - character - controls - 40;
        IsCompactLayout = compact;
        Resources["Furina.NavigationWidth"] = new GridLength(compact ? 72 : 176);
        Resources["Furina.PageWidth"] = Math.Min(1100.0, width - (compact ? 72 : 176) - 112);
        Resources["Furina.FieldWidth"] = Math.Min(780.0, (double)Resources["Furina.PageWidth"] - 64);
        Resources["Furina.SettingsColumns"] = compact ? 1 : 2;
        Resources["Furina.CharacterWidth"] = new GridLength(character);
        Resources["Furina.ControlsWidth"] = new GridLength(controls);
        Resources["Furina.GameBannerWidth"] = new GridLength(middle >= 640 ? 240 : 156);
        Resources["Furina.BannerTextWidth"] = middle >= 640 ? 132.0 : 100.0;
        Resources["Furina.CharacterArtWidth"] = character + 100;
        Resources["Furina.CharacterArtHeight"] = (character + 100) * 1.5;
        ((FrameworkElement)FindName("HomeCharacterPanel")).Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        summary.Columns = middle >= 640 ? 4 : 2;
        Resources["Furina.RunButtonSize"] = compact ? 168.0 : 218.0;
    }
}
