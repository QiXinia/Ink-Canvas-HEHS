using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.AnalyticGeometry;

public static class WorkbenchDiagnostics
{
    public static async Task RunAsync(ICanvasToolsHost host, string directory)
    {
        var checks = new List<object>();
        GeometryWorkbenchWindow window = null;
        try
        {
            var target = host.CaptureTarget();
            var legacy = target.Controller.Selection.FirstOrDefault(stroke => stroke.ShapeSource?.Provider == PlotShapes.Provider);
            if (legacy is not null)
            {
                var originalFrame = legacy.ObjectFrame;
                window = new(host); window.Activate(); await window.CalculateAsync();
                Check("legacy formula opens in native workbench", window.Document.State.Objects.Any(item => item.Label == "g") && window.Evaluation.Values["g"].Defined);
                bool migrated = window.InsertShape();
                var converted = target.Controller.Selection.FirstOrDefault(stroke => stroke.ShapeSource?.Provider == ConstructionShapes.Provider);
                Check("legacy migration retains style and object placement", migrated && converted is not null
                    && converted.Attributes.Color == legacy.Attributes.Color && Math.Abs(converted.Attributes.BaseWidth - legacy.Attributes.BaseWidth) < .001
                    && System.Numerics.Vector2.Distance(originalFrame.Corners(System.Numerics.Matrix3x2.Identity)[0],converted.ObjectFrame.Corners(System.Numerics.Matrix3x2.Identity)[0]) < .01
                    && System.Numerics.Vector2.Distance(originalFrame.Corners(System.Numerics.Matrix3x2.Identity)[2],converted.ObjectFrame.Corners(System.Numerics.Matrix3x2.Identity)[2]) < .01, window.StatusText);
                window.Close(); window = null;
            }
            target.Controller.ClearSelection();
            window = new(host); window.Activate(); await Task.Delay(400);
            Check("native workbench uses shared WinUI title bar", window.ExtendsContentIntoTitleBar && window.Content is Controls.Controls.WindowFrame);
            window.FormulaInput.Text = "h(x)=";
            window.FormulaInput.Focus(FocusState.Programmatic);
            window.FormulaInput.SelectionStart = window.FormulaInput.Text.Length;
            window.Keyboard.SendKey("分数");
            Check("workbench fraction template selects its numerator", window.FormulaInput.Text == "h(x)=(□)/(□)" && window.FormulaInput.SelectedText == "□");
            window.Keyboard.SendKey("x"); window.Keyboard.SendKey("x²"); window.Keyboard.SendKey("⇥"); window.Keyboard.SendKey("2");
            await Task.Delay(200);
            Check("workbench keyboard fills and navigates structural placeholders", window.FormulaInput.Text == "h(x)=(x^2)/(2)", window.FormulaInput.Text);
            Check("main plugin entry displays MathML before creating a shape", window.FormulaMathML?.Contains("mfrac", StringComparison.Ordinal) == true,
                new { nativeRichEdit = window.FormulaUsesRichEdit });
            window.Execute(window.FormulaInput.Text); await window.CalculateAsync();
            Check("template expression uses the normal construction and algebra pipeline", window.Evaluation.Values["h"].Defined);
            var keyboardPanel = Descendants(window.Content).OfType<Expander>().Single(item => AutomationProperties.GetName(item) == "数学键盘与手写输入");
            keyboardPanel.IsExpanded = true;
            (window.Content as FrameworkElement)?.UpdateLayout();
            await Task.Delay(150);
            var keys = Descendants(window.Keyboard).OfType<Button>().Where(button => AutomationProperties.GetName(button).StartsWith("公式键 ")).ToArray();
            Check("formula keyboard uses touch-sized keys across its input width", keys.Length > 20 && keys.All(button => button.ActualHeight >= 44 && button.ActualWidth >= 40));
            await ScreenshotAsync(window, Path.Combine(directory, "math-keyboard.png"));
            window.FormulaInput.Text = "[[1,2],[3/2,-1/2]]";
            await Task.Delay(150);
            Check("workbench displays matrix and fraction structure", window.FormulaMathML?.Contains("mtable", StringComparison.Ordinal) == true);
            var surface = (FrameworkElement)window.Content;
            surface.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(150);
            await ScreenshotAsync(window, Path.Combine(directory, "math-dark.png"));
            surface.RequestedTheme = ElementTheme.Light;
            await Task.Delay(100);
            await ScreenshotAsync(window, Path.Combine(directory, "math-matrix.png"));
            window.FormulaInput.Text = "f(x)=";
            await Task.Delay(150);
            Check("incomplete input hides the previous mathematical preview", Descendants(window.Content).OfType<MathFormulaView>()
                .Where(view => AutomationProperties.GetName(view) == "输入公式排版")
                .All(view => view.Visibility == Visibility.Collapsed));
            window.FormulaInput.Text = "";
            keyboardPanel.IsExpanded = false;
            foreach (var example in GeometryWorkbenchWindow.ExampleCommands)
            {
                try
                {
                    if (example.Group == "CAS")
                    {
                        var result = await Task.Run(() => ComputerAlgebra.Calculate(example.Input));
                        Check("CAS example " + example.Name, result.Exact.Length > 0, result.Exact);
                    }
                    else
                    {
                        window.Document.Load(new()); window.Execute(example.Input); await window.CalculateAsync();
                        var errors = window.Evaluation?.Values.Where(pair => !pair.Value.Defined).Select(pair => pair.Key + " " + pair.Value.Error).ToArray();
                        Check("construction example " + example.Name, errors?.Length == 0, errors);
                    }
                }
                catch (Exception ex) { Check("example " + example.Name, false, ex.ToString()); }
            }
            window.Document.Load(new());
            window.Execute("a=2;show=Checkbox(true);input=InputBox(a);choice=Dropdown({1,2,3},2);next=Button(\"下一步\")");
            window.Document.Update("next", item => item with { OnClick = "js: ggbApplet.setValue('a',ggbApplet.getValue('a')+1);" });
            await window.CalculateAsync(); await Task.Delay(100);
            var nextButton = Descendants(window.Content).OfType<Button>().FirstOrDefault(button => button.Content?.ToString() == "下一步");
            Check("native graph hosts interactive controls", nextButton is not null && Descendants(window.Content).OfType<TextBox>().Any(box => box.Header?.ToString() == "a"));
            if (nextButton is not null)
            {
                ((IInvokeProvider)new ButtonAutomationPeer(nextButton).GetPattern(PatternInterface.Invoke)).Invoke();
                for (int i = 0; i < 40 && window.Evaluation?.Values.GetValueOrDefault("a")?.Number != 3; i++) await Task.Delay(50);
                Check("native button runs local JavaScript and refreshes dependencies", window.Evaluation?.Values.GetValueOrDefault("a")?.Number == 3);
            }
            window.Document.Load(new());
            var input = Descendants(window.Content).OfType<TextBox>().First(box => box.PlaceholderText.StartsWith("输入表达式"));
            input.Text = "a=1;A=(-3,0);B=(3,2);M=Midpoint(A,B);s=Segment(A,B);f(x)=a*x^2";
            var create = Descendants(window.Content).OfType<Button>().First(button => button.Content?.ToString() == "创建 / 更新");
            ((IInvokeProvider)new ButtonAutomationPeer(create).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.CalculateAsync();
            Check("native command input creates linked objects", window.Evaluation?.Values["M"].Origin.X == 0);
            input.Text = "x"; input.SelectionStart = 1;
            window.Keyboard.SendKey("x²");
            Check("native math keyboard preserves caret insertion", input.Text == "x^2");
            input.Text = "Integral(f,x,0,3)";
            var tabs = Descendants(window.Content).OfType<Controls.Controls.SegmentedSelector>()
                .First(selector => AutomationProperties.GetName(selector) == "工作区视图切换");
            tabs.SelectedIndex = 2;
            await window.CalculateCasAsync();
            await Task.Delay(80);
            Check("CAS input resolves geometry function definitions", window.LastCasResult?.Exact == "9"
                && Descendants(window.Content).OfType<TextBox>().Any(box => box.IsReadOnly && box.Text == "9"), window.LastCasResult);
            tabs.SelectedIndex = 0;
            window.Document.BeginGesture(); window.Execute("A=(-5,2)"); window.Execute("A=(-7,4)"); window.Document.EndGesture();
            await window.CalculateAsync();
            Check("point edits update dependent geometry", window.Evaluation.Values["M"].Origin.X == -2 && window.Evaluation.Values["M"].Origin.Y == 3);
            window.Document.Undo(); await window.CalculateAsync();
            Check("drag transaction undoes in one step", window.Evaluation.Values["A"].Origin.X == -3);
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++) window.Execute("a=" + ComputerAlgebra.Number(i / 10d));
            await window.CalculateAsync();
            Check("rapid parameters coalesce to latest result", window.Evaluation.Values["a"].Number == 3.9 && watch.ElapsedMilliseconds < 3000, new { ms = watch.ElapsedMilliseconds });
            var live = Descendants(window.Content).OfType<CheckBox>().First(box => box.Content?.ToString() == "实时预览到画布");
            live.IsChecked = true;
            Check("native workbench previews without committing", target.Controller.ExtensionPreview.Count > 0);
            int count = target.Controller.Document.Count;
            Check("workbench inserts grouped editable contours", window.InsertShape(), window.StatusText);
            var group = target.Controller.Selection.FirstOrDefault()?.ShapeSource;
            Check("construction metadata round trips", group?.Provider == ConstructionShapes.Provider
                && ConstructionExport.Decode(group.Definition).Objects.Any(item => item.Label == "M"));
            int inserted = target.Controller.Document.Count;
            target.Controller.Undo(); Check("construction insertion is a single undo step", target.Controller.Document.Count == count);
            target.Controller.Redo(); Check("construction insertion is a single redo step", target.Controller.Document.Count == inserted);
            string archive = Path.Combine(directory, "construction.icink"); InkFileStore.Save(archive, target.Controller.Document.Strokes);
            Check("canvas archive retains full construction", InkFileStore.Load(archive).Any(stroke => stroke.ShapeSource?.Provider == ConstructionShapes.Provider));
            string workspace = Path.Combine(directory, "construction.icgeo");
            await ConstructionDocument.SaveAsync(workspace, window.Document.State);
            Check("workspace retains definitions", (await ConstructionDocument.OpenAsync(workspace)).Objects.Count == window.Document.State.Objects.Count);
            var svg = ConstructionExport.Svg(window.Document.State, window.Evaluation);
            await File.WriteAllTextAsync(Path.Combine(directory, "construction.svg"), svg);
            using (var document = CanvasSvgDocument.LoadFromXml(CanvasDevice.GetSharedDevice(), svg))
            using (var bitmap = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 750, 96))
            {
                using (var ds = bitmap.CreateDrawingSession()) { ds.Clear(Microsoft.UI.Colors.White); ds.DrawSvg(document, new Windows.Foundation.Size(1000, 750)); }
                await bitmap.SaveAsync(Path.Combine(directory, "construction.png"), CanvasBitmapFileFormat.Png);
                Check("native SVG and PNG render nonempty geometry", bitmap.GetPixelBytes().Count(b => b < 220) > 1000);
            }
            GeometryWorkbenchWindow.ExportPdf(Path.Combine(directory, "construction.pdf"), window.Document.State, window.Evaluation);
            Check("native vector PDF export", new FileInfo(Path.Combine(directory, "construction.pdf")).Length > 300);
            await File.WriteAllTextAsync(Path.Combine(directory, "construction-pstricks.tex"),
                ConstructionExport.Pstricks(window.Document.State, window.Evaluation));
            var animation = window.Document.State with
            {
                Objects = window.Document.State.Objects.Select(item => item.Label == "a" ? item with { Animate = true } : item).ToList(),
            };
            string gifPath = Path.Combine(directory, "construction.gif");
            await window.WriteAnimatedGifAsync(gifPath, animation);
            using (var gif = File.OpenRead(gifPath).AsRandomAccessStream())
            {
                var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, gif);
                var first = await decoder.GetFrameAsync(0);
                var later = await decoder.GetFrameAsync(16);
                var firstPixels = (await first.GetPixelDataAsync()).DetachPixelData();
                var laterPixels = (await later.GetPixelDataAsync()).DetachPixelData();
                Check("animated GIF contains 64 changing frames", decoder.FrameCount == 64
                    && decoder.PixelWidth == 480 && decoder.PixelHeight == 360 && !firstPixels.SequenceEqual(laterPixels));
            }
            await ScreenshotAsync(window, Path.Combine(directory, "workbench-2d.png"));
            window.Document.Load(new() { ThreeDimensional = true });
            window.Execute("g=Cube((-2,-2,-2),4);p=Plane((0,0,0),Vector((1,1,1)));c=Intersect(p,g)");
            await window.CalculateAsync(); await Task.Delay(180);
            Check("3D view renders solid and section", window.Evaluation.Values["c"].Paths.Count > 0);
            Check("3D mode button follows loaded construction", Descendants(window.Content).OfType<Controls.Controls.SegmentedSelector>().Any(selector => selector.SelectedIndex == 1));
            await ScreenshotAsync(window, Path.Combine(directory, "workbench-3d.png"));
            window.Document.SetView(window.Document.State with { Perspective = true, Anaglyph = true });
            await ScreenshotAsync(window, Path.Combine(directory, "workbench-anaglyph.png"));
            window.Document.SetView(window.Document.State with { AnaglyphBlue = true });
            await ScreenshotAsync(window, Path.Combine(directory, "workbench-anaglyph-blue.png"));
            window.Document.Load(new());
            window.Execute("a=0;A=(0,0);B=(2,0);M=Midpoint(A,B)");
            await window.CalculateAsync();
            window.Document.RegisterEvent("*", "OnRename", "js: if (eventTarget === 'P' && previousEventTarget === 'A') ggbApplet.setValue('a', 7);");
            window.Execute("Rename(A,P)");
            for (int attempt = 0; attempt < 20 && window.Document.State.Objects.First(item => item.Label == "a").Definition != "7"; attempt++)
                await Task.Delay(20);
            await window.CalculateAsync();
            Check("construction event registration preserves rename context and dependent geometry",
                window.Evaluation.Values["a"].Number == 7 && window.Evaluation.Values["M"].Origin.X == 1);

            string geoGebra = Path.Combine(directory, "applet.ggb");
            using (var file = File.Create(geoGebra))
            using (var ggb = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (var xml = new StreamWriter(ggb.CreateEntry("geogebra.xml").Open()))
                    xml.Write("""
                        <geogebra><construction>
                          <element type="numeric" label="n"><value val="0"/></element>
                          <element type="numeric" label="mode"><value val="0"/></element>
                          <element type="button" label="next"><caption val="Next"/><listener type="objectClick" val="nextStep"/></element>
                        </construction></geogebra>
                        """);
                using var js = new StreamWriter(ggb.CreateEntry("geogebra_javascript.js").Open());
                js.Write("""
                    function ggbOnInit() {
                        ggbApplet.setValue('n', 1);
                        ggbApplet.registerClientListener(onClient);
                    }
                    function onClient(event) {
                        if (event.type === 'setMode') ggbApplet.setValue('mode', Number(event.argument));
                    }
                    function nextStep() { ggbApplet.setValue('n', ggbApplet.getValue('n') + 1); }
                    """);
            }
            await window.OpenConstructionAsync(geoGebra);
            Check("GeoGebra opens through the workbench and runs applet initialization", window.Evaluation.Values["n"].Number == 1);
            var next = Descendants(window.Content).OfType<Button>().First(button => Equals(button.Content, "Next"));
            ((IInvokeProvider)new ButtonAutomationPeer(next).GetPattern(PatternInterface.Invoke)).Invoke();
            for (int attempt = 0; attempt < 30 && window.Document.State.Objects.First(item => item.Label == "n").Definition != "2"; attempt++)
                await Task.Delay(20);
            await window.CalculateAsync();
            Check("imported GeoGebra control dispatches its object listener", window.Evaluation.Values["n"].Number == 2);
            tabs.SelectedIndex = 3;
            await Task.Delay(100);
            var attachmentTool = Descendants(window.Content).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "几何工具 附着 / 脱离");
            ((IInvokeProvider)new ButtonAutomationPeer(attachmentTool).GetPattern(PatternInterface.Invoke)).Invoke();
            for (int attempt = 0; attempt < 30 && window.Document.State.Objects.First(item => item.Label == "mode").Definition != "67"; attempt++)
                await Task.Delay(20);
            await window.CalculateAsync();
            Check("selecting attachment tool emits the GeoGebra mode number", window.Evaluation.Values["mode"].Number == 67);

            window.Document.Load(new());
            window.Execute("A=(-6,-2);B=(2,-2);s=Segment(A,B);c=Circle((5,3),2);"
                + "l=Line((-7,6),(-7,2));m=Line((-9,4),(-5,4))");
            await window.CalculateAsync();
            var view = Descendants(window.Content).OfType<ConstructionView>().Single();
            async Task SelectDrawingTool(string name)
            {
                var button = Descendants(window.Content).OfType<Button>()
                    .Single(candidate => AutomationProperties.GetName(candidate) == "几何工具 " + name);
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(35);
            }
            void Pick(double x, double y) => view.PickConstructionAt(view.Project(new(x, y)));
            GeometryValue LastValue() => window.Evaluation.Values[window.Document.State.Objects[^1].Label];

            int originalCount = window.Document.State.Objects.Count;
            await SelectDrawingTool("中点 / 中心");
            Pick(-3, -2);
            await window.CalculateAsync();
            Check("midpoint tool accepts a segment with no helper point", window.Document.State.Objects.Count == originalCount + 1
                && LastValue().Kind == GeometryKind.Point && LastValue().Origin == new GeoVector(-2, -2));
            Pick(5, 5);
            await window.CalculateAsync();
            Check("midpoint tool accepts a circle", LastValue().Origin == new GeoVector(5, 3));
            window.Execute("B=(4,-2)");
            await window.CalculateAsync();
            var midpoint = window.Document.State.Objects.Single(item => item.Definition == "Midpoint(s)");
            Check("midpoint follows an edited endpoint", window.Evaluation.Values[midpoint.Label].Origin == new GeoVector(-1, -2));

            await SelectDrawingTool("角平分线");
            Pick(-7, 6);
            Pick(-4, 4);
            await window.CalculateAsync();
            Check("angle bisector accepts two lines", LastValue().Items.Count == 2);
            await SelectDrawingTool("交点");
            Pick(-7, 4);
            await window.CalculateAsync();
            Check("intersection tool selects both paths at their crossing", LastValue().Kind == GeometryKind.Point
                && LastValue().Origin == new GeoVector(-7, 4));
            await SelectDrawingTool("斜率");
            int beforeInvalidPick = window.Document.State.Objects.Count;
            Pick(0, 6);
            Check("object-only tool ignores empty space", window.Document.State.Objects.Count == beforeInvalidPick);
            await SelectDrawingTool("距离 / 长度");
            Pick(-3, -2);
            await window.CalculateAsync();
            Check("length tool creates a dynamic canvas measurement", LastValue().Number == 10 && LastValue().TextAnchor == new GeoVector(-1, -2));
            await SelectDrawingTool("对象上的点");
            Pick(5, 3);
            await window.CalculateAsync();
            Check("point tool places a constrained region point", LastValue().AttachmentTarget?.Kind == GeometryKind.Circle
                && ConstructionDocument.IsRegionPoint(window.Document.State.Objects[^1]));

            await SelectDrawingTool("定半径圆");
            int beforeDialog = window.Document.State.Objects.Count;
            Pick(0, -4);
            await Task.Delay(150);
            var root = (FrameworkElement)window.Content;
            ContentDialog RadiusDialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
                .Where(popup => popup.IsOpen).SelectMany(popup => Descendants(popup.Child)).OfType<ContentDialog>().FirstOrDefault();
            async Task<ContentDialog> WaitForRadiusDialog()
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    if (RadiusDialog() is { } dialog) return dialog;
                    await Task.Delay(50);
                }
                throw new InvalidOperationException($"Radius dialog did not open. {window.StatusText}");
            }
            void InvokeDialogButton(ContentDialog dialog, string name)
            {
                var buttons = Descendants(dialog).OfType<Button>().ToArray();
                var button = buttons.FirstOrDefault(candidate => candidate.Name == name)
                    ?? throw new InvalidOperationException($"Missing {name}. Buttons: {string.Join(", ", buttons.Select(candidate => candidate.Name))}");
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            }
            InvokeDialogButton(await WaitForRadiusDialog(), "CloseButton");
            for (int attempt = 0; attempt < 20 && RadiusDialog() is not null; attempt++) await Task.Delay(50);
            await Task.Delay(350);
            Check("cancelling radius input leaves no extra center point", window.Document.State.Objects.Count == beforeDialog);
            Pick(0, -4);
            InvokeDialogButton(await WaitForRadiusDialog(), "PrimaryButton");
            await Task.Delay(200);
            await window.CalculateAsync();
            Check("confirming radius input creates the center and circle", window.Document.State.Objects.Count == beforeDialog + 2
                && LastValue().Radius == 3);
            window.Document.Undo();
            await window.CalculateAsync();
            Check("one undo removes the circle and its new center", window.Document.State.Objects.Count == beforeDialog);
            tabs.SelectedIndex = 0;

            window.Document.Load(new() { Objects = Enumerable.Range(1, 1000).Select(index => new ConstructionObject
                { Label = "P" + index, Definition = $"({index % 20},{index % 10})" }).ToList() });
            await window.CalculateAsync();
            await Task.Delay(150);
            var algebraList = Descendants(window.Content).OfType<ListView>().First(list => list.Items.Count == 1000);
            var visibleRows = Descendants(algebraList).OfType<ListViewItem>().Count(row => row.Tag is string);
            Check("large imported constructions only realize visible algebra rows", visibleRows is > 0 and < 100, visibleRows);
            window.Close(); window = null;
            Check("closing workbench releases canvas preview", target.Controller.ExtensionPreview.Count == 0);
            window = new(host); window.Activate(); await window.CalculateAsync();
            Check("selected construction reopens with dependencies", window.Document.State.Objects.Any(item => item.Label == "M"));
        }
        catch (Exception ex) { Check("workbench runtime exception", false, ex.ToString()); }
        finally
        {
            window?.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "workbench.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task ScreenshotAsync(Window window, string path)
    {
        await Task.Delay(120);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(window.Content as FrameworkElement);
        var pixels = await bitmap.GetPixelsAsync(); byte[] bytes = new byte[pixels.Length]; DataReader.FromBuffer(pixels).ReadBytes(bytes);
        using var memory = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync(); memory.Seek(0);
        using var output = File.Create(path); await memory.AsStreamForRead().CopyToAsync(output);
    }
}
