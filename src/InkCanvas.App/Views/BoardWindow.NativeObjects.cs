using System.Numerics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Views;

public sealed partial class BoardWindow
{
    private bool _boardPlanOpen;

    internal async Task<string> PreviewBoardPlanAsync(AssistantBoardPlan plan, string instruction,
        Func<bool> targetIsCurrent, CancellationToken cancellationToken)
    {
        if (_boardPlanOpen || _editingAiCard || _contentRoot.XamlRoot is null)
            return "已有白板对话框，请关闭后再试。";

        var page = CurrentController;
        var documentId = _services.Board.DocumentId;
        var originalSelection = page.Selection.ToArray();
        var originalElements = page.SelectedElements.ToArray();
        int layer = page.ActiveInkLayer;
        var originalStrokes = page.Document.Strokes.Where(stroke => stroke.InkLayerIndex == layer).ToArray();
        var replaceable = page.HasSelection ? originalSelection : originalStrokes;
        bool changed = false;
        void OnInkChanged() => changed = true;
        bool CanWrite() => !cancellationToken.IsCancellationRequested && !_closed && AppWindow.IsVisible
            && targetIsCurrent() && documentId == _services.Board.DocumentId && ReferenceEquals(page, CurrentController)
            && !changed && page.ActiveInkLayer == layer && page.Selection.SequenceEqual(originalSelection) && page.SelectedElements.SequenceEqual(originalElements)
            && !page.HasActiveInteraction && page.Playback is null;

        _boardPlanOpen = true;
        page.Document.Changed += OnInkChanged;
        _board.NativeObjectsChanged += OnInkChanged;
        try
        {
            var options = new StackPanel { Spacing = 10 };
            if (plan.NeedsReview || plan.Warnings.Count > 0 || plan.Blocks.Any(block => block.Confidence < AssistantBoardPlan.ReviewThreshold))
                options.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                    Title = "请复核识别结果", Message = string.Join("　", plan.Warnings) });
            foreach (string note in plan.Notes) options.Children.Add(Wrapped(note));

            var compare = new Grid { ColumnSpacing = 16 };
            compare.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            compare.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var originals = new StackPanel { Spacing = 6 };
            originals.Children.Add(new TextBlock { Text = "识别原文", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            originals.Children.Add(Wrapped(plan.Originals.Count > 0 ? string.Join("\n", plan.Originals) : "本次计划没有识别原文。"));
            var results = new StackPanel { Spacing = 8 };
            results.Children.Add(new TextBlock { Text = "采用内容", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var choices = new List<(AssistantBoardBlock Block, CheckBox Box)>();
            foreach (var block in plan.Blocks)
            {
                string body = block.Kind == "formula" ? block.Latex : string.IsNullOrWhiteSpace(block.Text)
                    ? string.Join("\n", block.Lines) : block.Text;
                string title = string.Join("\n", new[] { block.Title, body }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (block.Confidence < AssistantBoardPlan.ReviewThreshold) title = "请复核\n" + title;
                var box = new CheckBox { Content = Wrapped(title), IsChecked = true };
                results.Children.Add(box);
                choices.Add((block, box));
            }
            Grid.SetColumn(results, 1);
            compare.Children.Add(originals);
            compare.Children.Add(results);
            options.Children.Add(compare);
            var replace = new CheckBox
            {
                Content = $"替换{(page.HasSelection ? "选区" : "当前页")}原笔画（{replaceable.Length} 笔）",
                IsEnabled = replaceable.Length > 0 && plan.Originals.Count > 0,
            };
            options.Children.Add(replace);

            var dialog = new ContentDialog
            {
                XamlRoot = _contentRoot.XamlRoot,
                Title = _isSideBoard ? "预览板中板内容" : "预览 AI 板书",
                Content = new ScrollViewer { MaxHeight = 440, Content = options },
                PrimaryButtonText = "写入当前页",
                SecondaryButtonText = "写入新页",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            using var registration = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            Activate();
            var decision = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (decision == ContentDialogResult.None) return "已取消板书写入。";
            if (!CanWrite()) return "画布、页面或选区已变化，请重新生成板书预览。";

            var selected = plan.SelectBlocks(choices.Where(choice => choice.Box.IsChecked == true).Select(choice => choice.Block.Id).ToArray());
            var created = BoardPlanWriter.Create(selected, instruction);
            if (!created.Success) return created.Error;
            bool newPage = decision == ContentDialogResult.Secondary;
            var removed = !newPage && replace.IsChecked == true ? replaceable : Array.Empty<InkStroke>();
            var ignore = removed.ToHashSet();
            Rect occupied = Rect.Empty;
            if (!newPage)
                foreach (var item in page.DrawingObjects)
                {
                    if (item.Stroke is { } stroke && ignore.Contains(stroke)) continue;
                    occupied = occupied.Union(item.Stroke?.Bounds ?? item.Element?.Bounds ?? Rect.Empty);
                }

            if (!Matrix3x2.Invert(page.ViewTransform, out var inverse)) return "当前白板视口无效。";
            var start = Vector2.Transform(Vector2.Zero, inverse);
            var end = Vector2.Transform(new((float)_surfaceHost.ActualWidth,
                (float)Math.Max(0, _surfaceHost.ActualHeight - _dock.ActualHeight)), inverse);
            var viewport = new AssistantRect(start.X, start.Y, end.X, end.Y);
            var sizes = created.OrderedElements.Select(item => new BoardCardSize(item.Bounds.Width, item.Bounds.Height)).ToArray();
            var layout = BoardContentLayoutPlanner.Plan(
                occupied.IsEmpty ? default : new AssistantRect(occupied.Left, occupied.Top, occupied.Right, occupied.Bottom), sizes, viewport);
            if (!layout.IsComplete) return layout.Message;
            if (layout.NeedsShrink)
            {
                if (!await ConfirmAsync("缩小后写入", layout.Message)) return "已取消板书写入。";
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanWrite()) return "画布、页面或选区已变化，请重新生成板书预览。";
            }

            string group = Guid.NewGuid().ToString("N");
            string color = _board.Background is BoardBackground.Black or BoardBackground.Green ? "#FFF3F4F6" : "#FF202024";
            for (int i = 0; i < created.OrderedElements.Count; i++)
            {
                var placement = layout.Placements[i];
                switch (created.OrderedElements[i])
                {
                    case BoardTextObject text:
                        text.Left = (float)placement.Left; text.Top = (float)placement.Top;
                        text.Width = (float)placement.Width; text.Height = (float)placement.Height;
                        text.FontSize *= layout.Scale; text.GroupId = group; text.Color = color;
                        break;
                    case BoardFormulaObject formula:
                        formula.Left = (float)placement.Left; formula.Top = (float)placement.Top;
                        formula.Width = (float)placement.Width; formula.Height = (float)placement.Height;
                        formula.FontSize *= layout.Scale; formula.GroupId = group; formula.Color = color;
                        break;
                }
            }

            if (newPage)
            {
                if (!_board.InsertPageAfterCurrent()) return "无法新增白板页面。";
                CurrentController.ViewTransform = page.ViewTransform;
            }
            if (_board.WriteNativeBoardObjects(created.Texts, created.Formulas, removed))
                return $"已写入 {created.Texts.Count} 个可编辑文本和 {created.Formulas.Count} 个公式。";
            if (newPage) _board.RemoveCurrentPage();
            return "板书未能写入，原有内容已保留。";
        }
        finally
        {
            page.Document.Changed -= OnInkChanged;
            _board.NativeObjectsChanged -= OnInkChanged;
            _boardPlanOpen = false;
        }

        static TextBlock Wrapped(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    }

    private async Task EditNativeObjectAsync(ISelectionElement item)
    {
        if (_editingAiCard || _boardPlanOpen || _contentRoot.XamlRoot is null) return;
        var page = CurrentController;
        var documentId = _services.Board.DocumentId;
        var controls = new StackPanel { Spacing = 10 };
        var body = new TextBox { Header = item is BoardFormulaObject ? "LaTeX" : "正文",
            Text = item is BoardFormulaObject formula ? formula.Latex : ((BoardTextObject)item).Content,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 96 };
        var font = new TextBox { Header = "字体", Text = item is BoardFormulaObject formulaFont ? formulaFont.FontFamily : ((BoardTextObject)item).FontFamily };
        var size = new NumberBox { Header = "字号", Minimum = 8, Maximum = 400, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = item is BoardFormulaObject formulaSize ? formulaSize.FontSize : ((BoardTextObject)item).FontSize };
        controls.Children.Add(body);
        controls.Children.Add(font);
        controls.Children.Add(size);
        var align = new ComboBox { Header = "对齐", ItemsSource = new[] { "左对齐", "居中", "右对齐", "两端对齐" } };
        var aligns = new[] { "left", "center", "right", "justify" };
        if (item is BoardTextObject text)
        {
            align.SelectedIndex = Math.Max(0, Array.IndexOf(aligns, text.Align));
            controls.Children.Add(align);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = _contentRoot.XamlRoot, Title = item is BoardFormulaObject ? "编辑公式" : "编辑文字",
            Content = new ScrollViewer { MaxHeight = 420, Content = controls },
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        _editingAiCard = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || _closed || !AppWindow.IsVisible || !ReferenceEquals(page, CurrentController)
                || documentId != _services.Board.DocumentId || !item.IsPresent) return;
            if (string.IsNullOrWhiteSpace(body.Text)) { await ShowMessageAsync("内容不能为空。"); return; }
            if (item is BoardTextObject currentText)
                _board.UpdateAiObject(currentText, edit =>
                {
                    edit.Content = body.Text; edit.FontFamily = font.Text; edit.FontSize = size.Value;
                    edit.Align = aligns[Math.Max(0, align.SelectedIndex)];
                }, "编辑文字");
            else if (item is BoardFormulaObject currentFormula)
                _board.UpdateAiObject(currentFormula, edit =>
                {
                    edit.Latex = body.Text; edit.FontFamily = font.Text; edit.FontSize = size.Value;
                }, "编辑公式");
        }
        finally { _editingAiCard = false; }
    }
}
