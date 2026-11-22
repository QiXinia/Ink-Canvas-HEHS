using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>共享 AI 卡片编辑器的纯逻辑：元数据读取/应用/渲染参数与序列化往返。</summary>
public sealed class AiCardEditStateTests
{
    private static AiPasteMetadata BoardPlan(string blockKind) => new()
    {
        Kind = AiPasteKind.BoardPlan,
        BlockKind = blockKind,
        Schema = 2,
        StyleJson = "{\"fontSize\":28,\"align\":\"center\",\"columns\":2,\"lineHeight\":1.6}",
        Instruction = "整理当前页板书",
        CreatedAt = 1700000000000,
        Notes = new List<string> { "第 2 块标题自动生成" },
        Annotations = new List<List<double>> { new() { 1, 2, 3, 4 } },
        Title = "静夜思",
        Author = "唐·李白",
        Lines = new List<string> { "床前明月光", "疑是地上霜" },
    };

    [Fact]
    public void FromMetadata_ReadsPoemFieldsAndBuildsSpec()
    {
        AiPasteMetadata metadata = BoardPlan("poem");
        AiCardEditState state = AiCardEditState.FromMetadata(metadata);

        Assert.True(state.IsBoardPlan);
        Assert.True(state.IsPoem);
        Assert.False(state.IsFormula);
        Assert.True(state.HasTitleEditor);
        Assert.True(state.HasAuthorEditor);
        Assert.Equal("静夜思", state.Title);
        Assert.Equal("唐·李白", state.Author);
        Assert.Equal("床前明月光\n疑是地上霜", state.Body);
        Assert.Equal("编辑 AI 板书卡片", state.DialogTitle);

        BoardTextCardSpec spec = state.BuildTextSpec(metadata, "新标题", "新作者", "第一行\n第二行");
        Assert.Equal("poem", spec.Kind);
        Assert.Equal("新标题", spec.Title);
        Assert.Equal("新作者", spec.Subtitle);
        Assert.Equal(28, spec.FontSize, 3);
        Assert.Equal(2, spec.Columns);
        Assert.Equal(1.6, spec.LineHeight, 3);
        Assert.Equal(new[] { "第一行", "第二行" }, spec.Lines);
    }

    [Fact]
    public void ApplyTo_Poem_PreservesBoardPlanMetadataAndRoundTrips()
    {
        AiPasteMetadata metadata = BoardPlan("poem");
        AiCardEditState state = AiCardEditState.FromMetadata(metadata);

        AiPasteMetadata updated = state.ApplyTo(metadata, " 新标题 ", " 新作者 ", "第一行\n第二行");

        Assert.Equal(AiPasteKind.BoardPlan, updated.Kind);
        Assert.Equal("poem", updated.BlockKind);
        Assert.Equal("新标题", updated.Title);
        Assert.Equal("新作者", updated.Author);
        Assert.Equal(new[] { "第一行", "第二行" }, updated.Lines);
        Assert.Equal(metadata.Schema, updated.Schema);
        Assert.Equal(metadata.StyleJson, updated.StyleJson);
        Assert.Equal(metadata.Instruction, updated.Instruction);
        Assert.Equal(metadata.CreatedAt, updated.CreatedAt);
        Assert.Equal(metadata.Notes, updated.Notes);
        Assert.Equal(metadata.Annotations, updated.Annotations);

        AiPasteMetadata parsed = AiPasteMetadata.TryParse(updated.Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(updated.BlockKind, parsed.BlockKind);
        Assert.Equal(updated.Title, parsed.Title);
        Assert.Equal(updated.Author, parsed.Author);
        Assert.Equal(updated.Lines, parsed.Lines);
        Assert.Equal(updated.StyleJson, parsed.StyleJson);
        Assert.Equal(updated.Notes, parsed.Notes);
        Assert.Equal(updated.CreatedAt, parsed.CreatedAt);
    }

    [Fact]
    public void FormulaBoardPlan_EditsLatexAndClearsText()
    {
        AiPasteMetadata metadata = BoardPlan("formula");
        metadata.Latex = "\\frac{1}{2}x^2";
        metadata.Text = "旧文本";
        AiCardEditState state = AiCardEditState.FromMetadata(metadata);

        Assert.True(state.IsFormula);
        Assert.Equal("\\frac{1}{2}x^2", state.Body);

        AiPasteMetadata updated = state.ApplyTo(metadata, "", "", " x^2 - 3x ");
        Assert.Equal("x^2 - 3x", updated.Latex);
        Assert.Empty(updated.Lines);
        Assert.Equal("", updated.Text);

        BoardTextCardSpec fallback = state.BuildFormulaFallbackSpec(updated, updated.Latex);
        Assert.Equal("公式（LaTeX）", fallback.Title);
        Assert.Equal("x^2 - 3x", fallback.Text);
    }

    [Fact]
    public void TextAndWordList_UpdateTextAndLines()
    {
        AiPasteMetadata text = BoardPlan("text");
        AiCardEditState textState = AiCardEditState.FromMetadata(text);
        AiPasteMetadata textUpdated = textState.ApplyTo(text, "标题", "", "第一行\n第二行");

        Assert.Equal("第一行\n第二行", textUpdated.Text);
        Assert.Equal(new[] { "第一行", "第二行" }, textUpdated.Lines);

        AiPasteMetadata wordList = BoardPlan("wordlist");
        AiCardEditState wordState = AiCardEditState.FromMetadata(wordList);
        AiPasteMetadata wordUpdated = wordState.ApplyTo(wordList, "", "", "判别式｜pàn bié shì｜判断根的情况");

        Assert.Equal(new[] { "判别式｜pàn bié shì｜判断根的情况" }, wordUpdated.Lines);
        Assert.Equal("判别式｜pàn bié shì｜判断根的情况", wordUpdated.Text);
    }

    [Fact]
    public void LegacyStepsAndFormula_KeepLegacyShape()
    {
        var steps = new AiPasteMetadata
        {
            Version = 1,
            Kind = AiPasteKind.Steps,
            Lines = new List<string> { "第一步", "第二步" },
            Annotations = new List<List<double>> { new() { 1, 2, 3, 4 } },
        };
        AiCardEditState stepsState = AiCardEditState.FromMetadata(steps);
        Assert.False(stepsState.IsBoardPlan);
        Assert.Equal("第一步\n第二步", stepsState.Body);
        Assert.Equal("卡片文本", stepsState.EditorLabel);

        AiPasteMetadata stepsUpdated = stepsState.ApplyTo(steps, "", "", "新一步\n新二步");
        Assert.Equal(AiPasteKind.Steps, stepsUpdated.Kind);
        Assert.Equal(new[] { "新一步", "新二步" }, stepsUpdated.Lines);
        Assert.Single(stepsUpdated.Annotations);

        var formula = new AiPasteMetadata
        {
            Kind = AiPasteKind.Formula,
            Latex = "E = mc^2",
        };
        AiCardEditState formulaState = AiCardEditState.FromMetadata(formula);
        Assert.True(formulaState.IsFormula);
        Assert.Equal("E = mc^2", formulaState.Body);
        Assert.Equal("LaTeX 公式", formulaState.EditorLabel);

        AiPasteMetadata formulaUpdated = formulaState.ApplyTo(formula, "", "", "a^2+b^2=c^2");
        Assert.Equal(AiPasteKind.Formula, formulaUpdated.Kind);
        Assert.Equal("a^2+b^2=c^2", formulaUpdated.Latex);
    }

    [Fact]
    public void InvalidStyleJson_FallsBackToDefaults()
    {
        AssistantBoardStyle style = AiCardEditState.ParseStyle("{ not json");
        Assert.Equal(24, style.FontSize, 3);
        Assert.Equal(1, style.Columns);
        Assert.Equal(1.4, style.LineHeight, 3);
        Assert.Equal("left", style.Align);

        Assert.Equal("formula", AiCardEditState.NormalizeKind("FORMULA"));
        Assert.Equal("text", AiCardEditState.NormalizeKind(null));
        Assert.Equal(new[] { "a", "b" }, AiCardEditState.SplitLines("a\r\nb\n"));
    }
}