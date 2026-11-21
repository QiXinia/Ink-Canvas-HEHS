using InkCanvas.App.Services;
using InkCanvas.Controls.Animation;
using InkCanvas.Features.Classroom;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>
/// 随机点名窗口（纯 code-behind）。名单列表 + 手动添加 + CSV/TXT 导入 + 单次/连续抽取 + 重置本轮。
/// 结果大字显示并带可跳过的滚动动画；名单持久化到 %APPDATA%\InkCanvas\roster.json。
/// </summary>
public sealed class RosterWindow : Window
{
    private static readonly string RosterPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas", "roster.json");

    private readonly ClassroomRoster _roster = new();
    private readonly TextBox _input = new()
    {
        PlaceholderText = "输入姓名后点击「添加」",
        Height = 36,
        MinWidth = 180,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly ListView _list = new()
    {
        Height = 150,
        SelectionMode = ListViewSelectionMode.Single,
    };
    private readonly TextBlock _result = new()
    {
        FontSize = 56,
        FontWeight = FontWeights.SemiBold,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        Text = "—",
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        Opacity = 0.75,
    };
    private readonly TextBlock _error = new()
    {
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 17, 35)),
    };
    private readonly DispatcherQueueTimer _rollTimer;
    private readonly Action<string> _pasteToBoard;
    private Button _pasteButton;
    private string _lastPicked;
    private int _rollTicks;
    private bool _rolling;
    private bool _rollNoRepeat;

    /// <summary>抽取成功后触发，参数为抽中的姓名。</summary>
    public event Action<string> NameDrawn;

    /// <param name="pasteToBoard">可选：把当前抽取结果贴到白板的回调；为 null 时不显示贴板按钮。</param>
    public RosterWindow(Action<string> pasteToBoard = null)
    {
        _pasteToBoard = pasteToBoard;
        Title = "随机点名";
        _roster.Load(RosterPath);
        _roster.Drawn += name => NameDrawn?.Invoke(name);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _rollTimer = dispatcher.CreateTimer();
        _rollTimer.Interval = TimeSpan.FromMilliseconds(55);
        _rollTimer.Tick += (_, _) => OnRollTick();

        var addButton = MakeButton("添加", AddName);
        addButton.MinWidth = 72;
        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        inputRow.Children.Add(_input);
        inputRow.Children.Add(addButton);

        var importButton = MakeButton("导入 CSV/TXT", async () => await ImportAsync());
        var removeButton = MakeButton("移除选中", RemoveSelected);

        var manageRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        manageRow.Children.Add(importButton);
        manageRow.Children.Add(removeButton);

        var drawOnce = MakeButton("单次抽取", () => DrawOnce());
        var drawRound = MakeButton("连续抽取（本轮不重复）", () =>
        {
            if (_rolling) { FinishRoll(); return; }
            StartRoll(noRepeat: true);
        });
        var resetRound = MakeButton("重置本轮", ResetRound);

        var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        actionRow.Children.Add(drawOnce);
        actionRow.Children.Add(drawRound);
        actionRow.Children.Add(resetRound);

        var pasteRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        if (_pasteToBoard is not null)
        {
            _pasteButton = MakeButton("贴到白板", PasteResult);
            _pasteButton.MinWidth = 120;
            _pasteButton.IsEnabled = false;
            pasteRow.Children.Add(_pasteButton);
        }

        var panel = new StackPanel { Spacing = 14, Padding = new Thickness(20), VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(_result);
        panel.Children.Add(_status);
        panel.Children.Add(inputRow);
        panel.Children.Add(_list);
        panel.Children.Add(manageRow);
        panel.Children.Add(actionRow);
        if (pasteRow.Children.Count > 0) panel.Children.Add(pasteRow);
        panel.Children.Add(_error);

        var root = new Border
        {
            Background = (Brush)Application.Current.Resources["IcSurfaceBrush"],
            Child = panel,
        };

        var escape = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { Close(); e.Handled = true; };
        root.KeyboardAccelerators.Add(escape);

        Content = root;

        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.CenterOnPrimary(this, 520, 600);
        RefreshList();
        UpdateStatus();
        Closed += (_, _) => { _rollTimer.Stop(); _roster.Save(RosterPath); };
        Activated += (_, _) => Motion.PlayEntrance(panel, 14f);
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var button = new Button { Content = text, MinHeight = 40, Padding = new Thickness(12, 0, 12, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void AddName()
    {
        if (_roster.Add(_input.Text))
        {
            _input.Text = string.Empty;
            _error.Text = string.Empty;
            RefreshList();
            SaveRoster();
            UpdateStatus();
        }
        else
        {
            _error.Text = string.IsNullOrWhiteSpace(_input.Text) ? "请输入姓名。" : "该姓名已在名单中。";
        }
    }

    private void RemoveSelected()
    {
        if (_list.SelectedItem is not string name) return;
        _roster.Remove(name);
        RefreshList();
        SaveRoster();
        UpdateStatus();
    }

    private async Task ImportAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.FileTypeFilter.Add(".csv");
            picker.FileTypeFilter.Add(".txt");
            IntPtr hwnd = WindowChrome.GetHwnd(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            string error = _roster.ImportCsvOrTxt(file.Path);
            if (error is not null)
            {
                _error.Text = error;
                return;
            }

            _error.Text = string.Empty;
            RefreshList();
            SaveRoster();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            _error.Text = $"导入失败：{ex.Message}";
        }
    }

    private void DrawOnce()
    {
        _error.Text = string.Empty;
        if (_roster.Names.Count == 0)
        {
            _error.Text = "名单为空，请先添加或导入名单。";
            return;
        }

        if (_rolling) FinishRoll();
        string picked = _roster.DrawOne(withoutRepeat: false);
        _result.Text = picked ?? "名单为空";
        SetPicked(picked);
        Motion.Pulse(_result, 1.15f);
        UpdateStatus();
    }

    private void StartRoll(bool noRepeat)
    {
        _error.Text = string.Empty;
        if (_roster.Names.Count == 0)
        {
            _error.Text = "名单为空，请先添加或导入名单。";
            return;
        }
        if (noRepeat && _roster.History.Count >= _roster.Names.Count)
        {
            _error.Text = "本轮已抽完，请点击「重置本轮」。";
            return;
        }

        _rolling = true;
        _rollNoRepeat = noRepeat;
        _rollTicks = 0;
        _rollTimer.Start();
    }

    private void OnRollTick()
    {
        _rollTicks++;
        if (_roster.Names.Count > 0)
        {
            var names = _roster.Names;
            _result.Text = names[Random.Shared.Next(names.Count)];
        }
        if (_rollTicks >= 16) FinishRoll();
    }

    private void FinishRoll()
    {
        _rollTimer.Stop();
        if (!_rolling) return;
        _rolling = false;

        string picked = _roster.DrawOne(_rollNoRepeat);
        _result.Text = picked ?? "本轮已抽完";
        SetPicked(picked);
        Motion.Pulse(_result, 1.15f);
        UpdateStatus();
    }

    private void PasteResult()
    {
        if (_lastPicked is null || _pasteToBoard is null) return;
        _pasteToBoard(_lastPicked);
    }

    private void SetPicked(string picked)
    {
        _lastPicked = string.IsNullOrWhiteSpace(picked) ? null : picked;
        if (_pasteButton is not null)
            _pasteButton.IsEnabled = _lastPicked is not null && _pasteToBoard is not null;
    }

    private void ResetRound()
    {
        _rolling = false;
        _rollTimer.Stop();
        _roster.ResetRound();
        _result.Text = "—";
        _error.Text = string.Empty;
        SetPicked(null);
        UpdateStatus();
    }

    private void RefreshList()
    {
        _list.ItemsSource = null;
        _list.ItemsSource = _roster.Names.ToList();
    }

    private void UpdateStatus()
        => _status.Text = $"名单 {_roster.Names.Count} 人 · 本轮已抽 {_roster.History.Count} 人";

    private void SaveRoster() => _roster.Save(RosterPath);
}