using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace VIvian.Tests;

public sealed class FurinaThemeVisualTests
{
    [Fact]
    public void CompiledThemeRendersRealControlsAndLayeredArtworkWithoutOpeningAWindow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RenderGallery(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Offscreen theme rendering timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void RenderGallery()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "VIvian.App")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var root = directory.FullName;
        var assets = Path.Combine(root, "src", "VIvian.App", "Assets", "Furina");
        var output = Path.Combine(root, "artifacts", "tests-a", "visual");
        Directory.CreateDirectory(output);
        var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/VIvian;component/Themes/Furina/Theme.xaml", UriKind.Relative));
        foreach (var (key, type) in new[] {
            ("PrimaryButtonStyle", typeof(Button)), ("SecondaryButtonStyle", typeof(Button)),
            ("PanelStyle", typeof(Border)), ("InputStyle", typeof(TextBox)),
            ("ProgressStyle", typeof(ProgressBar)), ("MessageBubbleStyle", typeof(Border)) })
            Assert.Equal(type, Assert.IsType<Style>(dictionary["Furina." + key]).TargetType);
        foreach (var name in new[] { "furina-main", "furina-avatar", "scene-background", "water-foreground", "game-genshin", "game-hsr", "game-zzz" })
        {
            var bitmap = new BitmapImage(new Uri(Path.Combine(assets, name + ".png")));
            Assert.True(bitmap.PixelWidth >= 512 && bitmap.PixelHeight >= 256, name);
            if (name is "furina-main" or "furina-avatar" or "water-foreground")
            {
                var rgba = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                var pixel = new byte[4];
                rgba.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                Assert.Equal(0, pixel[3]);
            }
        }

        // A component evidence fixture, not another app or a simulated execution screen.
        var gallery = (Grid)XamlReader.Parse("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Background="{DynamicResource Furina.StageBrush}">
              <Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="/VIvian;component/Themes/Furina/Theme.xaml"/>
              </ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources>
              <Image x:Name="Scene" Stretch="UniformToFill" IsHitTestVisible="False"/>
              <Grid Margin="24">
                <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="*"/></Grid.RowDefinitions>
                <TextBlock Text="furina  /  A 主题组件演示 · 非产品界面验收" Foreground="White" FontSize="22" Margin="0,0,0,18"/>
                <Grid Grid.Row="1">
                  <Grid.ColumnDefinitions><ColumnDefinition Width="0.28*"/><ColumnDefinition Width="0.72*"/></Grid.ColumnDefinitions>
                  <Image x:Name="Character" Stretch="Uniform" IsHitTestVisible="False" Margin="0,0,24,0"/>
                  <Border Grid.Column="1" Style="{StaticResource Furina.PanelStyle}">
                    <ScrollViewer VerticalScrollBarVisibility="Auto">
                      <StackPanel>
                        <TextBlock Text="真实控件 / 普通 · 禁用 · 错误 · 运行示例" Foreground="{StaticResource Furina.TextBrush}" FontSize="20" Margin="0,0,0,16"/>
                        <WrapPanel Margin="0,0,0,16">
                          <Button x:Name="Primary" Content="一键执行" Style="{StaticResource Furina.PrimaryButtonStyle}" Margin="0,0,12,8"/>
                          <Button Content="任务计划" Style="{StaticResource Furina.SecondaryButtonStyle}" Margin="0,0,12,8"/>
                          <Button Content="暂停不可用" Style="{StaticResource Furina.SecondaryButtonStyle}" IsEnabled="False" Margin="0,0,12,8"/>
                          <Button Content="停止任务" Style="{StaticResource Furina.DangerButtonStyle}" Margin="0,0,0,8"/>
                        </WrapPanel>
                        <TextBox x:Name="Input" Style="{StaticResource Furina.InputStyle}" AcceptsReturn="True" TextWrapping="Wrap" MaxHeight="100" VerticalScrollBarVisibility="Auto"
                                 Text="输入示例：请检查今天的任务计划。&#10;长中文消息可换行；这张演示不发送请求，也不执行游戏。" Margin="0,0,0,16"/>
                        <Border Style="{StaticResource Furina.MessageBubbleStyle}" Margin="0,0,0,12">
                          <DockPanel><Image x:Name="Avatar" Width="60" Height="60" Stretch="Uniform" Margin="0,0,12,0" IsHitTestVisible="False"/>
                            <TextBlock Text="芙宁娜：这是消息气泡的排版示例。真实运行结论必须来自执行引擎。" TextWrapping="Wrap" VerticalAlignment="Center" Foreground="{StaticResource Furina.TextBrush}" FontSize="14"/>
                          </DockPanel>
                        </Border>
                        <Border Style="{StaticResource Furina.ErrorPanelStyle}" Margin="0,0,0,16" Padding="12">
                          <TextBlock Text="错误样式示例：连接失败。此文字仅用于验证主题可读性。" TextWrapping="Wrap" Foreground="{StaticResource Furina.DangerBrush}"/>
                        </Border>
                        <TextBlock Text="进度组件示例值 42 / 100（非真实任务）" Foreground="{StaticResource Furina.MutedTextBrush}" Margin="0,0,0,8"/>
                        <ProgressBar x:Name="Progress" Style="{StaticResource Furina.ProgressStyle}" Value="42" Height="10" Margin="0,0,0,10"/>
                        <ProgressBar x:Name="Indeterminate" Style="{StaticResource Furina.ProgressStyle}" IsIndeterminate="True" Height="10" Margin="0,0,0,16"/>
                        <UniformGrid Columns="3"><Image x:Name="Genshin" Height="84" Stretch="UniformToFill" Margin="0,0,8,0"/>
                          <Image x:Name="Hsr" Height="84" Stretch="UniformToFill" Margin="0,0,8,0"/><Image x:Name="Zzz" Height="84" Stretch="UniformToFill"/>
                        </UniformGrid>
                      </StackPanel>
                    </ScrollViewer>
                  </Border>
                </Grid>
              </Grid>
              <Grid Height="24" VerticalAlignment="Bottom" ClipToBounds="True" IsHitTestVisible="False">
                <Image x:Name="Water" Stretch="Uniform" VerticalAlignment="Bottom"/>
              </Grid>
            </Grid>
            """);
        RenderOptions.SetBitmapScalingMode(gallery, BitmapScalingMode.HighQuality);
        foreach (var (control, file) in new[] { ("Scene", "scene-background"), ("Character", "furina-main"), ("Avatar", "furina-avatar"),
            ("Water", "water-foreground"), ("Genshin", "game-genshin"), ("Hsr", "game-hsr"), ("Zzz", "game-zzz") })
            ((Image)gallery.FindName(control)).Source = new BitmapImage(new Uri(Path.Combine(assets, file + ".png")));

        foreach (var (width, height) in new[] { (1600, 900), (960, 500) })
        {
            gallery.Width = width; gallery.Height = height;
            ((Image)gallery.FindName("Water")).Height = width / 3.0;
            gallery.Measure(new Size(width, height)); gallery.Arrange(new Rect(0, 0, width, height)); gallery.UpdateLayout();
            var input = (TextBox)gallery.FindName("Input");
            Assert.NotNull(input.Template.FindName("PART_ContentHost", input));
            var progress = (ProgressBar)gallery.FindName("Progress");
            Assert.NotNull(progress.Template.FindName("PART_Indicator", progress));
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                var target = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                target.Render(gallery);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                using var file = File.Create(Path.Combine(output, $"theme-{width}x{height}-{scale * 100:0}.png"));
                encoder.Save(file);
            }
        }
    }
}
