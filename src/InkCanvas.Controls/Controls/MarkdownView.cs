using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;
using MarkdownBlock = Markdig.Syntax.Block;

namespace InkCanvas.Controls.Controls;

/// <summary>Markdown rendered as native selectable text and controls.</summary>
public sealed class MarkdownView : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseAutoLinks().DisableHtml().Build();
    private static readonly FontFamily CodeFont = new("Consolas");
    private readonly SolidColorBrush _foreground = new(), _muted = new(), _border = new(), _codeBackground = new();
    private Uri _baseUri;

    public MarkdownView(int theme)
    {
        Spacing = 12;
        RequestedTheme = theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        ActualThemeChanged += (_, _) => ApplyTheme();
        Loaded += (_, _) => ApplyTheme();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        _foreground.Color = dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 26, 26, 26);
        _muted.Color = dark ? Windows.UI.Color.FromArgb(255, 190, 190, 190) : Windows.UI.Color.FromArgb(255, 96, 96, 96);
        _border.Color = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(32, 0, 0, 0);
        _codeBackground.Color = dark ? Windows.UI.Color.FromArgb(255, 32, 32, 32) : Windows.UI.Color.FromArgb(255, 246, 246, 246);
    }

    public void SetMarkdown(string markdown, Uri baseUri = null)
    {
        Children.Clear();
        _baseUri = baseUri;
        foreach (var block in Markdown.Parse(markdown ?? "", Pipeline)) Children.Add(RenderBlock(block));
    }

    private UIElement RenderBlock(MarkdownBlock block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Text(heading.Inline);
                title.FontSize = heading.Level switch { 1 => 24, 2 => 20, 3 => 17, _ => 15 };
                title.LineHeight = Math.Ceiling(title.FontSize * 1.4);
                title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                AutomationProperties.SetHeadingLevel(title, heading.Level switch
                {
                    1 => AutomationHeadingLevel.Level1, 2 => AutomationHeadingLevel.Level2, 3 => AutomationHeadingLevel.Level3,
                    4 => AutomationHeadingLevel.Level4, 5 => AutomationHeadingLevel.Level5, _ => AutomationHeadingLevel.Level6,
                });
                return title;
            case ParagraphBlock paragraph:
                if (paragraph.Inline?.FirstChild is LinkInline { IsImage: true, NextSibling: null } image) return Image(image);
                return Text(paragraph.Inline);
            case CodeBlock code:
                return new Border
                {
                    Padding = new Thickness(12), CornerRadius = new CornerRadius(6), Background = _codeBackground,
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = new TextBlock { Text = code.Lines.ToString().TrimEnd('\r', '\n'), FontFamily = CodeFont, FontSize = 13,
                            IsTextSelectionEnabled = true, Foreground = _foreground },
                    },
                };
            case ListBlock list:
                return List(list);
            case QuoteBlock quote:
                return new Border { BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = _border,
                    Padding = new Thickness(12, 4, 0, 4), Child = Blocks(quote) };
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = _border, Margin = new Thickness(0, 4, 0, 4) };
            case Table table:
                return Table(table);
            case ContainerBlock container:
                return Blocks(container);
            case LeafBlock leaf:
                return new TextBlock { Text = leaf.Lines.ToString(), TextWrapping = TextWrapping.Wrap, Foreground = _foreground };
            default:
                return new Border();
        }
    }

    private StackPanel Blocks(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var block in container) panel.Children.Add(RenderBlock(block));
        return panel;
    }

    private RichTextBlock Text(ContainerInline content)
    {
        var text = new RichTextBlock { FontSize = 14, Foreground = _foreground, TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true, LineHeight = 22 };
        var paragraph = new Paragraph();
        AddInlines(paragraph.Inlines, content);
        text.Blocks.Add(paragraph);
        return text;
    }

    private void AddInlines(InlineCollection output, ContainerInline content)
    {
        if (content is null) return;
        foreach (var inline in content)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    output.Add(new Run { Text = literal.Content.ToString() });
                    break;
                case CodeInline code:
                    output.Add(new Run { Text = code.Content, FontFamily = CodeFont });
                    break;
                case LineBreakInline line:
                    output.Add(line.IsHard ? new LineBreak() : new Run { Text = " " });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                    else span.FontStyle = FontStyle.Italic;
                    AddInlines(span.Inlines, emphasis);
                    output.Add(span);
                    break;
                case LinkInline link:
                    var target = LinkTarget(link.Url);
                    Span label = target is null || link.IsImage ? new Span() : new Hyperlink { NavigateUri = target };
                    AddInlines(label.Inlines, link);
                    if (!string.IsNullOrWhiteSpace(link.Title)) ToolTipService.SetToolTip(label, link.Title);
                    output.Add(label);
                    break;
                case AutolinkInline link:
                    var uri = LinkTarget(link.IsEmail ? "mailto:" + link.Url : link.Url);
                    if (uri is null) output.Add(new Run { Text = link.Url });
                    else
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri };
                        hyperlink.Inlines.Add(new Run { Text = link.Url });
                        output.Add(hyperlink);
                    }
                    break;
                case HtmlEntityInline entity:
                    output.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;
                case TaskList task:
                    output.Add(new Run { Text = task.Checked ? "☑ " : "☐ " });
                    break;
                case ContainerInline container:
                    AddInlines(output, container);
                    break;
            }
        }
    }

    private StackPanel List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 8 };
        int number = int.TryParse(list.OrderedStart, out int first) ? first : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = list.IsOrdered ? (number++) + "." : "•", MinWidth = 18,
                Foreground = _muted, FontSize = 14, LineHeight = 22 });
            var body = Blocks(item);
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
        }
        return panel;
    }

    private UIElement Table(Table table)
    {
        int columns = Math.Max(1, table.ColumnDefinitions.Count);
        var grid = new Grid { MinWidth = columns * 110 };
        for (int column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var row in table.OfType<TableRow>())
        {
            int rowIndex = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            int column = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var content = Blocks(cell);
                if (row.IsHeader)
                    foreach (var text in content.Children.OfType<RichTextBlock>()) text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                var border = new Border { Padding = new Thickness(10, 8, 10, 8), BorderThickness = new Thickness(.5),
                    BorderBrush = _border, Background = row.IsHeader ? _codeBackground : null, Child = content };
                column = Math.Max(column, cell.ColumnIndex);
                int span = Math.Max(1, cell.ColumnSpan);
                var alignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment : null;
                foreach (var text in content.Children.OfType<RichTextBlock>())
                    text.TextAlignment = alignment switch { TableColumnAlign.Right => TextAlignment.Right, TableColumnAlign.Center => TextAlignment.Center, _ => TextAlignment.Left };
                Grid.SetColumn(border, column);
                Grid.SetColumnSpan(border, span);
                column += span;
                Grid.SetRow(border, rowIndex);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    }

    private UIElement Image(LinkInline link)
    {
        var label = Text(link);
        var uri = LinkTarget(link.Url);
        if (uri?.Scheme != Uri.UriSchemeHttps && uri?.Scheme != Uri.UriSchemeHttp) return label;
        var bitmap = new BitmapImage();
        var image = new Microsoft.UI.Xaml.Controls.Image { Source = bitmap, MaxHeight = 420, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(image, string.Concat(link.OfType<LiteralInline>().Select(text => text.Content.ToString())));
        var panel = new StackPanel { Children = { image, label } };
        label.Visibility = Visibility.Collapsed;
        bitmap.ImageFailed += (_, _) => { image.Visibility = Visibility.Collapsed; label.Visibility = Visibility.Visible; };
        bitmap.UriSource = uri;
        return panel;
    }

    private Uri LinkTarget(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) && (_baseUri is null || !Uri.TryCreate(_baseUri, url, out uri))) return null;
        return uri.Scheme is "https" or "http" or "mailto" ? uri : null;
    }
}
