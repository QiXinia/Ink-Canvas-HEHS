using InkCanvas.Core.Classroom;
using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 本地例题库回归：新增 3 条往返、各检索条件分别命中、删除生效、
/// 功能开关关闭时的降级，以及损坏 / 增量 JSON 的兼容行为。
/// 全部使用临时目录，测试结束递归清理。
/// </summary>
public sealed class ExampleLibraryTests : IDisposable
{
    private readonly string _root;

    public ExampleLibraryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-example-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论；系统会在后续清理 %TEMP%。
        }
    }

    [Fact]
    public void Add_ThreeEntries_LoadRoundTrip()
    {
        string directory = NewLibraryDirectory();
        List<ExampleEntry> entries = BuildEntries();

        foreach (ExampleEntry entry in entries)
        {
            Assert.True(ExampleLibraryStore.Add(entry, directory), "新增例题应成功：" + entry.Id);
        }

        IReadOnlyList<ExampleEntry> loaded = ExampleLibraryStore.Load(directory);

        Assert.Equal(3, loaded.Count);
        foreach (ExampleEntry expected in entries)
        {
            ExampleEntry actual = loaded.Single(item => item.Id == expected.Id);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Topic, actual.Topic);
            Assert.Equal(expected.KnowledgePoints, actual.KnowledgePoints);
            Assert.Equal(expected.SourcePresentation, actual.SourcePresentation);
            Assert.Equal(expected.SourceSlideId, actual.SourceSlideId);
            Assert.Equal(expected.BoardPage, actual.BoardPage);
            Assert.Equal(expected.CreatedAt, actual.CreatedAt);
            Assert.Equal(expected.Answer, actual.Answer);
            Assert.Equal(expected.CorrectedText, actual.CorrectedText);
            Assert.Equal(1, actual.Version);
        }

        Assert.True(File.Exists(ExampleLibraryStore.PathFor(directory)), "例题库文件应落在指定目录。");
    }

    [Fact]
    public void Search_EachCondition_ReturnsExpectedEntry()
    {
        string directory = NewLibraryDirectory();
        Seed(directory);

        IReadOnlyList<ExampleEntry> byTopic =
            ExampleLibraryStore.Search(new ExampleQuery { Topic = "一次函数" }, directory);
        Assert.Single(byTopic);
        Assert.Equal("ex-001", byTopic[0].Id);

        IReadOnlyList<ExampleEntry> byKnowledgePoint =
            ExampleLibraryStore.Search(new ExampleQuery { KnowledgePoint = "勾股定理" }, directory);
        Assert.Single(byKnowledgePoint);
        Assert.Equal("ex-002", byKnowledgePoint[0].Id);

        IReadOnlyList<ExampleEntry> bySource =
            ExampleLibraryStore.Search(new ExampleQuery { SourcePresentation = "5.3 化学方程式.pptx" }, directory);
        Assert.Single(bySource);
        Assert.Equal("ex-003", bySource[0].Id);

        IReadOnlyList<ExampleEntry> byRange = ExampleLibraryStore.Search(
            new ExampleQuery
            {
                From = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                To = new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc),
            },
            directory);
        Assert.Equal(2, byRange.Count);
        Assert.DoesNotContain(byRange, entry => entry.Id == "ex-003");

        IReadOnlyList<ExampleEntry> byKeyword =
            ExampleLibraryStore.Search(new ExampleQuery { Keyword = "质量守恒" }, directory);
        Assert.Single(byKeyword);
        Assert.Equal("ex-003", byKeyword[0].Id);

        Assert.Equal(3, ExampleLibraryStore.Search(new ExampleQuery(), directory).Count);
        Assert.Equal(3, ExampleLibraryStore.Search(null, directory).Count);
    }

    [Fact]
    public void Delete_RemovesEntry_AndKeepsOthers()
    {
        string directory = NewLibraryDirectory();
        Seed(directory);

        Assert.True(ExampleLibraryStore.Delete("ex-002", directory), "删除已存在例题应返回 true。");

        IReadOnlyList<ExampleEntry> remaining = ExampleLibraryStore.Load(directory);
        Assert.Equal(2, remaining.Count);
        Assert.DoesNotContain(remaining, entry => entry.Id == "ex-002");
        Assert.Contains(remaining, entry => entry.Id == "ex-001");
        Assert.Contains(remaining, entry => entry.Id == "ex-003");
        Assert.False(ExampleLibraryStore.Delete("ex-002", directory), "重复删除应返回 false。");
    }

    [Fact]
    public void DisabledFeature_AddFailsAndSearchEmpty_ButLoadStillWorks()
    {
        string directory = NewLibraryDirectory();
        Seed(directory);
        var settings = new FeatureSettings { EnableExampleLibrary = false };
        var entry = new ExampleEntry { Id = "ex-009", Title = "关闭时新增", Topic = "开关" };

        Assert.False(
            ExampleLibraryStore.Add(entry, directory, settings),
            "例题库关闭时新增应返回 false。");
        Assert.Empty(ExampleLibraryStore.Search(new ExampleQuery(), directory, settings));
        Assert.Empty(ExampleLibraryStore.Search(null, directory, settings));

        IReadOnlyList<ExampleEntry> loaded = ExampleLibraryStore.Load(directory);
        Assert.Equal(3, loaded.Count);
        Assert.DoesNotContain(loaded, item => item.Id == "ex-009");
        Assert.False(ExampleLibraryStore.Delete("ex-001", directory, settings), "例题库关闭时删除应返回 false。");
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyAndKeepsFile()
    {
        string directory = NewLibraryDirectory();
        Directory.CreateDirectory(directory);
        string path = ExampleLibraryStore.PathFor(directory);
        File.WriteAllText(path, "{ 这不是合法的 JSON");

        IReadOnlyList<ExampleEntry> loaded = ExampleLibraryStore.Load(directory);

        Assert.Empty(loaded);
        Assert.True(File.Exists(path), "损坏的例题库文件不应被删除，便于人工修复。");
    }

    [Fact]
    public void Load_IgnoresUnknownFields_AndAcceptsNewerVersion()
    {
        string directory = NewLibraryDirectory();
        Directory.CreateDirectory(directory);
        File.WriteAllText(ExampleLibraryStore.PathFor(directory), """
            {
              "version": 99,
              "futureField": "ignored",
              "examples": [
                {
                  "id": "ex-010",
                  "title": "增量兼容例题",
                  "topic": "兼容性",
                  "knowledgePoints": ["增量"],
                  "sourcePresentation": "兼容.pptx",
                  "sourceSlideId": 1,
                  "boardPage": 0,
                  "createdAt": "2026-05-01T00:00:00Z",
                  "answer": "A",
                  "correctedText": "",
                  "version": 2,
                  "unknownEntryField": true
                }
              ]
            }
            """);

        IReadOnlyList<ExampleEntry> loaded = ExampleLibraryStore.Load(directory);

        Assert.Single(loaded);
        Assert.Equal("ex-010", loaded[0].Id);
        Assert.Equal("增量兼容例题", loaded[0].Title);
        Assert.Equal(new List<string> { "增量" }, loaded[0].KnowledgePoints);
    }

    private string NewLibraryDirectory()
        => Path.Combine(_root, "library-" + Guid.NewGuid().ToString("N"));

    private void Seed(string directory)
    {
        foreach (ExampleEntry entry in BuildEntries())
        {
            Assert.True(ExampleLibraryStore.Add(entry, directory), "预置例题应成功：" + entry.Id);
        }
    }

    private static List<ExampleEntry> BuildEntries()
        => new()
        {
            new ExampleEntry
            {
                Id = "ex-001",
                Title = "一次函数图象与性质",
                Topic = "一次函数",
                KnowledgePoints = new List<string> { "一次函数", "图象与性质" },
                SourcePresentation = "3.2 一次函数.pptx",
                SourceSlideId = 5,
                BoardPage = 1,
                CreatedAt = new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc),
                Answer = "y = kx + b",
                CorrectedText = "斜率 k 决定倾斜方向",
            },
            new ExampleEntry
            {
                Id = "ex-002",
                Title = "勾股定理应用",
                Topic = "勾股定理",
                KnowledgePoints = new List<string> { "勾股定理", "直角三角形" },
                SourcePresentation = "4.1 三角形.pptx",
                SourceSlideId = 12,
                BoardPage = 2,
                CreatedAt = new DateTime(2026, 3, 10, 9, 30, 0, DateTimeKind.Utc),
                Answer = "a² + b² = c²",
                CorrectedText = "先确认直角边再代入",
            },
            new ExampleEntry
            {
                Id = "ex-003",
                Title = "化学方程式配平",
                Topic = "化学方程式",
                KnowledgePoints = new List<string> { "配平", "质量守恒" },
                SourcePresentation = "5.3 化学方程式.pptx",
                SourceSlideId = 8,
                BoardPage = 3,
                CreatedAt = new DateTime(2026, 4, 1, 14, 15, 0, DateTimeKind.Utc),
                Answer = "配平后两边原子数相等",
                CorrectedText = "从氧原子开始配平更稳",
            },
        };
}