using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using System.IO.Compression;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// AI 板书卡片（BoardPlan）保存/重开：复用 BoardImageAsset + Metadata，v4 包格式不变；
/// 主展板与板中板（Blob Scope=side）均需完整往返。
/// </summary>
public sealed class AiBoardPersistenceTests
{
    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-ai-board-{Guid.NewGuid():N}-{name}");

    private static BoardImageAsset Card(string name, AiPasteMetadata metadata, byte fill)
        => new()
        {
            Name = name,
            Data = new byte[] { fill, (byte)2, (byte)3, (byte)4 },
            ContentType = "image/png",
            Left = 10 + fill,
            Top = 20 + fill,
            Width = 240,
            Height = 120,
            Metadata = metadata.Serialize(),
        };

    private static AiPasteMetadata Meta(AiPasteKind kind, string latex, string text, params string[] lines) => new()
    {
        Kind = kind,
        Schema = 1,
        Latex = latex,
        Text = text,
        Lines = lines.ToList(),
        StyleJson = "{\"fontSize\":24,\"align\":\"center\",\"columns\":1,\"lineHeight\":1.4}",
        Instruction = "整理当前页板书",
        CreatedAt = 1730000000000,
        Notes = new List<string> { "第 2 行置信度较低" },
    };

    private static BoardImageAsset[] BoardPlanCards() => new[]
    {
        Card("公式", Meta(AiPasteKind.BoardPlan, "\\frac{1}{2}x^2", "", "公式"), 1),
        Card("文本", Meta(AiPasteKind.BoardPlan, "", "一元二次方程", "文本"), 2),
        Card("古诗", Meta(AiPasteKind.BoardPlan, "", "", "静夜思", "床前明月光", "疑是地上霜"), 3),
        Card("词语", Meta(AiPasteKind.BoardPlan, "", "", "判别式", "配方法", "因式分解"), 4),
    };

    private static void AssertMetadataEqual(AiPasteMetadata expected, string actualJson)
    {
        AiPasteMetadata actual = AiPasteMetadata.TryParse(actualJson);
        Assert.NotNull(actual);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Schema, actual.Schema);
        Assert.Equal(expected.Latex, actual.Latex);
        Assert.Equal(expected.Text, actual.Text);
        Assert.Equal(expected.Lines, actual.Lines);
        Assert.Equal(expected.StyleJson, actual.StyleJson);
        Assert.Equal(expected.Instruction, actual.Instruction);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.Notes, actual.Notes);
    }

    [Fact]
    public async Task BoardPlanCards_SurviveV4SaveAndReopen()
    {
        string path = TempFile("board-v4.icink");
        try
        {
            var board = new BoardService();
            BoardImageAsset[] cards = BoardPlanCards();
            IReadOnlyList<int> indexes = board.AddImagesAtomic(cards, "AI 板书");
            Assert.Equal(new[] { 0, 1, 2, 3 }, indexes);
            Assert.True(board.Current.History.UndoCount >= 1);

            await board.SaveBoardAsync(path);
            string fileText = File.ReadAllText(path);
            Assert.Contains("\"Version\": 4", fileText);

            var loaded = new BoardService();
            await loaded.LoadBoardAsync(path);

            Assert.Equal(4, loaded.CurrentImages.Count);
            for (int index = 0; index < cards.Length; index++)
            {
                BoardImageAsset actual = loaded.CurrentImages[index];
                Assert.Equal(cards[index].Id, actual.Id);
                Assert.Equal(cards[index].Name, actual.Name);
                Assert.Equal(cards[index].Data, actual.Data);
                Assert.Equal(cards[index].Left, actual.Left, 3);
                Assert.Equal(cards[index].Top, actual.Top, 3);
                Assert.Equal(cards[index].Width, actual.Width, 3);
                Assert.Equal(cards[index].Height, actual.Height, 3);
                AssertMetadataEqual(AiPasteMetadata.TryParse(cards[index].Metadata), actual.Metadata);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SideBoardAiCard_RoundTripsWithSideScope()
    {
        string path = TempFile("board-side.icink");
        try
        {
            var main = new BoardService();
            var side = new BoardService();
            main.SideBoardSource = side;

            BoardImageAsset card = Card("子板公式", Meta(AiPasteKind.BoardPlan, "x^2-1", "", "公式"), 5);
            Assert.Single(side.AddImagesAtomic(new[] { card }, "AI 板书"));

            await main.SaveBoardAsync(path);

            using (ZipArchive archive = ZipFile.OpenRead(path))
            {
                ZipArchiveEntry pretty = archive.GetEntry("board.pretty.json");
                Assert.NotNull(pretty);
                using var reader = new StreamReader(pretty.Open());
                string manifest = reader.ReadToEnd();
                Assert.Contains("\"Version\": 4", manifest);
                Assert.Contains("\"Scope\": \"side\"", manifest);
            }

            var loadedSide = new BoardService();
            var loaded = new BoardService { SideBoardSource = loadedSide };
            await loaded.LoadBoardAsync(path);

            Assert.Empty(loaded.CurrentImages);
            Assert.Single(loadedSide.CurrentImages);
            BoardImageAsset actual = loadedSide.CurrentImages[0];
            Assert.Equal(card.Data, actual.Data);
            AssertMetadataEqual(AiPasteMetadata.TryParse(card.Metadata), actual.Metadata);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}