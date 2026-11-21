using InkCanvas.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.Controls.Controls;

/// <summary>Finite-width board gesture settings; native switch and combo layouts remain intact.</summary>
public sealed class BoardGesturePanel : UserControl
{
    private readonly GestureSettings _settings;
    private readonly List<ToggleSwitch> _gestures = new();
    private readonly TextBlock _hint;
    private readonly bool _automatic;
    public event Action Changed;
    public PopupHeader Header { get; }

    public BoardGesturePanel(GestureSettings settings, int theme) : this(settings, theme, false) { }

    public BoardGesturePanel(GestureSettings settings, int theme, bool automatic)
    {
        _automatic = automatic;
        _settings = settings;
        var foreground = new SolidColorBrush(theme == 2 ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(12, 10, 12, 12) };
        Add("多指书写", !automatic && settings.IsEnableMultiTouchMode, value => { settings.IsEnableMultiTouchMode = value; RefreshAvailability(); }, automaticControl: true);
        _hint = new TextBlock { Foreground = foreground, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .75 };
        panel.Children.Add(_hint);
        Add("双指移动", automatic || settings.IsEnableTwoFingerTranslate, value => settings.IsEnableTwoFingerTranslate = value, true, automaticControl: true);
        Add("双指缩放", settings.IsEnableTwoFingerZoom, value => settings.IsEnableTwoFingerZoom = value, true);
        Add("双指旋转", settings.IsEnableTwoFingerRotation, value => settings.IsEnableTwoFingerRotation = value, true);
        panel.Children.Add(new Border { Height = 1, Background = foreground, Opacity = .2 });
        Add("双指旋转选区", settings.IsEnableTwoFingerRotationOnSelection,
            value => settings.IsEnableTwoFingerRotationOnSelection = value);
        var center = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
        foreach (var label in new[] { "画布中心", "选中内容中心", "双指中心" }) center.Items.Add(label);
        center.SelectedIndex = Math.Clamp((int)settings.MatrixTransformCenterPoint, 0, 2);
        AutomationProperties.SetName(center, "变换中心点");
        center.SelectionChanged += (_, _) =>
        {
            if (center.SelectedIndex < 0) return;
            settings.MatrixTransformCenterPoint = (MatrixTransformCenterPointOptions)center.SelectedIndex;
            Changed?.Invoke();
        };
        panel.Children.Add(new TextBlock { Text = "变换中心点", FontSize = 12, Foreground = foreground });
        panel.Children.Add(center);
        var root = new StackPanel();
        Header = new PopupHeader("手势功能", theme);
        root.Children.Add(Header);
        root.Children.Add(panel);
        Content = new Border { Width = 312, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = Theming.PopupBrushes.ToolPanelBorder(theme),
            Background = new SolidColorBrush(theme == 2 ? Windows.UI.Color.FromArgb(245, 20, 20, 20) : Windows.UI.Color.FromArgb(245, 255, 255, 255)), Child = root };
        RefreshAvailability();

        void Add(string label, bool initial, Action<bool> update, bool gesture = false, bool automaticControl = false)
        {
            var row = new Grid { ColumnSpacing = 10, MinHeight = 32 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new TextBlock { Text = label, Foreground = foreground, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var toggle = new ToggleSwitch { IsOn = initial, OnContent = "", OffContent = "", MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, label);
            if (automatic && automaticControl)
            {
                toggle.IsEnabled = false;
                AutomationProperties.SetHelpText(toggle, "由进入白板时自动切换手势控制，可在详细设置中关闭自动切换。");
            }
            if (gesture) _gestures.Add(toggle);
            toggle.Toggled += (_, _) => { if (toggle.IsEnabled) { update(toggle.IsOn); Changed?.Invoke(); } };
            Grid.SetColumn(toggle, 1); row.Children.Add(text); row.Children.Add(toggle); panel.Children.Add(row);
        }
    }

    private void RefreshAvailability()
    {
        foreach (var toggle in _gestures) toggle.IsEnabled = (_automatic || !_settings.IsEnableMultiTouchMode)
            && !(_automatic && AutomationProperties.GetName(toggle) == "双指移动");
        if (_hint is not null) _hint.Text = _automatic ? "已自动关闭多指书写并开启双指移动，可在详细设置中关闭自动切换。"
            : _settings.IsEnableMultiTouchMode ? "选择对象后可用双指移动和缩放；选区开关控制自由旋转。"
                : "双指移动、缩放或旋转画布；选区开关控制双指旋转选中对象。";
    }
}
