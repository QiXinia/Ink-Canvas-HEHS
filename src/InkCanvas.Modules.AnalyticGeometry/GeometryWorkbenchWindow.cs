using System.Globalization;
using System.Numerics;
using System.Text.Json;
using InkCanvas.AnalyticGeometry;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Host.Contracts;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed partial class GeometryWorkbenchWindow : Window
{
    private readonly ICanvasToolsHost _host;
    private CanvasToolTarget _target;
    private IReadOnlyList<InkStroke> _editing = Array.Empty<InkStroke>();
    private readonly object _previewOwner = new();
    private readonly Grid _root = new() { RowSpacing = 8 };
    private readonly ConstructionKernel _kernel = new();
    private readonly ConstructionView _view = new();
    private readonly ListView _objects = new() { SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Dictionary<string, (ListViewItem Item, TextBlock Value, CheckBox Visible)> _rows = new();
    private readonly StackPanel _properties = new() { Spacing = 8, Padding = new Thickness(12) };
    private readonly StackPanel _casHistory = new() { Spacing = 12, Padding = new Thickness(12) };
    private readonly TextBox _input = FormulaBox("输入表达式或指令，例如 A=(1,2)、f(x)=a*x^2、Solve(x^2=2,x)");
    private readonly MathFormulaView _inputMath = new() { Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _mathDebounce;
    private TextBox _focused;
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 12, 8) };
    private readonly Button _insert = new() { Content = "插入画布", MinHeight = 44 };
    private readonly CheckBox _live = new() { Content = "实时预览到画布", MinHeight = 40 };
    private readonly ComboBox _tools = new() { MinHeight = 44, MinWidth = 140 };
    private readonly SegmentedSelector _mode = new(new[] { "2D", "3D" }) { Width = 116, Height = 44 };
    private bool _syncingView;
    private readonly DispatcherQueueTimer _debounce, _animation;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _calculation;
    private Task _pending = Task.CompletedTask;
    private bool _closed, _updatingRows, _casBusy, _playing;
    private int _renderedRevision = -1;
    private int _protocolStep = int.MaxValue;
    private int _suppressScriptsRevision = -1;
    private DateTime _animationTime;
    private readonly List<string> _toolPoints = new();
    public ConstructionDocument Document { get; } = new();
    public ConstructionEvaluation Evaluation { get; private set; }
    public AlgebraResult LastCasResult { get; private set; }
    public FormulaKeyboard Keyboard { get; } = new();
    public HandwritingInput Handwriting { get; } = new();
    public string StatusText => _status.Text;
    public TextBox FormulaInput => _input;
    public string FormulaMathML => _inputMath.MathML;
    public bool FormulaUsesRichEdit => _inputMath.UsesRichEdit;

    public GeometryWorkbenchWindow(ICanvasToolsHost host)
    {
        _host = host;
        Title = "数学工作区 — Ink Canvas";
        _mathDebounce = DispatcherQueue.CreateTimer();
        _mathDebounce.IsRepeating = false;
        _mathDebounce.Interval = TimeSpan.FromMilliseconds(100);
        _mathDebounce.Tick += (_, _) => UpdateMathPreview();
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.IsRepeating = false;
        _debounce.Interval = TimeSpan.FromMilliseconds(28);
        _debounce.Tick += (_, _) => _pending = CalculateCoreAsync();
        _animation = DispatcherQueue.CreateTimer();
        _animation.Interval = TimeSpan.FromMilliseconds(40);
        _animation.Tick += (_, _) => Animate();
        BuildInterface();
        Content = _root;
        host.ConfigureWindow(this, 1360, 940);
        WindowFrame.Attach(this, _root);
        Document.Changed += QueueCalculation;
        _view.GestureStarted += Document.BeginGesture;
        _view.GestureEnded += Document.EndGesture;
        _view.ViewChanged += Document.SetView;
        _view.PointMoved += (label, point) => Document.Update(label, item => item with { Definition = Coordinates(point) });
        _view.Clicked += GraphClicked;
        _view.ControlChanged += (label, definition) => Guard(() => Document.Update(label, item => item with { Definition = definition }));
        _view.ControlClicked += label => RunEventScript(Document.State.Objects.First(item => item.Label == label).OnClick);
        _objects.SelectionChanged += (_, _) => SelectObject((_objects.SelectedItem as ListViewItem)?.Tag as string);
        _root.ActualThemeChanged += (_, _) => { if (Evaluation is not null) RefreshObjects(); };
        _host.TargetChanged += TargetChanged;
        Closed += (_, _) => DisposeWorkbench();
        CaptureTarget();
        if (Document.State.Objects.Count == 0) Document.Execute("a=1;f(x)=a*x^2;A=(-2,1);B=(2,1);s=Segment(A,B);M=Midpoint(A,B)");
    }

    private void BuildInterface()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toolbar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsOpen = false };
        AppBarButton Action(string label, Symbol symbol, Action action)
        {
            var button = new AppBarButton { Label = label, Icon = new SymbolIcon(symbol) };
            button.Click += (_, _) => Guard(action);
            toolbar.PrimaryCommands.Add(button);
            return button;
        }
        Action("新建", Symbol.Add, () => { StopAnimation(); _protocolStep = int.MaxValue; _view.SelectedLabel = null; Document.Load(new()); _editing = Array.Empty<InkStroke>(); _insert.Content = "插入画布"; });
        Action("打开", Symbol.OpenFile, () => RunFileAction(OpenAsync));
        Action("保存", Symbol.Save, () => RunFileAction(SaveAsync));
        Action("撤销", Symbol.Undo, () => { StopAnimation(); Document.Undo(); });
        Action("重做", Symbol.Redo, () => { StopAnimation(); Document.Redo(); });
        Action("播放参数", Symbol.Play, ToggleAnimation);
        var export = new AppBarButton { Label = "导出", Icon = new SymbolIcon(Symbol.Share) };
        var formats = new MenuFlyout();
        foreach (var format in new[] { "SVG", "PNG", "PDF", "TikZ", "STL" })
        {
            string value = format;
            var menu = new MenuFlyoutItem { Text = format };
            menu.Click += (_, _) => RunFileAction(() => ExportAsync(value));
            formats.Items.Add(menu);
        }
        export.Flyout = formats;
        toolbar.PrimaryCommands.Add(export);
        var capture = new AppBarButton { Label = "使用当前画布 / 所选构造", Icon = new SymbolIcon(Symbol.Switch) };
        capture.Click += (_, _) => Guard(CaptureTarget);
        toolbar.SecondaryCommands.Add(capture);
        var help = new AppBarButton { Label = "指令与示例", Icon = new SymbolIcon(Symbol.Help) };
        help.Click += (_, _) => ShowCommandHelp(); toolbar.SecondaryCommands.Add(help);
        _root.Children.Add(toolbar);

        var body = new Grid { ColumnSpacing = 8 };
        body.ColumnDefinitions.Add(new() { Width = new GridLength(370) });
        body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); _root.Children.Add(body);
        var left = new Grid();
        left.RowDefinitions.Add(new() { Height = GridLength.Auto });
        left.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var pages = new SegmentedSelector(new[] { "代数", "对象属性", "CAS" }) { Height = 42, Margin = new Thickness(12, 0, 0, 0) };
        left.Children.Add(pages);
        var propertyScroll = new ScrollViewer { Content = _properties };
        var casScroll = new ScrollViewer { Content = _casHistory };
        foreach (var element in new FrameworkElement[] { _objects, propertyScroll, casScroll })
        { Grid.SetRow(element, 1); left.Children.Add(element); }
        pages.SelectionChanged += () =>
        {
            _objects.Visibility = pages.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            propertyScroll.Visibility = pages.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            casScroll.Visibility = pages.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        pages.SelectedIndex = 0;
        _properties.Children.Add(new TextBlock { Text = "选择一个对象以编辑定义、外观与参数。", TextWrapping = TextWrapping.Wrap });
        _casHistory.Children.Add(new TextBlock { Text = "支持精确化简、展开、因式分解、方程与方程组、微积分、矩阵和复数计算。输入后点击“CAS 计算”。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(left);

        var right = new Grid { RowSpacing = 6, Margin = new Thickness(0, 0, 12, 0) };
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var graphTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _tools.ItemsSource = DrawingTools.Select(tool => tool.Name).ToArray(); _tools.SelectedIndex = 0;
        _tools.SelectionChanged += (_, _) =>
        {
            _toolPoints.Clear(); _view.ConstructionToolActive = _tools.SelectedIndex > 0;
            SetStatus(DrawingTools[Math.Max(0, _tools.SelectedIndex)].Help);
        };
        graphTools.Children.Add(_tools);
        var commands = new Button { Content = "指令", MinHeight = 44 };
        commands.Click += (_, _) => ShowCommandHelp(); graphTools.Children.Add(commands);
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += () => { if (!_syncingView) Document.SetView(Document.State with { ThreeDimensional = _mode.SelectedIndex == 1 }); };
        graphTools.Children.Add(_mode);
        var options = new Button { Content = "视图", MinHeight = 44 };
        var viewMenu = new MenuFlyout();
        void ViewOption(string label, Func<ConstructionSnapshot, ConstructionSnapshot> update)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => Document.SetView(update(Document.State)); viewMenu.Items.Add(item);
        }
        ViewOption("显示 / 隐藏网格", state => state with { Grid = !state.Grid });
        ViewOption("显示 / 隐藏坐标轴", state => state with { Axes = !state.Axes });
        ViewOption("透视 / 正交投影", state => state with { Perspective = !state.Perspective, Oblique = false });
        ViewOption("倾斜投影", state => state with { Oblique = !state.Oblique, Perspective = false });
        ViewOption("极坐标 / 直角坐标网格", state => state with { PolarGrid = !state.PolarGrid });
        var dragZ = new ToggleMenuFlyoutItem { Text = "3D 拖点沿 Z 轴移动" };
        dragZ.Click += (_, _) => _view.MoveAlongZAxis = dragZ.IsChecked; viewMenu.Items.Add(dragZ);
        ViewOption("重置视图", state => state with { XMin = -10, XMax = 10, YMin = -7.5, YMax = 7.5, Yaw = -.6, Pitch = .65 });
        options.Flyout = viewMenu; graphTools.Children.Add(options);
        right.Children.Add(new ScrollViewer { Content = graphTools, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled });
        var frame = new Border { Child = _view, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray) };
        Grid.SetRow(frame, 1); right.Children.Add(frame);
        var footer = new Grid { ColumnSpacing = 10 };
        footer.ColumnDefinitions.Add(new()); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(_live); Grid.SetColumn(_insert, 1); footer.Children.Add(_insert);
        Grid.SetRow(footer, 2); right.Children.Add(footer);
        Grid.SetColumn(right, 1); body.Children.Add(right);
        _insert.Click += (_, _) => InsertShape();
        _live.Checked += (_, _) => PreviewCanvas();
        _live.Unchecked += (_, _) => _target?.Controller.ClearExtensionPreview(_previewOwner);

        var entry = new Grid { Margin = new Thickness(12, 0, 12, 0), ColumnSpacing = 8, RowSpacing = 8 };
        entry.ColumnDefinitions.Add(new());
        entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        entry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        entry.RowDefinitions.Add(new() { Height = GridLength.Auto });
        entry.RowDefinitions.Add(new() { Height = GridLength.Auto });
        entry.RowDefinitions.Add(new() { Height = GridLength.Auto });
        entry.Children.Add(_input); _focused = _input;
        var execute = new Button { Content = "创建 / 更新", MinHeight = 48 };
        execute.Click += (_, _) => ExecuteInput(); Grid.SetColumn(execute, 1); entry.Children.Add(execute);
        var cas = new Button { Content = "CAS 计算", MinHeight = 48 };
        cas.Click += async (_, _) => { pages.SelectedIndex = 2; await CalculateCasAsync(); };
        Grid.SetColumn(cas, 2); entry.Children.Add(cas);
        _input.KeyDown += (_, args) => { if (args.Key == VirtualKey.Enter) { ExecuteInput(); args.Handled = true; } };
        _input.GotFocus += (_, _) => _focused = _input;
        _input.TextChanged += (_, _) => { _mathDebounce.Stop(); _mathDebounce.Start(); };
        Grid.SetRow(_inputMath, 1); Grid.SetColumnSpan(_inputMath, 3); entry.Children.Add(_inputMath);
        var inputPanel = new Grid();
        var inputMethod = new SegmentedSelector(new[] { "数学键盘", "手写输入" }) { Width = 240, Height = 44, HorizontalAlignment = HorizontalAlignment.Left };
        inputMethod.SetOptionContent(0, new TextBlock { Text = "数学键盘", VerticalAlignment = VerticalAlignment.Center }, 44);
        inputMethod.SetOptionContent(1, new TextBlock { Text = "手写输入", VerticalAlignment = VerticalAlignment.Center }, 44);
        inputPanel.Children.Add(Keyboard);
        inputPanel.Children.Add(Handwriting);
        inputMethod.SelectionChanged += () =>
        {
            Keyboard.Visibility = inputMethod.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            Handwriting.Visibility = inputMethod.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        };
        inputMethod.SelectedIndex = 0;
        var inputScroll = new ScrollViewer { Content = inputPanel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var keyboard = new Expander { Header = inputMethod, Content = inputScroll, HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(keyboard, "数学键盘与手写输入");
        inputMethod.SelectionChanged += () => keyboard.IsExpanded = true;
        Grid.SetRow(keyboard, 2); Grid.SetColumnSpan(keyboard, 3); entry.Children.Add(keyboard);
        Keyboard.KeyPressed += EnterKey;
        Handwriting.CandidateChosen += value => { var box = _focused ?? _input; box.Text = value; box.SelectionStart = value.Length; };
        Grid.SetRow(entry, 2); _root.Children.Add(entry);
        Grid.SetRow(_status, 3); _root.Children.Add(_status);
        _root.SizeChanged += (_, _) =>
        {
            body.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(_root.ActualWidth * .3, 270, 390));
            inputScroll.MaxHeight = Math.Clamp(_root.ActualHeight * .38, 160, 360);
        };
    }

    private static TextBox FormulaBox(string placeholder)
    {
        var box = new TextBox { PlaceholderText = placeholder, MinHeight = 48, FontFamily = new FontFamily("Cambria Math"), FontSize = 19,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, PreventKeyboardDisplayOnProgrammaticFocus = true, MaxLength = 4096 };
        AutomationProperties.SetName(box, placeholder);
        return box;
    }

    private void ExecuteInput()
    {
        Guard(() => { string label = Document.Execute(_input.Text); _view.SelectedLabel = label; _input.Text = ""; });
    }

    public string Execute(string input) => Document.Execute(input);

    private void QueueCalculation()
    {
        if (_closed) return;
        _syncingView = true;
        _mode.SelectedIndex = Document.State.ThreeDimensional ? 1 : 0;
        _syncingView = false;
        _calculation?.Cancel();
        _insert.IsEnabled = false;
        _debounce.Stop(); _debounce.Start();
    }

    public async Task CalculateAsync()
    {
        _debounce.Stop();
        _pending = CalculateCoreAsync();
        while (!_closed)
        {
            var pending = _pending;
            await pending;
            if (_debounce.IsRunning) { _debounce.Stop(); _pending = CalculateCoreAsync(); continue; }
            if (ReferenceEquals(pending, _pending)) return;
        }
    }

    private Task _worker = Task.CompletedTask;
    private async Task CalculateCoreAsync()
    {
        if (_closed) return;
        _debounce.Stop();
        _calculation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(8));
        _calculation = cancellation;
        int revision = Document.Revision;
        var snapshot = Document.State;
        var previous = _worker;
        try
        {
            var operation = Task.Run(async () =>
            {
                try { await previous; } catch (Exception) { }
                cancellation.Token.ThrowIfCancellationRequested();
                return _kernel.Evaluate(snapshot, cancellation.Token);
            }, cancellation.Token);
            _worker = operation;
            var result = await operation;
            if (_closed || cancellation.IsCancellationRequested || revision != Document.Revision) return;
            var previousEvaluation = Evaluation;
            Evaluation = result; _renderedRevision = revision;
            RefreshObjects(); RenderView();
            _insert.IsEnabled = CanInsert();
            PreviewCanvas();
            int errors = result.Values.Count(pair => !pair.Value.Defined);
            SetStatus($"{snapshot.Objects.Count} 个对象 · 本次重算 {result.EvaluatedObjects} 个 · {result.Elapsed.TotalMilliseconds:F0} ms" + (errors > 0 ? $" · {errors} 个定义需修改" : ""));
            if (previousEvaluation is not null && revision != _suppressScriptsRevision)
            {
                var scripts = snapshot.Objects.Where(item => item.OnUpdate.Length > 0
                    && previousEvaluation.Values.TryGetValue(item.Label, out var before)
                    && result.Values.TryGetValue(item.Label, out var after) && after.Defined && !ReferenceEquals(before, after))
                    .Select(item => item.OnUpdate).ToArray();
                if (scripts.Length > 0) RunEventScripts(scripts);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed && revision == Document.Revision) SetStatus("计算已取消或达到时间上限，请简化表达式或缩小范围。");
        }
        catch (Exception ex) { if (!_closed && revision == Document.Revision) SetStatus(ex.GetBaseException().Message); }
        finally
        {
            if (ReferenceEquals(_calculation, cancellation)) _calculation = null;
            cancellation.Dispose();
        }
    }

    private void RenderView()
    {
        if (Evaluation is null) return;
        var state = Document.State;
        if (_protocolStep < state.Objects.Count)
            state = state with { Objects = state.Objects.Select((item, index) => item with { Visible = item.Visible && index < _protocolStep }).ToList() };
        _view.SetConstruction(state, Evaluation);
    }

    private void RefreshObjects()
    {
        _updatingRows = true;
        try
        {
            var names = Document.State.Objects.Select(item => item.Label).ToHashSet();
            foreach (string name in _rows.Keys.Where(name => !names.Contains(name)).ToArray())
            { _objects.Items.Remove(_rows[name].Item); _rows.Remove(name); }
            foreach (var item in Document.State.Objects)
            {
                if (!_rows.TryGetValue(item.Label, out var row))
                {
                    string label = item.Label;
                    var visible = new CheckBox { MinWidth = 32, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
                    AutomationProperties.SetName(visible, label + " 可见");
                    visible.Checked += (_, _) => { if (!_updatingRows) Document.Update(label, value => value with { Visible = true }); };
                    visible.Unchecked += (_, _) => { if (!_updatingRows) Document.Update(label, value => value with { Visible = false }); };
                    var grid = new Grid { ColumnSpacing = 6, Padding = new Thickness(0, 5, 0, 5) };
                    grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new());
                    grid.Children.Add(visible);
                    var value = new TextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cambria Math"), FontSize = 16 };
                    Grid.SetColumn(value, 1); grid.Children.Add(value);
                    var listItem = new ListViewItem { Content = grid, Tag = label, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    _objects.Items.Add(listItem);
                    row = (listItem, value, visible); _rows[label] = row;
                }
                row.Visible.IsChecked = item.Visible;
                var evaluated = Evaluation.Values[item.Label];
                row.Value.Text = item.Label + " = " + (evaluated.Defined ? evaluated.Display : evaluated.Error);
                row.Value.Foreground = new SolidColorBrush(evaluated.Defined
                    ? _root.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.WhiteSmoke : Windows.UI.Color.FromArgb(255, 42, 53, 69)
                    : Microsoft.UI.Colors.Firebrick);
                ToolTipService.SetToolTip(row.Item, item.Definition);
            }
            if (_view.SelectedLabel is string selected && _rows.TryGetValue(selected, out var selectedRow)) _objects.SelectedItem = selectedRow.Item;
        }
        finally { _updatingRows = false; }
    }

    private void SelectObject(string label)
    {
        _view.SelectedLabel = label; _view.InvalidateGeometry();
        _focused = _input;
        _properties.Children.Clear();
        var item = Document.State.Objects.FirstOrDefault(item => item.Label == label);
        if (item is null) return;
        _properties.Children.Add(new TextBlock { Text = label, FontSize = 24 });
        var definition = FormulaBox("对象定义"); definition.Text = item.Definition;
        definition.GotFocus += (_, _) => _focused = definition;
        _properties.Children.Add(definition);
        var formula = new MathFormulaView();
        formula.Visibility = formula.SetExpression(item.Definition) ? Visibility.Visible : Visibility.Collapsed;
        _properties.Children.Add(formula);
        definition.TextChanged += (_, _) => { formula.Visibility = formula.SetExpression(definition.Text) ? Visibility.Visible : Visibility.Collapsed; };
        var apply = new Button { Content = "应用定义", MinHeight = 44 };
        apply.Click += (_, _) => Guard(() => Document.Update(label, current => current with { Definition = definition.Text }));
        _properties.Children.Add(apply);
        void Check(string title, bool value, Func<ConstructionObject, bool, ConstructionObject> update)
        {
            var check = new CheckBox { Content = title, IsChecked = value, MinHeight = 40 };
            check.Checked += (_, _) => Document.Update(label, current => update(current, true));
            check.Unchecked += (_, _) => Document.Update(label, current => update(current, false));
            _properties.Children.Add(check);
        }
        Check("固定对象", item.Fixed, (current, value) => current with { Fixed = value });
        Check("显示标签", item.ShowLabel, (current, value) => current with { ShowLabel = value });
        Check("跟踪点轨迹", item.Trace, (current, value) => current with { Trace = value });
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (uint color in new uint[] { 0xFF2269DE, 0xFFBA3847, 0xFF15936F, 0xFF8E4CBD, 0xFFCE7918, 0xFF333333 })
        {
            uint packed = color;
            var button = new Button { Width = 38, Height = 42, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(color >> 16), (byte)(color >> 8), (byte)color)) };
            AutomationProperties.SetName(button, "对象颜色 " + color.ToString("X8"));
            button.Click += (_, _) => Document.Update(label, current => current with { Color = packed }); colors.Children.Add(button);
        }
        _properties.Children.Add(colors);
        AddPropertySlider("线宽", .5, 8, .5, item.LineWidth, value => Document.Update(label, current => current with { LineWidth = value }));
        AddPropertySlider("填充不透明度", 0, 1, .05, item.Opacity, value => Document.Update(label, current => current with { Opacity = value }));
        if (Evaluation?.Values.TryGetValue(label, out var evaluated) == true && evaluated.Kind == GeometryKind.Scalar
            && double.TryParse(item.Definition, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            AddPropertySlider("参数值", item.SliderMin, item.SliderMax, item.SliderStep, evaluated.Number,
                value => Document.Update(label, current => current with { Definition = ComputerAlgebra.Number(value) }));
            Check("播放时驱动此参数", item.Animate, (current, value) => current with { Animate = value });
            var range = new StackPanel { Spacing = 6 };
            foreach (var option in new[] { ("最小值", item.SliderMin), ("最大值", item.SliderMax), ("步长", item.SliderStep) })
            {
                string name = option.Item1;
                var number = new NumberBox { Header = name, Value = option.Item2, MinHeight = 44 };
                number.ValueChanged += (_, _) =>
                {
                    if (!double.IsFinite(number.Value)) return;
                    Document.Update(label, current => name switch
                    {
                        "最小值" => current with { SliderMin = Math.Min(number.Value, current.SliderMax - .001) },
                        "最大值" => current with { SliderMax = Math.Max(number.Value, current.SliderMin + .001) },
                        _ => current with { SliderStep = Math.Clamp(number.Value, .0001, 1e6) },
                    });
                };
                range.Children.Add(number);
            }
            _properties.Children.Add(new Expander { Header = "滑动条范围", Content = range, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        if (Evaluation?.Values.TryGetValue(label, out var boolean) == true && boolean.ControlKind == "checkbox")
            Check("复选框值", boolean.Number != 0, (current, value) => current with { Definition = "Checkbox(" + (value ? "1" : "0") + ")" });
        var scriptsPanel = new StackPanel { Spacing = 8 };
        foreach (var script in new[] { ("点击时", item.OnClick), ("更新时", item.OnUpdate) })
        {
            string eventName = script.Item1;
            var box = new TextBox { Header = eventName, Text = script.Item2, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 88,
                MaxLength = 4096, PlaceholderText = "例如 SetValue(a,a+1); SetVisible(g,true)" };
            box.LostFocus += (_, _) => Document.Update(label, current => eventName == "点击时"
                ? current with { OnClick = box.Text } : current with { OnUpdate = box.Text });
            scriptsPanel.Children.Add(box);
        }
        var run = new Button { Content = "运行点击事件", MinHeight = 44 };
        run.Click += (_, _) => RunEventScript(Document.State.Objects.First(current => current.Label == label).OnClick);
        scriptsPanel.Children.Add(run);
        _properties.Children.Add(new Expander { Header = "构造事件脚本", Content = scriptsPanel, HorizontalAlignment = HorizontalAlignment.Stretch });
        var remove = new Button { Content = "删除对象", MinHeight = 44 };
        remove.Click += (_, _) => Document.Execute("Delete(" + label + ")"); _properties.Children.Add(remove);
    }

    private void AddPropertySlider(string title, double min, double max, double step, double value, Action<double> changed)
    {
        var slider = new PointerSlider { Header = title, Minimum = min, Maximum = max, StepFrequency = step, Value = Math.Clamp(value, min, max), MinHeight = 44 };
        slider.ValueChanged += (_, _) => changed(slider.Value);
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Document.BeginGesture()), true);
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => Document.EndGesture()), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => Document.EndGesture()), true);
        _properties.Children.Add(slider);
    }

    public async Task CalculateCasAsync()
    {
        if (_casBusy || string.IsNullOrWhiteSpace(_input.Text)) return;
        _casBusy = true;
        string input = _input.Text;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var snapshot = Document.State;
            var evaluation = Evaluation;
            var result = await Task.Run(() => ComputerAlgebra.Calculate(evaluation is null ? input
                : ConstructionKernel.ResolveExpression(snapshot, evaluation, input, cancellation.Token), cancellation.Token), cancellation.Token);
            if (_closed) return;
            LastCasResult = result;
            var exact = new TextBox { Text = result.Exact, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cambria Math"), FontSize = 19 };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var use = new Button { Content = "送入输入栏", MinHeight = 40 };
            use.Click += (_, _) => _input.Text = result.Exact;
            var latex = new Button { Content = "复制 LaTeX", MinHeight = 40 };
            latex.Click += (_, _) => { var data = new DataPackage(); data.SetText(result.Latex); Clipboard.SetContent(data); };
            actions.Children.Add(use); actions.Children.Add(latex);
            var panel = new StackPanel { Spacing = 6, Children = {
                new TextBlock { Text = input, TextWrapping = TextWrapping.Wrap }, exact,
                new TextBlock { Text = result.Numeric, TextWrapping = TextWrapping.Wrap }, actions } };
            var formatted = new MathFormulaView();
            if (formatted.SetExpression(result.Exact)) panel.Children.Insert(1, formatted);
            _casHistory.Children.Add(new Border { Child = panel, Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.LightGray), CornerRadius = new CornerRadius(6) });
            while (_casHistory.Children.Count > 41) _casHistory.Children.RemoveAt(1);
            SetStatus(result.Evaluated ? "计算完成" : "结果保留未求出的符号运算。");
        }
        catch (OperationCanceledException) { SetStatus("符号计算已取消或超时。"); }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); }
        finally { _casBusy = false; }
    }

    private void ToggleAnimation()
    {
        if (_playing) { StopAnimation(); return; }
        if (!Document.State.Objects.Any(item => item.Animate))
        {
            SetStatus("在数值对象的属性中勾选“播放时驱动此参数”。"); return;
        }
        Document.BeginGesture(); _playing = true; _animationTime = DateTime.UtcNow; _animation.Start();
    }

    private void Animate()
    {
        if (!_playing || _renderedRevision != Document.Revision) return;
        double elapsed = Math.Clamp((DateTime.UtcNow - _animationTime).TotalSeconds, 0, .3);
        _animationTime = DateTime.UtcNow;
        foreach (var item in Document.State.Objects.Where(item => item.Animate).ToArray())
        {
            if (!double.TryParse(item.Definition, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            double span = item.SliderMax - item.SliderMin;
            double next = item.SliderMin + (value - item.SliderMin + span * elapsed / 8) % span;
            Document.Update(item.Label, current => current with { Definition = ComputerAlgebra.Number(next) });
        }
    }

    private void StopAnimation() { _animation.Stop(); if (_playing) Document.EndGesture(); _playing = false; }
    private void RunEventScript(string script)
    {
        if (!string.IsNullOrWhiteSpace(script)) RunEventScripts(new[] { script });
    }
    private bool _scriptsBusy;
    private async void RunEventScripts(IReadOnlyList<string> scripts)
    {
        if (_scriptsBusy || _closed) return;
        _scriptsBusy = true;
        var snapshot = Document.State;
        int revision = Document.Revision;
        try
        {
            var updated = await Task.Run(() =>
            {
                var transaction = new ConstructionDocument(); transaction.Load(snapshot);
                foreach (string script in scripts) { _lifetime.Token.ThrowIfCancellationRequested(); transaction.RunScript(script); }
                return transaction.State;
            }, _lifetime.Token);
            if (_closed || Document.Revision != revision) return;
            Document.Load(updated); _suppressScriptsRevision = Document.Revision;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.GetBaseException().Message); }
        finally { _scriptsBusy = false; }
    }
    private static string Coordinates(GeoVector p) => "(" + ComputerAlgebra.Number(p.X) + "," + ComputerAlgebra.Number(p.Y) + (p.Z == 0 ? ")" : "," + ComputerAlgebra.Number(p.Z) + ")");
    private void Guard(Action action) { try { action(); } catch (Exception ex) { SetStatus(ex.GetBaseException().Message); } }
    private void SetStatus(string text) { if (!_closed) _status.Text = text; }

    private void EnterKey(string key)
    {
        var box = _focused ?? _input;
        if (key == "⇥")
        {
            int from = box.SelectionStart + box.SelectionLength;
            int next = box.Text.IndexOf('□', from);
            if (next < 0) next = box.Text.IndexOf('□');
            if (next >= 0) { box.SelectionStart = next; box.SelectionLength = 1; }
            return;
        }
        Guard(() =>
        {
            var edit = FormulaEditing.Apply(box.Text, box.SelectionStart, box.SelectionLength, key);
            box.Text = edit.Text;
            box.SelectionStart = edit.SelectionStart;
            box.SelectionLength = edit.SelectionLength;
        });
    }

    private void UpdateMathPreview()
    {
        if (_closed) return;
        _inputMath.Visibility = !string.IsNullOrWhiteSpace(_input.Text) && _inputMath.SetExpression(_input.Text)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DisposeWorkbench()
    {
        if (_closed) return;
        _closed = true; StopAnimation(); _lifetime.Cancel(); _calculation?.Cancel(); _debounce.Stop(); _mathDebounce.Stop();
        Document.Changed -= QueueCalculation; _host.TargetChanged -= TargetChanged;
        _target?.Controller.ClearExtensionPreview(_previewOwner);
        _view.Dispose(); Handwriting.Dispose();
    }
}
