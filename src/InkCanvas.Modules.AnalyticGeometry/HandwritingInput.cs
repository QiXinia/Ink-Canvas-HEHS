using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Input.Inking;

namespace InkCanvas.Modules.AnalyticGeometry;

/// <summary>WinUI ink entry with Windows offline handwriting candidates.</summary>
public sealed class HandwritingInput : UserControl, IDisposable
{
    private readonly InkController _controller = new() { RecognizeShapes = false, MultiTouchEnabled = true, PalmErases = false };
    private readonly InkSurface _surface;
    private readonly ComboBox _language = new() { MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _candidates = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _recognize = new() { Content = "识别", MinHeight = 44, MinWidth = 80 };
    private InkRecognizerContainer _recognizer;
    private IReadOnlyList<InkRecognizer> _recognizers = Array.Empty<InkRecognizer>();
    private readonly CancellationTokenSource _lifetime = new();
    private int _version;
    private bool _busy, _disposed;
    public event Action<string> CandidateChosen;
    public bool RecognitionAvailable => _recognizers.Count > 0;
    public InkController Controller => _controller;

    public HandwritingInput()
    {
        _controller.CurrentAttributes.BaseWidth = 3; _controller.CurrentAttributes.Color = InkColor.Black; _controller.CurrentAttributes.TaperKind = TaperKind.None;
        _surface = new InkSurface { Controller = _controller, Height = 160, ClearColor = Microsoft.UI.Colors.White };
        _controller.Document.Changed += OnChanged;
        var clear = new Button { Content = "清空", MinHeight = 44, MinWidth = 80 };
        clear.Click += (_, _) => { _controller.Clear(); _candidates.Children.Clear(); };
        _recognize.Click += async (_, _) => await RecognizeAsync();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _recognize, clear } };
        Content = new StackPanel { Spacing = 8, Children = { _language,
            new Border { Child = _surface, BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray), CornerRadius = new CornerRadius(6) }, actions,
            new ScrollViewer { Content = _candidates, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, _status } };
        AutomationProperties.SetName(_surface, "公式手写区");
        Loaded += (_, _) => InitializeRecognition();
    }
    private void InitializeRecognition()
    {
        if (_disposed || _recognizer is not null) return;
        try
        {
            _recognizer = new InkRecognizerContainer(); _recognizers = _recognizer.GetRecognizers();
            _language.ItemsSource = _recognizers.Select(recognizer => recognizer.Name).ToArray();
            _language.SelectedIndex = _recognizers.Count == 0 ? -1 : 0;
            _recognize.IsEnabled = _recognizers.Count > 0;
            _status.Text = _recognizers.Count > 0 ? "手写后点击识别，再选择候选。复杂分式和幂可用公式键盘补充。" : "Windows 未安装手写识别语言包，可在系统语言选项中安装手写组件，或使用公式键盘。";
        }
        catch (Exception ex) { _recognize.IsEnabled = false; _status.Text = "Windows 手写识别不可用 · " + ex.GetBaseException().Message; }
    }
    private void OnChanged() { _version++; _candidates.Children.Clear(); }
    public async Task RecognizeAsync()
    {
        if (_disposed || _busy || !RecognitionAvailable || _controller.Document.Count == 0 || _controller.HasActiveInteraction) return;
        _busy = true; _recognize.IsEnabled = false; int version = _version;
        try
        {
            _status.Text = "正在本地识别…";
            var container = new InkStrokeContainer(); var builder = new InkStrokeBuilder();
            foreach (var stroke in _controller.Document.Strokes)
            {
                var points = stroke.Points.Select(point => new Windows.Foundation.Point(point.X, point.Y)).ToList();
                if (points.Count == 1) points.Add(new(points[0].X + .1, points[0].Y));
                if (points.Count > 1) container.AddStroke(builder.CreateStroke(points));
            }
            _recognizer.SetDefaultRecognizer(_recognizers[Math.Max(0, _language.SelectedIndex)]);
            var results = await _recognizer.RecognizeAsync(container, InkRecognitionTarget.All).AsTask(_lifetime.Token);
            if (_disposed || _version != version) return;
            var ordered = results.OrderBy(item => item.BoundingRect.Y / Math.Max(20, item.BoundingRect.Height)).ThenBy(item => item.BoundingRect.X).ToArray();
            var strings = new List<string> { string.Concat(ordered.Select(item => item.GetTextCandidates().FirstOrDefault() ?? "")) };
            for (int index = 0; index < ordered.Length && strings.Count < 6; index++)
            foreach (string alternative in ordered[index].GetTextCandidates().Skip(1).Take(2))
            {
                int changed = index;
                strings.Add(string.Concat(ordered.Select((item, i) => i == changed ? alternative : item.GetTextCandidates().FirstOrDefault() ?? "")));
                if (strings.Count >= 6) break;
            }
            _candidates.Children.Clear();
            foreach (string candidate in strings.Select(NormalizeCandidate).Where(text => text.Length > 0).Distinct())
            {
                string value = candidate;
                var button = new Button { Content = value, MinHeight = 44, MinWidth = 70, AllowFocusOnInteraction = false };
                button.Click += (_, _) => CandidateChosen?.Invoke(value); _candidates.Children.Add(button);
            }
            _status.Text = _candidates.Children.Count > 0 ? "选择候选可替换当前公式。" : "未得到候选，请重新书写或用公式键盘输入。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) _status.Text = "识别失败 · " + ex.GetBaseException().Message; }
        finally { _busy = false; if (!_disposed) _recognize.IsEnabled = RecognitionAvailable; }
    }
    private static string NormalizeCandidate(string value)
        => value.Replace(" ", "").Replace("×", "*").Replace("÷", "/").Replace("−", "-").Replace("²", "^2").Replace("³", "^3");
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _lifetime.Cancel();
        _controller.Document.Changed -= OnChanged; _surface.Dispose(); _controller.Dispose();
        _recognizer = null;
    }
}
