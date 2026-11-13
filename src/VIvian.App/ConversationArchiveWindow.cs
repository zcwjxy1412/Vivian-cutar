using System.Windows;
using System.Windows.Controls;
using VIvian.Infrastructure.Assistant;

namespace VIvian.App;

/// <summary>Archives are text only. No historical action is reconstructed.</summary>
public sealed class ConversationArchiveWindow : Window
{
    public ConversationArchiveWindow(IReadOnlyList<ConversationRecord> archives)
    {
        Title = "对话归档（只读）"; Width = 760; Height = 600; MinWidth = 480; MinHeight = 320;
        var grid = new Grid { Margin = new Thickness(12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new ListBox { ItemsSource = archives.Reverse().ToArray(), DisplayMemberPath = nameof(ConversationRecord.DisplayName) };
        var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 0, 0),
            Text = archives.Count == 0 ? "还没有归档。自动整理在20个完整轮次后或输入预算接近上限时进行。" : "选择一段归档查看原文。" };
        Grid.SetColumn(text, 1); grid.Children.Add(list); grid.Children.Add(text); Content = grid;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not ConversationRecord record) return;
            text.Text = (record.Summary.Length > 0 ? "本段开始时的历史摘要（不代表授权）：\n" + record.Summary + "\n\n" : "")
                + string.Join("\n\n", record.Turns.SelectMany(t => t).Where(m => m?["role"]?.ToString() is "user" or "assistant")
                    .Select(m => (m!["role"]!.ToString() == "user" ? "你：" : "助手：") + m["content"]?.ToString()));
        };
    }
}
