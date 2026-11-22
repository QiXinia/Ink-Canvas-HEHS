using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.Stylus;

internal sealed class StylusSettingsView : UserControl
{
    private static readonly string[] Actions = { "不处理", "画笔", "荧光笔", "面积橡皮", "整笔橡皮", "选择", "形状工具", "拖动画布", "撤销", "重做", "增大笔宽", "减小笔宽" };
    private static readonly string[] Modes = { "按住临时切换", "固定切换", "再次按下还原" };
    private readonly StylusService _service;
    private readonly StackPanel _rows = new() { Spacing = 16 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly Button _learn = new() { MinHeight = 40 };
    private bool _loaded;

    public StylusSettingsView(StylusService service)
    {
        _service = service;
        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(_rows);
        _learn.Click += (_, _) =>
        {
            if (_service.IsLearning) _service.EndLearning();
            else _service.BeginLearning(signal =>
            {
                if (!_service.Settings.Bindings.Any(binding => binding.Input == signal.Input) && _service.Settings.Bindings.Count < 64)
                    _service.Settings.Bindings.Add(new(signal.Input, signal.Name, StylusAction.Undo, StylusSwitchMode.Permanent));
                _service.Configure();
                // Keyboard hook callbacks return before XAML can rebuild their initiating controls.
                DispatcherQueue.TryEnqueue(BuildRows);
            });
            Refresh();
        };
        panel.Children.Add(_learn); panel.Children.Add(_status);
        Content = panel;
        Loaded += (_, _) => { if (_loaded) return; _loaded = true; _service.Changed += Refresh; Refresh(); };
        Unloaded += (_, _) => { _loaded = false; _service.Changed -= Refresh; _service.EndLearning(); };
        BuildRows();
    }

    private void BuildRows()
    {
        _rows.Children.Clear();
        foreach (var binding in _service.Settings.Bindings.ToArray())
        {
            var panel = new StackPanel { Spacing = 8 };
            var header = new Grid();
            header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new TextBlock { Text = binding.Name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            if (!binding.Input.StartsWith("pen:", StringComparison.Ordinal))
            {
                var delete = new TouchIconButton { Glyph = Glyphs.Delete, Width = 40, Height = 40 };
                ToolTipService.SetToolTip(delete, "删除按钮映射");
                delete.Click += (_, _) =>
                {
                    _service.Settings.Bindings.RemoveAll(item => item.Input == binding.Input);
                    _service.Configure(); BuildRows();
                };
                Grid.SetColumn(delete, 1); header.Children.Add(delete);
            }
            panel.Children.Add(header);
            var editors = new Grid { ColumnSpacing = 12 };
            editors.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            editors.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var action = new ComboBox { ItemsSource = Actions, SelectedIndex = (int)binding.Action, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0 };
            var mode = new ComboBox { ItemsSource = Modes, SelectedIndex = (int)binding.Mode, HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 0,
                IsEnabled = binding.Action is >= StylusAction.Pen and <= StylusAction.Pan };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(action, binding.Name + "操作");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(mode, binding.Name + "切换方式");
            void Save()
            {
                int index = _service.Settings.Bindings.FindIndex(item => item.Input == binding.Input);
                if (index < 0 || action.SelectedIndex < 0 || mode.SelectedIndex < 0) return;
                var picked = (StylusAction)action.SelectedIndex;
                _service.Settings.Bindings[index] = binding with { Action = picked, Mode = (StylusSwitchMode)mode.SelectedIndex };
                mode.IsEnabled = picked is >= StylusAction.Pen and <= StylusAction.Pan;
                _service.Configure();
            }
            action.SelectionChanged += (_, _) => Save();
            mode.SelectionChanged += (_, _) => Save();
            editors.Children.Add(action); Grid.SetColumn(mode, 1); editors.Children.Add(mode);
            panel.Children.Add(editors); _rows.Children.Add(panel);
        }
        Refresh();
    }

    private void Refresh()
    {
        _learn.Content = _service.IsLearning ? "取消学习" : "学习数位板 / 驱动按钮";
        _status.Text = _service.IsLearning ? "请在 15 秒内按下按钮或组合键，Esc 取消。" : "可学习键盘键、组合键、鼠标右键 / 中键 / 扩展键及标准 HID 按钮。";
        _learn.IsEnabled = _service.Settings.Enabled;
    }
}
