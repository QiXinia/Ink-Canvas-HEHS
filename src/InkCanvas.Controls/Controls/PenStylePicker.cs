using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Controls.Controls;

/// <summary>Compact pen-style choices sharing the palette's live stroke preview.</summary>
public sealed class PenStylePicker : UserControl
{
    private readonly SegmentedSelector _selector = new(OriginalMenuCatalog.PenStyles, columns: 4);
    public event Action SelectionChanged;

    public PenStylePicker()
    {
        AutomationProperties.SetName(this, "笔迹风格");
        string[] descriptions =
        {
            "起收笔较细，保留原有点集笔锋效果。",
            "沿用原有速率笔锋，粗细随采样点间距变化。",
            "保持固定线宽。",
            "动态去抖，粗细变化轻微，适合日常板书。",
            "按实际书写速度模拟压感，慢写较粗、快写较细。",
            "速度压感配合自然起笔和三次曲线收锋。",
            "更明显的粗细、转角和起收笔变化，可搭配斜椭圆笔尖。",
            "毛笔式速度压感与转向变化，淡墨边缘和浓墨笔心，起收笔自然收锋。",
        };
        for (int index = 0; index < OriginalMenuCatalog.PenStyles.Count; index++)
        {
            var icon = new PenStyleIcon(index) { Height = 18, HorizontalAlignment = HorizontalAlignment.Stretch };
            var label = new TextBlock { Text = OriginalMenuCatalog.PenStyles[index], FontSize = 9, Height = 12, LineHeight = 12,
                HorizontalAlignment = HorizontalAlignment.Center, IsTextScaleFactorEnabled = false };
            var content = new Grid { RowDefinitions = { new RowDefinition { Height = new GridLength(18) },
                new RowDefinition { Height = new GridLength(12) } }, Children = { icon, label } };
            Grid.SetRow(label, 1);
            _selector.SetOptionContent(index, content, minHeight: 32);
            _selector.SetOptionHelp(index, OriginalMenuCatalog.PenStyles[index] + "\n" + descriptions[index]);
        }
        _selector.SelectionChanged += () => SelectionChanged?.Invoke();
        Content = _selector;
        Update(1);
    }

    public int SelectedIndex { get => _selector.SelectedIndex; set => _selector.SelectedIndex = value; }

    public void Update(int theme)
    {
        RequestedTheme = theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
    }
}
