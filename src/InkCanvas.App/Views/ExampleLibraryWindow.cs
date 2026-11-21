using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Settings;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// 本地例题库窗口：按关键词 / 课题 / 知识点检索已有例题，查看题目、答案与教师订正，
/// 并可删除不再需要的条目。数据全部来自本地 <see cref="ExampleLibraryStore"/>，不联网。
/// 关闭按钮只隐藏窗口（<see cref="Microsoft.UI.Windowing.AppWindow.Hide"/>），保留实例便于再次打开。
/// </summary>
public sealed class ExampleLibraryWindow : Window
{
    private readonly FeatureSettings _features;
    private readonly bool _enabled;
    private readonly TextBox _keyword = new()
    {
        PlaceholderText = "关键词（标题 / 答案 / 订正）",
        Width = 200,
        MinHeight = 32,
    };
    private readonly TextBox _topic = new()
    {
        PlaceholderText = "课题",
        Width = 150,
        MinHeight = 32,
    };
    private readonly TextBox _knowledge = new()
    {
        PlaceholderText = "知识点",
        Width = 150,
        MinHeight = 32,
    };
    private readonly ListView _list = new()
    {
        Height = 196,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly TextBlock _empty = new()
    {
        Text = "暂无例题",
        FontSize = 12,
        Opacity = 0.7,
        Margin = new Thickness(2, 0, 0, 0),
    };
    private readonly TextBlock _closedHint = new()
    {
        Text = "例题库已关闭",
        FontSize = 13,
        Opacity = 0.8,
        Margin = new Thickness(2, 8, 0, 8),
    };
    private readonly TextBlock _titleText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _answerText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _correctedText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _detailCard;
    private readonly Button _search;
    private readonly Button _clear;
    private readonly Button _delete;

    public ExampleLibraryWindow(AppSettings settings, int theme)
    {
        _features = settings?.Features;
        _enabled = _features?.EnableExampleLibrary ?? true;
        Title = "例题库";

        _search = new Button { Content = "检索", MinHeight = 34, MinWidth = 76 };
        _search.Click += (_, _) => Refresh();

        _clear = new Button { Content = "清空", MinHeight = 34, MinWidth = 68 };
        _clear.Click += (_, _) =>
        {
            _keyword.Text = "";
            _topic.Text = "";
            _knowledge.Text = "";
            Refresh();
        };

        var filterRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        filterRow.Children.Add(_keyword);
        filterRow.Children.Add(_topic);
        filterRow.Children.Add(_knowledge);
        filterRow.Children.Add(_search);
        filterRow.Children.Add(_clear);

        _list.SelectionChanged += (_, _) => UpdateSelection();

        _delete = new Button
        {
            Content = "删除",
            MinHeight = 34,
            MinWidth = 80,
            IsEnabled = false,
        };
        _delete.Click += (_, _) => DeleteSelected();

        var close = new Button { Content = "关闭", MinHeight = 34, MinWidth = 72 };
        close.Click += (_, _) => HideWindow();

        var actionRow = new Grid();
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_delete, 0);
        Grid.SetColumn(close, 1);
        _delete.HorizontalAlignment = HorizontalAlignment.Left;
        actionRow.Children.Add(_delete);
        actionRow.Children.Add(close);

        var detailRows = new StackPanel { Spacing = 10 };
        detailRows.Children.Add(DetailField("题目", _titleText));
        detailRows.Children.Add(DetailField("答案", _answerText));
        detailRows.Children.Add(DetailField("教师订正", _correctedText));

        _detailCard = new Border
        {
            Background = ThemeBrushes.Resolve(theme, "IcSurfaceAltBrush"),
            BorderBrush = ThemeBrushes.Resolve(theme, "IcStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Visibility = Visibility.Collapsed,
            Child = new ScrollViewer
            {
                Content = detailRows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 170,
            },
        };

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "本地例题库",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(filterRow);
        panel.Children.Add(_list);
        panel.Children.Add(_closedHint);
        panel.Children.Add(_empty);
        panel.Children.Add(_detailCard);
        panel.Children.Add(actionRow);

        var body = new Border
        {
            Background = ThemeBrushes.Resolve(theme, "IcSurfaceBrush"),
            Child = panel,
        };

        var root = WindowChrome.AddTitleBar(this, "例题库", body, closeButton: false);
        if (root.Children.Count > 0 && root.Children[0] is Grid header)
            header.Background = ThemeBrushes.Resolve(theme, "IcSurfaceBrush");

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { HideWindow(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;
        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 800, 680);
        WindowChrome.ApplyTheme(this, theme);

        ApplyEnabledState();
        Refresh();
    }

    /// <summary>显示或重新置前；用于单实例入口从隐藏状态恢复。</summary>
    public void ShowWindow()
    {
        try { AppWindow.Show(); }
        catch (Exception) { }
        Activate();
    }

    private void HideWindow()
    {
        try { AppWindow.Hide(); }
        catch (Exception) { }
    }

    private void ApplyEnabledState()
    {
        _keyword.IsEnabled = _enabled;
        _topic.IsEnabled = _enabled;
        _knowledge.IsEnabled = _enabled;
        _search.IsEnabled = _enabled;
        _clear.IsEnabled = _enabled;
        if (!_enabled) _delete.IsEnabled = false;
        _closedHint.Visibility = _enabled ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility = _enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Refresh()
    {
        _list.Items.Clear();

        if (!_enabled)
        {
            _empty.Visibility = Visibility.Collapsed;
            _detailCard.Visibility = Visibility.Collapsed;
            _delete.IsEnabled = false;
            return;
        }

        var query = new ExampleQuery
        {
            Keyword = Trimmed(_keyword.Text),
            Topic = Trimmed(_topic.Text),
            KnowledgePoint = Trimmed(_knowledge.Text),
        };
        IReadOnlyList<ExampleEntry> entries = ExampleLibraryStore.Search(query, settings: _features);

        foreach (ExampleEntry entry in entries)
        {
            _list.Items.Add(new ListViewItem
            {
                Tag = entry,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = BuildRow(entry),
            });
        }

        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _detailCard.Visibility = Visibility.Collapsed;
        _delete.IsEnabled = false;
    }

    private void UpdateSelection()
    {
        ExampleEntry entry = SelectedEntry();
        bool hasSelection = _enabled && entry is not null;
        _delete.IsEnabled = hasSelection;

        if (!hasSelection)
        {
            _detailCard.Visibility = Visibility.Collapsed;
            return;
        }

        _titleText.Text = string.IsNullOrWhiteSpace(entry.Title) ? "（未命名例题）" : entry.Title;
        _answerText.Text = string.IsNullOrWhiteSpace(entry.Answer) ? "（无答案）" : entry.Answer;
        _correctedText.Text = string.IsNullOrWhiteSpace(entry.CorrectedText)
            ? "（无教师订正）"
            : entry.CorrectedText;
        _detailCard.Visibility = Visibility.Visible;
    }

    private void DeleteSelected()
    {
        ExampleEntry entry = SelectedEntry();
        if (!_enabled || entry is null) return;

        if (ExampleLibraryStore.Delete(entry.Id, settings: _features))
            Refresh();
    }

    private ExampleEntry SelectedEntry()
        => (_list.SelectedItem as ListViewItem)?.Tag as ExampleEntry;

    private static FrameworkElement BuildRow(ExampleEntry entry)
    {
        string knowledge = entry.KnowledgePoints is { Count: > 0 }
            ? string.Join("，", entry.KnowledgePoints)
            : "（无知识点）";
        string source = string.IsNullOrWhiteSpace(entry.SourcePresentation)
            ? "（无来源课件）"
            : entry.SourcePresentation;
        string created = entry.CreatedAt == default ? "（无日期）" : entry.CreatedAt.ToString("yyyy-MM-dd");

        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(entry.Title) ? "（未命名例题）" : entry.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "课题：" + (string.IsNullOrWhiteSpace(entry.Topic) ? "（未填写）" : entry.Topic)
                + "    知识点：" + knowledge,
            FontSize = 11,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = "来源课件：" + source + "    创建日期：" + created,
            FontSize = 11,
            Opacity = 0.62,
            TextWrapping = TextWrapping.Wrap,
        });
        return content;
    }

    private static FrameworkElement DetailField(string label, TextBlock value)
    {
        var field = new StackPanel { Spacing = 3 };
        field.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Opacity = 0.7,
        });
        field.Children.Add(value);
        return field;
    }

    private static string Trimmed(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}