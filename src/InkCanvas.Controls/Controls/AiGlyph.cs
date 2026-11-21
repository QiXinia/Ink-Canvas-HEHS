using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace InkCanvas.Controls.Controls;

/// <summary>“AI”标记的尺寸档位：字号、字重与字距（1/1000 em）。</summary>
public readonly record struct AiGlyphPreset(double FontSize, FontWeight Weight, int CharacterSpacing);

/// <summary>
/// “AI”助教标记的唯一来源。调用点不得自行拼写文本或指定字号 / 字重，只能请求尺寸档位，
/// 从而保证 16 / 20 / 24 等实际尺寸下的字重、字距、基线与对齐一致。
/// <para>
/// 标记以文本图元实现（不引入位图资源），固定度量盒承载，避免替换旧问号图标后引发布局抖动。
/// 颜色由调用方传入主题画刷；未传时继承父级前景色，亮 / 暗主题切换无需重建控件。
/// </para>
/// </summary>
public static class AiGlyph
{
    /// <summary>标记文本；渲染层与无障碍名称共用同一字面量。</summary>
    public const string Text = "AI";

    /// <summary>主浮条 / 触控按钮内的标记字号（40×40 按钮）。</summary>
    public const double ButtonLabelSize = 12;

    /// <summary>标题前缀、缩略图角标等徽标字号。</summary>
    public const double BadgeLabelSize = 10;

    /// <summary>工具箱磁贴中标记盒的默认宽度。</summary>
    public const double ToolTileWidth = 24;

    /// <summary>工具箱磁贴中标记盒的默认高度，与同排 18px 字体图标行高对齐。</summary>
    public const double ToolTileHeight = 22;

    /// <summary>需要逐一适配的实际图标盒尺寸。</summary>
    public static readonly IReadOnlyList<double> IconBoxSizes = new[] { 16d, 20d, 24d };

    /// <summary>
    /// 按目标盒尺寸返回档位：小尺寸放大字距保持可读，24px 收紧字距避免字母黏连；
    /// 字重统一 SemiBold，只调整字号与字距，不堆叠装饰。
    /// </summary>
    public static AiGlyphPreset PresetFor(double boxSize)
    {
        if (!double.IsFinite(boxSize) || boxSize <= 0) boxSize = 20;
        if (boxSize <= 16) return new AiGlyphPreset(11, FontWeights.SemiBold, 40);
        if (boxSize <= 20) return new AiGlyphPreset(13, FontWeights.SemiBold, 20);
        if (boxSize <= 24) return new AiGlyphPreset(15, FontWeights.SemiBold, 0);
        return new AiGlyphPreset(Math.Round(boxSize * 0.66), FontWeights.SemiBold, -20);
    }

    /// <summary>按字号返回档位（徽标、按钮等已知字号的场景）。</summary>
    public static AiGlyphPreset PresetForLabel(double fontSize)
    {
        if (!double.IsFinite(fontSize) || fontSize <= 0) fontSize = BadgeLabelSize;
        int spacing = fontSize switch
        {
            <= 10 => 40,
            <= 12 => 20,
            <= 15 => 0,
            <= 17 => -10,
            _ => -20,
        };
        return new AiGlyphPreset(fontSize, FontWeights.SemiBold, spacing);
    }

    /// <summary>创建纯文本标记；<paramref name="foreground"/> 为空时继承父级前景色。</summary>
    public static TextBlock CreateLabel(double fontSize, Brush foreground = null)
    {
        AiGlyphPreset preset = PresetForLabel(fontSize);
        var text = new TextBlock
        {
            Text = Text,
            FontSize = preset.FontSize,
            FontWeight = preset.Weight,
            CharacterSpacing = preset.CharacterSpacing,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (foreground is not null) text.Foreground = foreground;
        return text;
    }

    /// <summary>
    /// 创建占据固定度量盒的标记（推荐用于图标位）。盒尺寸不变，只调整内部字号与字距，
    /// 因此替换旧图标不会改变按钮尺寸、点击区域或行高。
    /// </summary>
    public static Grid CreateFramed(double boxWidth, double boxHeight, Brush foreground = null)
    {
        double nominal = Math.Min(boxWidth, boxHeight);
        AiGlyphPreset preset = PresetFor(nominal);
        var host = new Grid
        {
            Width = boxWidth,
            Height = boxHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = CreateLabel(preset.FontSize, foreground);
        label.CharacterSpacing = preset.CharacterSpacing;
        host.Children.Add(label);
        return host;
    }

    /// <summary>正方形度量盒重载。</summary>
    public static Grid CreateFramed(double boxSize, Brush foreground = null)
        => CreateFramed(boxSize, boxSize, foreground);
}