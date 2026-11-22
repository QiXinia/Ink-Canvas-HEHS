using System.Xml;
using System.Xml.Linq;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Latex;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace InkCanvas.Features.AiAssistant;

public sealed class BoardFormulaRenderResult
{
    public byte[] Png { get; set; } = Array.Empty<byte>();

    /// <summary>卡片在展板上的 DIP 宽度。</summary>
    public double Width { get; set; }

    /// <summary>卡片在展板上的 DIP 高度。</summary>
    public double Height { get; set; }

    public string Error { get; set; } = "";

    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

    public bool Success => Png is { Length: > 0 } && Width > 0 && Height > 0;
}

/// <summary>
/// 把 LaTeX 公式渲染为透明/浅底 PNG 卡片：
/// <see cref="LatexToMathML"/> → UI 线程构造 WinUI 公式视觉树 → 测量 → RenderTargetBitmap → PNG。
/// 公式视图必须挂到前台窗口的 XamlRoot 才能被 RenderTargetBitmap 捕获，
/// 因此调用方需传入当前窗口根元素（<paramref name="host"/>）；
/// 任何失败都返回理由，由调用方回退到 <see cref="BoardTextCardRenderer"/> 保留 LaTeX 源。
/// </summary>
public static class BoardFormulaRenderer
{
    private const double Padding = 18;
    private const double MinCardWidth = 180;
    private const double MaxCardWidth = 720;
    private const double PreferredFontSize = 30;
    private const double MinFontSize = 15;
    private const double MaxRenderScale = 4.0;
    private const double MinRenderScale = 0.5;
    private const int MaxPixels = 8192;
    private const string FontFamily = "Cambria Math";
    private const string CjkFontFamily = "Microsoft YaHei UI";

    private static readonly SolidColorBrush InkBrush = new(Color.FromArgb(255, 32, 32, 36));
    private static readonly SolidColorBrush CardBrush = new(Color.FromArgb(255, 250, 250, 250));
    private static readonly SolidColorBrush BorderBrush = new(Color.FromArgb(255, 220, 220, 224));

    /// <summary>
    /// 渲染公式卡片。<paramref name="scale"/> 为物理像素倍率（建议 2.0）；
    /// <paramref name="host"/> 可以是前台窗口的任意已挂载元素（Content、Border、Grid 等），
    /// 渲染器会自动解析可挂载的 Panel。
    /// </summary>
    public static Task<BoardFormulaRenderResult> RenderPngAsync(string latex, int maxWidthDip, double scale)
        => RenderPngAsync(latex, maxWidthDip, scale, null);

    public static async Task<BoardFormulaRenderResult> RenderPngAsync(
        string latex, int maxWidthDip, double scale, FrameworkElement host)
    {
        if (!LatexToMathML.TryConvert(latex, out string mathMl, out string error, out IReadOnlyList<string> warnings))
        {
            string message = "LaTeX 转换失败：" + error;
            FileLog.WriteLogToFile($"[AI] 公式渲染失败：{message}，latex={Shorten(latex)}", LogType.Error);
            return Fail(message, warnings);
        }

        if (warnings.Count > 0)
            FileLog.WriteLogToFile(
                $"[AI] 公式转换警告：{string.Join("；", warnings)}，latex={Shorten(latex)}", LogType.Trace);

        if (host is null || host.XamlRoot is null)
        {
            const string reason = "公式渲染需要前台窗口（XamlRoot 不可用），已回退文本";
            FileLog.WriteLogToFile($"[AI] 公式位图渲染回退：{reason}，latex={Shorten(latex)}", LogType.Trace);
            return Fail(reason, warnings);
        }

        if (host.DispatcherQueue is { HasThreadAccess: false })
        {
            FileLog.WriteLogToFile("[AI] 公式渲染失败：必须在 UI 线程构造并测量公式视图", LogType.Error);
            return Fail("公式渲染必须在 UI 线程调用", warnings);
        }

        Panel panel = ResolveMountPanel(host, out string mountPath);
        if (panel is null)
        {
            const string reason = "未找到可挂载公式视图的 Panel（已尝试 host、祖先链与 XamlRoot 子树）";
            FileLog.WriteLogToFile($"[AI] 公式位图渲染回退：{reason}，latex={Shorten(latex)}", LogType.Trace);
            return Fail(reason, warnings);
        }

        FileLog.WriteLogToFile(
            $"[AI] 公式渲染宿主：host={host.GetType().Name}，panel={panel.GetType().Name}，path={mountPath}",
            LogType.Trace);

        int widthLimit = maxWidthDip > 0 ? maxWidthDip : (int)MaxCardWidth;
        widthLimit = (int)Clamp(widthLimit, MinCardWidth, MaxCardWidth);
        double renderScale = Clamp(scale <= 0 ? 2.0 : scale, MinRenderScale, MaxRenderScale);

        double overlayExtent = panel is StackPanel ? 0 : 1;
        var overlay = new Canvas
        {
            Width = overlayExtent,
            Height = overlayExtent,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Canvas.SetLeft(overlay, -20000);
        Canvas.SetTop(overlay, -20000);

        double fontSize = PreferredFontSize;
        Border card = null;
        try
        {
            panel.Children.Add(overlay);

            double cardWidth = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                card = BuildCard(mathMl, fontSize);
                overlay.Children.Add(card);

                cardWidth = MeasureAndArrange(card);
                if (cardWidth <= widthLimit || fontSize <= MinFontSize) break;

                overlay.Children.Remove(card);
                card.Child = null;
                card = null;
                fontSize = System.Math.Max(MinFontSize, fontSize * 0.86);
            }

            if (card is null) return Fail("公式视图构建失败", warnings);

            cardWidth = System.Math.Min(widthLimit, System.Math.Ceiling(cardWidth));
            double cardHeight = System.Math.Ceiling(card.ActualHeight);
            if (cardHeight <= 0) return Fail("公式测量高度为空", warnings);

            card.Width = cardWidth;
            MeasureAndArrange(card, cardWidth);
            cardHeight = System.Math.Ceiling(card.ActualHeight <= 0 ? card.DesiredSize.Height : card.ActualHeight);
            if (cardHeight <= 0) return Fail("公式测量高度为空", warnings);

            card.Width = cardWidth;
            card.Height = cardHeight;
            card.Measure(new Size(cardWidth, cardHeight));
            card.Arrange(new Rect(0, 0, cardWidth, cardHeight));
            card.UpdateLayout();

            // 元素刚挂到可视树，等待一次布局周期，确保 RenderAsync 读取到最终 arrange。
            await Task.Yield();
            card.UpdateLayout();

            double pixelScale = System.Math.Min(renderScale, MaxPixels / System.Math.Max(1, System.Math.Max(cardWidth, cardHeight)));
            if (!(pixelScale > 0)) pixelScale = 1.0;

            int pixelWidth = System.Math.Max(1, (int)System.Math.Round(cardWidth * pixelScale));
            int pixelHeight = System.Math.Max(1, (int)System.Math.Round(cardHeight * pixelScale));

            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(card, pixelWidth, pixelHeight);

            IBuffer pixels = await bitmap.GetPixelsAsync();
            if (pixels is null || pixels.Length == 0) return Fail("RenderTargetBitmap 未返回像素", warnings);

            byte[] png = await EncodePngAsync(bitmap, pixels);
            if (png.Length == 0)
            {
                const string reason = "公式位图渲染结果为空或全透明";
                FileLog.WriteLogToFile($"[AI] 公式位图渲染回退：{reason}，latex={Shorten(latex)}", LogType.Error);
                return Fail(reason, warnings);
            }

            return new BoardFormulaRenderResult
            {
                Png = png,
                Width = cardWidth,
                Height = cardHeight,
                Warnings = warnings,
            };
        }
        catch (Exception ex)
        {
            string message = "公式位图渲染异常：" + (ex.Message ?? ex.GetType().Name);
            FileLog.WriteLogToFile($"[AI] 公式渲染失败：{message}，latex={Shorten(latex)}", LogType.Error);
            return Fail(message, warnings);
        }
        finally
        {
            if (card is not null)
            {
                overlay.Children.Remove(card);
                card.Child = null;
            }

            panel.Children.Remove(overlay);
        }
    }

    /// <summary>
    /// 解析可挂载的 Panel：优先 host 自身，其次祖先链，最后在 XamlRoot 内容树中向下查找
    /// （浮窗 Content 常是 Border，真正的布局 Grid 在其子级）。只读查找，不修改现有布局。
    /// </summary>
    private static Panel ResolveMountPanel(FrameworkElement host, out string path)
    {
        path = "";
        if (host is Panel self)
        {
            path = "host";
            return self;
        }

        int ancestorDepth = 0;
        for (DependencyObject node = VisualTreeHelper.GetParent(host);
             node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            ancestorDepth++;
            if (node is Panel ancestor)
            {
                path = "ancestor+" + ancestorDepth;
                return ancestor;
            }
        }

        FrameworkElement root = host.XamlRoot?.Content as FrameworkElement;
        if (root is Panel rootPanel)
        {
            path = "xamlroot";
            return rootPanel;
        }

        if (root is not null)
        {
            Panel fromRoot = FindFirstMountPanel(root, maxDepth: 8, maxNodes: 128);
            if (fromRoot is not null)
            {
                path = "xamlroot-tree";
                return fromRoot;
            }
        }

        Panel fromHost = FindFirstMountPanel(host, maxDepth: 4, maxNodes: 64);
        if (fromHost is not null)
        {
            path = "host-tree";
            return fromHost;
        }

        return null;
    }

    /// <summary>广度优先查找第一个 Panel；优先 Canvas/Grid/RelativePanel，其次任意 Panel。</summary>
    private static Panel FindFirstMountPanel(DependencyObject root, int maxDepth, int maxNodes)
    {
        var queue = new Queue<(DependencyObject Node, int Depth)>();
        queue.Enqueue((root, 0));
        Panel fallback = null;
        int visited = 0;

        while (queue.Count > 0 && visited++ < maxNodes)
        {
            (DependencyObject node, int depth) = queue.Dequeue();
            if (node is Panel panel)
            {
                if (panel is Canvas or Grid or RelativePanel) return panel;
                fallback ??= panel;
            }

            if (depth >= maxDepth) continue;

            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
                queue.Enqueue((VisualTreeHelper.GetChild(node, index), depth + 1));
        }

        return fallback;
    }

    private static BoardFormulaRenderResult Fail(string message, IReadOnlyList<string> warnings)
        => new() { Error = message, Warnings = warnings ?? Array.Empty<string>() };

    private static Border BuildCard(string mathMl, double fontSize)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(BuildMathMl(mathMl, fontSize));

        return new Border
        {
            Background = CardBrush,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(Padding, Padding - 2, Padding, Padding - 2),
            Child = content,
        };
    }

    private static double MeasureAndArrange(FrameworkElement element, double width = double.PositiveInfinity)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        double measuredWidth = element.DesiredSize.Width;
        double measuredHeight = element.DesiredSize.Height;
        element.Arrange(new Rect(0, 0, measuredWidth, measuredHeight));
        element.UpdateLayout();
        return element.ActualWidth > 0 ? element.ActualWidth : measuredWidth;
    }

    private static async Task<byte[]> EncodePngAsync(RenderTargetBitmap bitmap, IBuffer pixels)
    {
        var bytes = new byte[pixels.Length];
        using (var reader = DataReader.FromBuffer(pixels)) reader.ReadBytes(bytes);
        if (!HasOpaquePixel(bytes)) return Array.Empty<byte>();

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth,
            (uint)bitmap.PixelHeight,
            96,
            96,
            bytes);
        await encoder.FlushAsync();

        stream.Seek(0);
        var png = new byte[stream.Size];
        if (png.Length == 0) return Array.Empty<byte>();
        using var pngReader = new DataReader(stream.GetInputStreamAt(0));
        await pngReader.LoadAsync((uint)png.Length);
        pngReader.ReadBytes(png);
        return png;
    }

    /// <summary>采样 BGRA 像素的 alpha，判断 RenderTargetBitmap 是否只输出了全透明缓冲。</summary>
    private static bool HasOpaquePixel(byte[] bgra)
    {
        if (bgra is null || bgra.Length < 4) return false;
        int stride = System.Math.Max(4, bgra.Length / 256 / 4 * 4);
        for (int index = 3; index < bgra.Length; index += stride)
        {
            if (bgra[index] != 0) return true;
        }

        return bgra[^1] != 0;
    }

    // ---- MathML → WinUI 视觉树 ------------------------------------------------

    private static FrameworkElement BuildMathMl(string mathMl, double fontSize)
    {
        using var reader = XmlReader.Create(new StringReader(mathMl), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 32768,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });

        XElement root = XDocument.Load(reader).Root
            ?? throw new FormatException("MathML 缺少根节点");
        if (root.Name.LocalName != "math") throw new FormatException("MathML 根节点必须是 math");

        var counter = new NodeCounter();
        return BuildNode(root, 0, counter, fontSize);
    }

    private sealed class NodeCounter
    {
        public int Value;
    }

    private static FrameworkElement BuildNode(XElement element, int depth, NodeCounter counter, double fontSize)
    {
        if (++counter.Value > 512 || depth > 48) throw new FormatException("公式结构过于复杂");

        XElement[] children = element.Elements().ToArray();
        FrameworkElement Child(int index)
            => index < children.Length ? BuildNode(children[index], depth + 1, counter, fontSize) : new TextBlock();

        switch (element.Name.LocalName)
        {
            case "math":
            case "mrow":
            case "mstyle":
            case "mpadded":
                return Row(children, depth, counter, fontSize);
            case "mi":
            case "mn":
            case "mo":
            case "mtext":
                return Atom(element.Value, fontSize);
            case "mfrac":
                return Fraction(Child(0), Child(1));
            case "msup":
                return Script(Child(0), Child(1), null, fontSize);
            case "msub":
                return Script(Child(0), null, Child(1), fontSize);
            case "msubsup":
                return Script(Child(0), Child(2), Child(1), fontSize);
            case "msqrt":
                return Radical(Row(children, depth + 1, counter, fontSize), fontSize);
            case "mroot":
                return IndexedRadical(Child(0), Child(1), fontSize);
            case "munder":
                return Script(Child(0), null, Child(1), fontSize);
            case "mover":
                return Script(Child(0), Child(1), null, fontSize);
            case "munderover":
                return Script(Child(0), Child(2), Child(1), fontSize);
            case "mtable":
                return Table(children, depth, counter, fontSize);
            case "mtd":
                return Row(children, depth, counter, fontSize);
            case "mfenced":
                return Fenced(element, children, depth, counter, fontSize);
            case "semantics":
                return Row(
                    children.Where(child => child.Name.LocalName is not ("annotation" or "annotation-xml")).ToArray(),
                    depth, counter, fontSize);
            case "mspace":
            case "none":
            case "annotation":
            case "annotation-xml":
                return new TextBlock();
            default:
                return children.Length == 0
                    ? Atom(element.Value, fontSize)
                    : Row(children, depth, counter, fontSize);
        }
    }

    private static FrameworkElement Row(XElement[] children, int depth, NodeCounter counter, double fontSize)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2,
        };
        foreach (XElement child in children)
            row.Children.Add(BuildNode(child, depth + 1, counter, fontSize));
        return row;
    }

    private static FrameworkElement Table(XElement[] rows, int depth, NodeCounter counter, double fontSize)
    {
        var grid = new Grid
        {
            ColumnSpacing = 14,
            RowSpacing = 6,
            Margin = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
        };

        int columns = rows.Length == 0 ? 0 : rows.Max(row => row.Elements().Count());
        for (int index = 0; index < columns; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int column = 0;
            foreach (XElement cell in rows[row].Elements())
            {
                FrameworkElement cellElement = BuildNode(cell, depth + 1, counter, fontSize);
                cellElement.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetRow(cellElement, row);
                Grid.SetColumn(cellElement, column++);
                grid.Children.Add(cellElement);
            }
        }

        return grid;
    }

    private static FrameworkElement Atom(string value, double fontSize)
    {
        bool hasCjk = ContainsCjk(value);
        return new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily(hasCjk ? CjkFontFamily : FontFamily),
            FontSize = fontSize,
            Foreground = InkBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
    }

    private static FrameworkElement Fraction(FrameworkElement numerator, FrameworkElement denominator)
    {
        var fraction = new Grid
        {
            MinWidth = 34,
            Padding = new Thickness(4, 3, 4, 3),
            RowSpacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        fraction.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fraction.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        fraction.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Center(numerator);
        Center(denominator);
        fraction.Children.Add(numerator);

        var bar = new Border { Height = 1, Background = InkBrush, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(bar, 1);
        fraction.Children.Add(bar);

        Grid.SetRow(denominator, 2);
        fraction.Children.Add(denominator);
        return fraction;
    }

    private static FrameworkElement Script(
        FrameworkElement basis, FrameworkElement superscript, FrameworkElement subscript, double fontSize)
    {
        var grid = new Grid
        {
            ColumnSpacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Center(basis);
        Grid.SetRow(basis, 1);
        grid.Children.Add(basis);

        if (superscript is not null)
        {
            FrameworkElement small = Small(superscript, fontSize);
            small.Margin = new Thickness(0, 0, 0, -fontSize * 0.30);
            Grid.SetColumn(small, 1);
            Grid.SetRow(small, 0);
            grid.Children.Add(small);
        }

        if (subscript is not null)
        {
            FrameworkElement small = Small(subscript, fontSize);
            small.Margin = new Thickness(0, -fontSize * 0.30, 0, 0);
            Grid.SetColumn(small, 1);
            Grid.SetRow(small, 2);
            grid.Children.Add(small);
        }

        return grid;
    }

    private static FrameworkElement Radical(FrameworkElement radicand, double fontSize)
    {
        var grid = new Grid
        {
            ColumnSpacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        FrameworkElement sign = Atom("√", fontSize);
        Center(radicand);
        Grid.SetRow(sign, 1);
        Grid.SetColumn(sign, 0);
        grid.Children.Add(sign);

        var body = new Border
        {
            BorderBrush = InkBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(3, 1, 3, 0),
            Child = radicand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(body, 1);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement IndexedRadical(
        FrameworkElement radicand, FrameworkElement index, double fontSize)
    {
        index = Small(index, fontSize);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(index, 0);
        Grid.SetColumn(index, 0);
        grid.Children.Add(index);

        FrameworkElement radical = Radical(radicand, fontSize);
        Grid.SetRowSpan(radical, 2);
        Grid.SetColumn(radical, 1);
        grid.Children.Add(radical);
        return grid;
    }

    private static FrameworkElement Fenced(
        XElement element, XElement[] children, int depth, NodeCounter counter, double fontSize)
    {
        string open = (string)element.Attribute("open") ?? "(";
        string close = (string)element.Attribute("close") ?? ")";
        string separators = (string)element.Attribute("separators") ?? ",";
        string separator = separators.Length == 0 ? "," : separators[..1];

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 1,
        };
        if (open.Length > 0) row.Children.Add(Atom(open, fontSize));
        for (int index = 0; index < children.Length; index++)
        {
            if (index > 0 && separator.Length > 0) row.Children.Add(Atom(separator, fontSize));
            row.Children.Add(BuildNode(children[index], depth + 1, counter, fontSize));
        }
        if (close.Length > 0) row.Children.Add(Atom(close, fontSize));
        return row;
    }

    private static FrameworkElement Small(FrameworkElement element, double fontSize) => new Viewbox
    {
        Child = element,
        Height = System.Math.Max(12, fontSize * 0.62),
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static void Center(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
    }

    private static bool ContainsCjk(string value)
    {
        foreach (char character in value ?? "")
        {
            if (character is >= '\u2E80' and <= '\u9FFF' or >= '\uFF00' and <= '\uFFEF')
                return true;
        }

        return false;
    }

    private static string Shorten(string latex)
    {
        if (string.IsNullOrEmpty(latex)) return "(空)";
        return latex.Length <= 120 ? latex : latex[..120] + "…";
    }

    private static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;
}