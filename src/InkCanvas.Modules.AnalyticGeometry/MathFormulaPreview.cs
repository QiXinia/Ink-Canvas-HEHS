using System.Xml;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>Native WinUI MathML presentation for machines where RichEdit math mode is unavailable.</summary>
internal sealed class MathFormulaPreview : UserControl
{
    private const string MathMlNamespace = "http://www.w3.org/1998/Math/MathML";

    public MathFormulaPreview()
    {
        MinHeight = 84;
        MaxHeight = 180;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        IsTabStop = false;
        AutomationProperties.SetName(this, "MathML 公式预览");
    }

    public void SetMathML(string mathMl)
    {
        using var reader = XmlReader.Create(new StringReader(mathMl), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 32768,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        });
        var document = XDocument.Load(reader);
        if (document.Root is null || document.Root.Name.LocalName != "math"
            || document.Root.Name.NamespaceName != MathMlNamespace)
            throw new FormatException("公式不是有效的 MathML 3.0。");
        var nodes = new NodeCounter();
        var visual = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = Build(document.Root, 0, nodes),
        };
        Content = visual;
    }

    private sealed class NodeCounter { public int Value; }

    private FrameworkElement Build(XElement element, int depth, NodeCounter counter)
    {
        if (++counter.Value > 512 || depth > 48) throw new FormatException("公式结构过于复杂，无法预览。");
        var children = element.Elements().ToArray();
        FrameworkElement Child(int index) => index < children.Length ? Build(children[index], depth + 1, counter) : new TextBlock();
        return element.Name.LocalName switch
        {
            "math" or "mrow" or "mstyle" => Row(children, depth, counter),
            "mi" or "mn" or "mo" or "mtext" => Atom(element.Value),
            "mfrac" => Fraction(Child(0), Child(1)),
            "msup" => Script(Child(0), Child(1), null),
            "msub" => Script(Child(0), null, Child(1)),
            "msubsup" => Script(Child(0), Child(2), Child(1)),
            "msqrt" => Radical(Row(children, depth + 1, counter)),
            "mroot" => IndexedRadical(Child(0), Child(1)),
            "munder" => Script(Child(0), null, Child(1)),
            "mover" => Script(Child(0), Child(1), null),
            "munderover" => Script(Child(0), Child(2), Child(1)),
            "mtable" => Table(children, depth, counter),
            "mtd" => Row(children, depth, counter),
            "mfenced" => Fenced(element, children, depth, counter),
            "semantics" => Row(children.Where(child => child.Name.LocalName is not ("annotation" or "annotation-xml")).ToArray(), depth, counter),
            "mspace" or "none" or "annotation" or "annotation-xml" => new TextBlock(),
            _ => children.Length == 0 ? Atom(element.Value) : Row(children, depth, counter),
        };
    }

    private FrameworkElement Row(XElement[] children, int depth, NodeCounter counter)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        foreach (var child in children) row.Children.Add(Build(child, depth + 1, counter));
        return row;
    }

    private FrameworkElement Table(XElement[] rows, int depth, NodeCounter counter)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4, Margin = new Thickness(5) };
        int columns = rows.Length == 0 ? 0 : rows.Max(row => row.Elements().Count());
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int row = 0; row < rows.Length; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            int column = 0;
            foreach (var cell in rows[row].Elements())
            {
                var element = Build(cell, depth + 1, counter);
                Grid.SetRow(element, row); Grid.SetColumn(element, column++);
                grid.Children.Add(element);
            }
        }
        return grid;
    }

    private static FrameworkElement Atom(string value) => new TextBlock
    {
        Text = value,
        FontFamily = new FontFamily("Cambria Math"),
        FontSize = 25,
        VerticalAlignment = VerticalAlignment.Center,
        IsTextScaleFactorEnabled = true,
    };

    private FrameworkElement Fraction(FrameworkElement numerator, FrameworkElement denominator)
    {
        var fraction = new Grid { MinWidth = 34, Padding = new Thickness(4, 3, 4, 3), RowSpacing = 2 };
        fraction.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fraction.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
        fraction.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Center(numerator); Center(denominator);
        fraction.Children.Add(numerator);
        var bar = new Border { Height = 1 };
        BindForeground(bar, Border.BackgroundProperty);
        Grid.SetRow(bar, 1); fraction.Children.Add(bar);
        Grid.SetRow(denominator, 2); fraction.Children.Add(denominator);
        return fraction;
    }

    private static FrameworkElement Script(FrameworkElement basis, FrameworkElement superscript, FrameworkElement subscript)
    {
        var grid = new Grid { ColumnSpacing = 1, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Center(basis);
        Grid.SetRow(basis, 1); grid.Children.Add(basis);
        if (superscript is not null)
        {
            var small = Small(superscript);
            small.Margin = new Thickness(0, 0, 0, -8);
            Grid.SetColumn(small, 1); Grid.SetRow(small, 0); grid.Children.Add(small);
        }
        if (subscript is not null)
        {
            var small = Small(subscript);
            small.Margin = new Thickness(0, -8, 0, 0);
            Grid.SetColumn(small, 1); Grid.SetRow(small, 2); grid.Children.Add(small);
        }
        return grid;
    }

    private FrameworkElement Radical(FrameworkElement radicand)
    {
        var grid = new Grid { ColumnSpacing = 1, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var sign = Atom("√");
        Center(radicand);
        Grid.SetRow(sign, 1); Grid.SetColumn(sign, 0); grid.Children.Add(sign);
        var body = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(3, 1, 3, 0), Child = radicand };
        BindForeground(body, Border.BorderBrushProperty);
        Grid.SetRow(body, 1); Grid.SetColumn(body, 1); grid.Children.Add(body);
        return grid;
    }

    private FrameworkElement IndexedRadical(FrameworkElement radicand, FrameworkElement index)
    {
        index = Small(index);
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(index, 0); Grid.SetColumn(index, 0); grid.Children.Add(index);
        var radical = Radical(radicand);
        Grid.SetRowSpan(radical, 2); Grid.SetColumn(radical, 1); grid.Children.Add(radical);
        return grid;
    }

    private FrameworkElement Fenced(XElement element, XElement[] children, int depth, NodeCounter counter)
    {
        string open = (string)element.Attribute("open") ?? "(";
        string close = (string)element.Attribute("close") ?? ")";
        string separators = (string)element.Attribute("separators") ?? ",";
        string separator = separators.Length == 0 ? "," : separators[..1];
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Atom(open));
        for (int index = 0; index < children.Length; index++)
        {
            if (index > 0) row.Children.Add(Atom(separator));
            row.Children.Add(Build(children[index], depth + 1, counter));
        }
        row.Children.Add(Atom(close));
        return row;
    }

    private static void Center(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
    }

    private static FrameworkElement Small(FrameworkElement element) => new Viewbox
    {
        Child = element, Height = 19, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
    };

    private void BindForeground(DependencyObject target, DependencyProperty property) =>
        BindingOperations.SetBinding(target, property, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
}
