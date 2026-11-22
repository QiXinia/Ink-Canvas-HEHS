using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Features.Board;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Features.AiAssistant;

/// <summary>
/// 主展板与板中板共用的 AI 卡片编辑器：按 Kind 编辑标题/作者/正文/LaTeX，
/// 保存时重渲染（公式优先 LaTeX 位图，失败回退文本）并一次 <see cref="BoardService.UpdateImage"/>。
/// 渲染/写入失败只提示，不改动原对象。
/// </summary>
public static class AiCardEditor
{
    public const int FormulaMaxWidthDip = 560;

    public static async Task<bool> EditAsync(
        BoardService board, BoardImageAsset asset, AiPasteMetadata metadata, XamlRoot xamlRoot)
    {
        if (board is null || asset is null || metadata is null || xamlRoot is null) return false;

        AiCardEditState state;
        try
        {
            state = AiCardEditState.FromMetadata(metadata);
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] AI 卡片编辑失败：{asset.Name}，读取元数据异常 {ex.Message}", LogType.Error);
            return false;
        }

        FileLog.WriteLogToFile(
            $"[AI] AI 卡片编辑：资产 {asset.Name}，Kind={metadata.Kind}，BlockKind={state.BlockKind}",
            LogType.Event);

        var panel = new StackPanel { Spacing = 6 };
        TextBox titleEditor = null;
        TextBox authorEditor = null;

        if (state.HasTitleEditor)
        {
            titleEditor = new TextBox
            {
                Text = state.Title,
                PlaceholderText = "标题",
                MinWidth = 360,
            };
            panel.Children.Add(EditorLabel(state.TitleLabel));
            panel.Children.Add(titleEditor);
        }

        if (state.HasAuthorEditor)
        {
            authorEditor = new TextBox
            {
                Text = state.Author,
                PlaceholderText = "作者 / 朝代",
                MinWidth = 360,
            };
            panel.Children.Add(EditorLabel(state.AuthorLabel));
            panel.Children.Add(authorEditor);
        }

        var editor = new TextBox
        {
            Text = state.Body,
            AcceptsReturn = state.BodyAcceptsReturn,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = state.EditorMinWidth,
            MinHeight = state.EditorMinHeight,
        };
        panel.Children.Add(EditorLabel(state.EditorLabel));
        panel.Children.Add(editor);

        var dialog = new ContentDialog
        {
            Title = state.DialogTitle,
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] AI 卡片编辑失败：{asset.Name}，对话框异常 {ex.Message}", LogType.Error);
            return false;
        }

        string body = editor.Text ?? "";
        if (state.IsFormula && string.IsNullOrWhiteSpace(body))
        {
            await ShowMessageAsync(xamlRoot, "LaTeX 内容不能为空。");
            return false;
        }

        AiPasteMetadata updated = state.ApplyTo(
            metadata,
            titleEditor?.Text ?? state.Title,
            authorEditor?.Text ?? state.Author,
            body);

        try
        {
            bool success = state.IsBoardPlan
                ? await RenderBoardPlanAsync(board, asset, updated, state, body, xamlRoot)
                : await RenderLegacyAsync(board, asset, updated, xamlRoot);

            if (success)
            {
                FileLog.WriteLogToFile(
                    $"[AI] AI 卡片编辑完成：{asset.Name}，kind={metadata.Kind}，"
                    + $"blockKind={updated.BlockKind}，body={body.Length} 字",
                    LogType.Event);
            }

            return success;
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile(
                $"[AI] AI 卡片编辑失败：{asset.Name}，{ex.Message ?? ex.GetType().Name}", LogType.Error);
            await ShowMessageAsync(xamlRoot, "卡片重新渲染失败，原内容保持不变。");
            return false;
        }
    }

    private static async Task<bool> RenderBoardPlanAsync(
        BoardService board,
        BoardImageAsset asset,
        AiPasteMetadata updated,
        AiCardEditState state,
        string body,
        XamlRoot xamlRoot)
    {
        byte[] png;
        double width;
        double height;
        string fallbackReason = "";

        if (state.IsFormula)
        {
            var host = xamlRoot.Content as FrameworkElement;
            BoardFormulaRenderResult formula = await BoardFormulaRenderer.RenderPngAsync(
                updated.Latex, FormulaMaxWidthDip, 2.0, host);

            if (formula.Success)
            {
                png = formula.Png;
                width = formula.Width;
                height = formula.Height;
            }
            else
            {
                fallbackReason = string.IsNullOrWhiteSpace(formula.Error)
                    ? "公式渲染不可用" : formula.Error;
                BoardTextCardRenderResult fallback = await BoardTextCardRenderer.RenderAsync(
                    state.BuildFormulaFallbackSpec(updated, updated.Latex));
                png = fallback.Png;
                width = fallback.Width;
                height = fallback.Height;
            }
        }
        else
        {
            BoardTextCardRenderResult rendered = await BoardTextCardRenderer.RenderAsync(
                state.BuildTextSpec(updated, updated.Title, updated.Author, body));
            png = rendered.Png;
            width = rendered.Width;
            height = rendered.Height;
        }

        if (png is not { Length: > 0 } || width <= 0 || height <= 0)
        {
            await ShowMessageAsync(xamlRoot, "卡片重新渲染失败，原内容保持不变。");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(fallbackReason))
        {
            string note = "手动编辑时 LaTeX 排版回退为文本：" + fallbackReason;
            if (!updated.Notes.Contains(note)) updated.Notes.Add(note);
            FileLog.WriteLogToFile(
                $"[AI] 板书卡片编辑回退：{asset.Name}，{fallbackReason}", LogType.Error);
        }

        FileLog.WriteLogToFile(
            $"[AI] 板书卡片重渲染：{asset.Name}，blockKind={updated.BlockKind}，"
            + $"formula={state.IsFormula}，size={width:0}×{height:0}，lines={updated.Lines.Count}",
            LogType.Event);

        if (!board.UpdateImage(asset, png, updated.Serialize()))
        {
            await ShowMessageAsync(xamlRoot, "卡片已不在当前页面，未更新。");
            return false;
        }

        return true;
    }

    private static async Task<bool> RenderLegacyAsync(
        BoardService board, BoardImageAsset asset, AiPasteMetadata updated, XamlRoot xamlRoot)
    {
        List<string> lines = AiCardEditState.ContentLines(updated);
        var (estimatedWidth, estimatedHeight) = PasteCardLayout.Estimate(lines);
        int width = Math.Clamp((int)Math.Round(Math.Max(asset.Width, estimatedWidth)), 120, 640);
        int height = Math.Clamp((int)Math.Round(Math.Max(asset.Height, estimatedHeight)), 60, 480);

        byte[] png = await PasteCardRenderer.RenderPngAsync(asset.Name, lines, width, height);
        if (png is null || png.Length == 0)
        {
            await ShowMessageAsync(xamlRoot, "卡片重新渲染失败，原内容保持不变。");
            return false;
        }

        if (!board.UpdateImage(asset, png, updated.Serialize()))
        {
            await ShowMessageAsync(xamlRoot, "卡片已不在当前页面，未更新。");
            return false;
        }

        return true;
    }

    private static TextBlock EditorLabel(string text) => new() { Text = text, Opacity = 0.7 };

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了",
                XamlRoot = xamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            FileLog.WriteLogToFile($"[AI] AI 卡片提示失败：{message}，{ex.Message}", LogType.Trace);
        }
    }
}