using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

/// <summary>Activatable colour editor so RGB and HEX fields accept keyboard and touch input.</summary>
internal sealed class ColorEditorWindow : Window
{
    private readonly TaskCompletionSource<InkColor?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ColorPicker _picker;
    public Task<InkColor?> Result => _completion.Task;

    public ColorEditorWindow(InkColor color, int theme)
    {
        Title = "编辑自定义颜色 - Ink Canvas";
        _picker = new ColorPicker
        {
            Color = Windows.UI.Color.FromArgb(255, color.R, color.G, color.B),
            IsAlphaEnabled = false, IsAlphaSliderVisible = false, IsAlphaTextInputVisible = false,
            IsHexInputVisible = true, IsColorChannelTextInputVisible = true, IsMoreButtonVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(_picker, "自定义颜色取色器");
        var save = new Button { Content = "保存颜色", MinWidth = 120, MinHeight = 44,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        AutomationProperties.SetName(save, "保存自定义颜色");
        save.Click += (_, _) =>
        {
            var chosen = _picker.Color;
            _completion.TrySetResult(InkColor.Opaque(chosen.R, chosen.G, chosen.B));
            Close();
        };
        var cancel = new Button { Content = "取消", MinWidth = 100, MinHeight = 44 };
        cancel.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        actions.Children.Add(cancel); actions.Children.Add(save);
        var root = new Grid { Padding = new Thickness(20) };
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(new ScrollViewer { Content = _picker, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetRow(actions, 1); root.Children.Add(actions);
        Content = root;
        WindowChrome.MakeStandard(this, 400, 650);
        WindowFrame.Attach(this, root).UseMica();
        WindowChrome.ApplyTheme(this, theme);
        var minimum = new MinSizeHook(this, 340, 420);
        Closed += (_, _) => { minimum.Dispose(); _completion.TrySetResult(null); };
    }
}
