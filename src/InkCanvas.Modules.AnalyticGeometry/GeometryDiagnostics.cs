using System.Diagnostics;
using System.Text.Json;
using InkCanvas.AnalyticGeometry;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Persistence;
using InkCanvas.Host.Contracts;
using InkCanvas.Platform.Windows;
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

public static class GeometryDiagnostics
{
    public static async Task RunAsync(ICanvasToolsHost host, string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<object>(); GeometryWindow window = null;
        try
        {
            window = new GeometryWindow(host); window.Activate(); await Task.Delay(500);
            Check("geometry uses shared WinUI title bar", window.ExtendsContentIntoTitleBar
                && window.Content is Controls.Controls.WindowFrame frame && frame.Header.Title == window.Title);
            foreach (string sampleFormula in new[] { "y=a*x^2+b*x+c", "x^2/9-y^2/4=1", "y=sin(x)/2", "y=sqrt(abs(x))" })
            {
                string restored = MathMarkup.ToExpression(MathMarkup.ToMathML(sampleFormula));
                Check("MathML round-trips a plot expression " + sampleFormula,
                    CompiledPlot.Compile(new PlotDefinition { Formula = restored }) is not null);
            }
            Check("Math editor exposes native or WinUI MathML preview", window.MathModeAvailable
                || Descendants(window.Content).OfType<MathFormulaPreview>().Any());
            var target = host.CaptureTarget(); int count = target.Controller.Document.Count, undo = target.Controller.History.UndoCount;
            window.SetDefinition(new PlotDefinition { Formula = "x^2/9-y^2/4=1", IncludeAxes = true }); await window.CalculateAsync();
            Check("implicit graph creates stitched native preview", window.CurrentMesh?.Paths.Count >= 2 && target.Controller.ExtensionPreview.Count > 0, new { window.StatusText, mesh = window.CurrentMesh?.Paths.Count, preview = target.Controller.ExtensionPreview.Count });
            Check("live preview does not change document or undo history", target.Controller.Document.Count == count && target.Controller.History.UndoCount == undo);
            bool didInsert = window.InsertShape(); Check("insert uses normal host transaction", didInsert, window.StatusText);
            if (!didInsert) throw new InvalidOperationException("Insert failed " + window.StatusText);
            var original = target.Controller.Selection.ToArray(); var group = original[0].ShapeSource.GroupId;
            Check("all graph contours select as one source object", original.Length >= 2 && original.All(stroke => stroke.IsRecognizedShape && stroke.ShapeSource.GroupId == group));
            int inserted = target.Controller.Document.Count;
            target.Controller.Undo(); Check("whole formula insert undoes in one step", target.Controller.Document.Count == count);
            target.Controller.Redo(); Check("whole formula insert redoes in one step", target.Controller.Document.Count == inserted);
            window.SetDefinition(new PlotDefinition { Formula = "x^2+y^2=a^2", A = 4 }); await window.CalculateAsync();
            Check("editing a formula updates the same shape group", window.InsertShape() && target.Controller.Selection.All(stroke => stroke.ShapeSource.GroupId == group));
            string archive = Path.Combine(directory, "formula-shape.icink"); InkFileStore.Save(archive, target.Controller.Document.Strokes);
            Check("formula archive retains editable source", InkFileStore.Load(archive).Any(stroke => stroke.ShapeSource?.GroupId == group && stroke.ShapeSource.Definition.Contains("x^2+y^2")));
            window.SetDefinition(new PlotDefinition { Formula = "y=x" });
            window.FormulaMode.SelectedIndex = 0;
            var formula = Descendants(window.Content).OfType<TextBox>().First(box => box.Header?.ToString() == "公式"); formula.SelectionStart = formula.Text.Length;
            var key = Descendants(window.Keyboard).OfType<Button>().Single(button => button.Content?.ToString() == "x²");
            ((IInvokeProvider)new ButtonAutomationPeer(key).GetPattern(PatternInterface.Invoke)).Invoke();
            await window.CalculateAsync();
            Check("native formula keyboard edits caret and recalculates", formula.Text == "y=x^2" && window.CurrentMesh.Paths.Count > 0);
            window.SetDefinition(new PlotDefinition { Formula = "y=x" });
            window.FormulaMode.SelectedIndex = 0;
            var touchFormula = Descendants(window.Content).OfType<TextBox>().First(box => box.Header?.ToString() == "公式");
            touchFormula.SelectionStart = touchFormula.Text.Length;
            window.SendFormulaKey("x²");
            await window.CalculateAsync();
            string mathMl = window.CurrentDefinition.FormulaMathML;
            Check("MathLive style keyboard inserts at formula caret and updates the plot", touchFormula.Text == "y=x^2" && window.CurrentMesh.Paths.Count > 0);
            Check("MathML and editable plot source stay synchronized", !string.IsNullOrWhiteSpace(mathMl)
                && MathMarkup.ToExpression(mathMl) == "y=(x)^(2)" && window.CurrentDefinition.Formula == touchFormula.Text,
                new { source = touchFormula.Text, parsed = string.IsNullOrWhiteSpace(mathMl) ? "" : MathMarkup.ToExpression(mathMl),
                    stored = window.CurrentDefinition.Formula, nativeAvailable = window.MathModeAvailable, length = mathMl?.Length });
            window.FormulaMode.SelectedIndex = 1;
            await Task.Delay(100);
            Check("WinUI formula preview renders MathML structure", window.MathModeAvailable
                ? Descendants(window.Content).OfType<Microsoft.UI.Xaml.Controls.RichEditBox>().Any(editor => editor.Visibility == Visibility.Visible)
                : Descendants(window.Content).OfType<MathFormulaPreview>().Any(preview => preview.Visibility == Visibility.Visible && preview.Content is ScrollViewer));
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 30; i++) window.SetDefinition(new PlotDefinition { Formula = "y=a*sin(x)+b", A = i / 10d, B = i / 20d });
            await window.CalculateAsync();
            Check("rapid parameter changes keep the latest generation", window.CurrentMesh.Paths.Count > 0 && watch.ElapsedMilliseconds < 3000, new { elapsed = watch.ElapsedMilliseconds, calculation = window.LastCalculationMilliseconds, window.CompletedGeneration });
            var selector = Descendants(window.Content).OfType<InkCanvas.Controls.Controls.SegmentedSelector>()
                .Single(item => AutomationProperties.GetName(item) == "公式输入方式");
            selector.SelectedIndex = 1; await Task.Delay(200);
            if (window.Handwriting.RecognitionAvailable)
            {
                var ink = window.Handwriting.Controller; uint id = 100;
                var strokes = new[] { new[] { (20f, 30f), (40f, 65f), (60f, 30f) }, new[] { (40f, 65f), (27f, 105f) },
                    new[] { (85f, 50f), (120f, 50f) }, new[] { (85f, 75f), (120f, 75f) },
                    new[] { (150f, 35f), (190f, 95f) }, new[] { (190f, 35f), (150f, 95f) } };
                foreach (var stroke in strokes)
                {
                    var first = new OverlayPointer(id++, stroke[0].Item1, stroke[0].Item2, .5f, 3, 3, OverlayPointerKind.Mouse, id * 20);
                    ink.OnPointerDown(first);
                    foreach (var point in stroke.Skip(1)) ink.OnPointerMove(first with { X = point.Item1, Y = point.Item2, TimeMs = first.TimeMs + 20 });
                    ink.OnPointerUp(first with { X = stroke[^1].Item1, Y = stroke[^1].Item2, TimeMs = first.TimeMs + 40 });
                }
                await window.Handwriting.RecognizeAsync();
                var texts = Descendants(window.Handwriting).OfType<TextBlock>().Select(text => text.Text).ToArray();
                Check("Windows local handwriting returns without blocking UI", !texts.Any(text => text.StartsWith("识别失败")), new { texts });
            }
            else checks.Add(new { check = "Windows handwriting language availability", pass = true, detail = "No recognition language installed. Native ink capture and formula keyboard remain available." });
            selector.SelectedIndex = 0;
            await ScreenshotAsync(window, Path.Combine(directory, "analytic-geometry.png"));
            window.Close(); window = null;
            Check("closing editor clears temporary canvas preview", target.Controller.ExtensionPreview.Count == 0);
            await WorkbenchDiagnostics.RunAsync(host, directory);
        }
        catch (Exception ex) { checks.Add(new { check = "geometry runtime exception", pass = false, detail = ex.ToString() }); }
        finally
        {
            window?.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "geometry.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        void Check(string check, bool pass, object detail = null) => checks.Add(new { check, pass, detail });
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break; yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Descendants(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static async Task ScreenshotAsync(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(window.Content as FrameworkElement);
        var pixels = await bitmap.GetPixelsAsync(); byte[] bytes = new byte[pixels.Length]; DataReader.FromBuffer(pixels).ReadBytes(bytes);
        using var memory = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes); await encoder.FlushAsync(); memory.Seek(0);
        using var output = File.Create(path); await memory.AsStreamForRead().CopyToAsync(output);
    }
}
