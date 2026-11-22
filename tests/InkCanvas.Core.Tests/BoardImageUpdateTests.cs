using InkCanvas.Core.AiAssistant;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>AI 卡片编辑落盘：UpdateImage 一次撤销/重做与编辑元数据往返。</summary>
public sealed class BoardImageUpdateTests
{
    private static BoardImageAsset Image(string name, byte fill) => new()
    {
        Name = name,
        Data = new[] { fill, (byte)1, (byte)2 },
        ContentType = "image/png",
        Left = 10,
        Top = 10,
        Width = 100,
        Height = 100,
        Metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.BoardPlan,
            BlockKind = "text",
            Title = "原标题",
            Text = "原正文",
        }.Serialize(),
    };

    [Fact]
    public void UpdateImage_ReplacesDataAndMetadata_UndoRedoOneStep()
    {
        var board = new BoardService();
        var asset = Image("card", 1);
        board.AddImagesAtomic(new[] { asset }, "AI 板书");

        byte[] originalData = board.CurrentImages[0].Data;
        string originalMetadata = board.CurrentImages[0].Metadata;

        var edited = new AiPasteMetadata
        {
            Kind = AiPasteKind.BoardPlan,
            BlockKind = "text",
            Title = "新标题",
            Text = "新正文",
            Lines = new List<string> { "新正文" },
            StyleJson = "{\"fontSize\":30}",
            Notes = new List<string> { "手动编辑" },
        };
        byte[] newData = new byte[] { 9, 8, 7, 6 };

        Assert.True(board.UpdateImage(asset, newData, edited.Serialize()));
        Assert.Equal(newData, board.CurrentImages[0].Data);
        Assert.Equal(edited.Serialize(), board.CurrentImages[0].Metadata);
        Assert.Equal(2, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Equal(originalData, board.CurrentImages[0].Data);
        Assert.Equal(originalMetadata, board.CurrentImages[0].Metadata);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Redo();
        Assert.Equal(newData, board.CurrentImages[0].Data);
        Assert.Equal(edited.Serialize(), board.CurrentImages[0].Metadata);

        AiPasteMetadata parsed = AiPasteMetadata.TryParse(board.CurrentImages[0].Metadata);
        Assert.NotNull(parsed);
        Assert.Equal("text", parsed.BlockKind);
        Assert.Equal("新标题", parsed.Title);
        Assert.Equal("新正文", parsed.Text);
        Assert.Equal("{\"fontSize\":30}", parsed.StyleJson);
        Assert.Single(parsed.Notes);
    }

    [Fact]
    public void UpdateImage_EmptyData_IsNoOp()
    {
        var board = new BoardService();
        var asset = Image("card", 2);
        board.AddImagesAtomic(new[] { asset }, "AI 板书");
        string before = board.CurrentImages[0].Metadata;

        Assert.False(board.UpdateImage(asset, Array.Empty<byte>(), "{}"));
        Assert.Equal(before, board.CurrentImages[0].Metadata);
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void BoardPlanMetadata_RoundTripsAllEditableFields()
    {
        var metadata = new AiPasteMetadata
        {
            Kind = AiPasteKind.BoardPlan,
            BlockKind = "poem",
            Schema = 2,
            Title = "静夜思",
            Author = "唐·李白",
            Lines = new List<string> { "床前明月光", "疑是地上霜" },
            Latex = "",
            Text = "",
            StyleJson = "{\"fontSize\":26,\"columns\":1}",
            Instruction = "整理当前页",
            CreatedAt = 1700000000000,
            Notes = new List<string> { "标题自动生成" },
            Annotations = new List<List<double>> { new() { 1, 2, 3, 4 } },
        };

        AiPasteMetadata parsed = AiPasteMetadata.TryParse(metadata.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(AiPasteKind.BoardPlan, parsed.Kind);
        Assert.Equal("poem", parsed.BlockKind);
        Assert.Equal(2, parsed.Schema);
        Assert.Equal("静夜思", parsed.Title);
        Assert.Equal("唐·李白", parsed.Author);
        Assert.Equal(metadata.Lines, parsed.Lines);
        Assert.Equal(metadata.StyleJson, parsed.StyleJson);
        Assert.Equal(metadata.Instruction, parsed.Instruction);
        Assert.Equal(metadata.CreatedAt, parsed.CreatedAt);
        Assert.Equal(metadata.Notes, parsed.Notes);
        Assert.Equal(metadata.Annotations, parsed.Annotations);
    }
}