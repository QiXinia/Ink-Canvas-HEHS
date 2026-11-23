using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using InkCanvas.Ink;
using System.Text.Json;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 时光机 AI 对象回放：事件格式、add/update/remove、检查点增量、旧会话兼容、
/// 录制器订阅 AiObjectsChanged，以及回放路径无网络/无模型依赖。
/// </summary>
public sealed class AiTimelineReplayTests
{
    private static TimelineAiObject Obj(string id, double left, string text = null, string png = null) => new()
    {
        Id = id,
        Kind = "text",
        Left = left,
        Top = 10,
        Width = 100,
        Height = 50,
        LayerOrder = (long)left,
        Text = text ?? ("t-" + id),
        Lines = new[] { "第一行", "第二行" },
        RenderPngBase64 = png ?? Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        Notes = new[] { "note-" + id },
    };

    private static TimelineEvent Ai(long timeMs, int page, int boardId, string action,
        params TimelineAiObject[] objects) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        BoardId = boardId,
        Kind = TimelineEventKind.AiStep,
        Payload = AiObjectPayloadCodec.Encode(action, page, boardId, objects),
    };

    private static TimelineAiObject NativeText(string id, double left, string content) => new()
    {
        Id = id,
        Kind = "wordList",
        IsNative = true,
        Content = content,
        Title = "原生标题",
        Author = "教师",
        Dynasty = "唐",
        FontFamily = "Microsoft YaHei UI",
        FontSize = 28,
        Color = "#FF112233",
        LineHeight = 1.5,
        Align = "center",
        Columns = 2,
        Left = left,
        Top = 10,
        Width = 200,
        Height = 80,
        LayerOrder = (long)left,
        GroupId = "native-group",
    };

    private static TimelineAiObject NativeFormula(string id, double left, string latex) => new()
    {
        Id = id,
        Kind = "formula",
        IsNative = true,
        Latex = latex,
        FontFamily = "Cambria Math",
        FontSize = 30,
        Color = "#FF112233",
        Left = left,
        Top = 10,
        Width = 180,
        Height = 70,
        LayerOrder = (long)left,
        GroupId = "native-group",
    };

    private static TimelineEvent AiNative(long timeMs, int page, int boardId, string action,
        params TimelineAiObject[] nativeObjects) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        BoardId = boardId,
        Kind = TimelineEventKind.AiStep,
        Payload = AiObjectPayloadCodec.Encode(
            action, page, boardId, Array.Empty<TimelineAiObject>(), nativeObjects),
    };

    private static TimelineEvent Page(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static ClassroomSession Session(params TimelineEvent[] events) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = "ai-timeline",
        CreatedAt = DateTime.Now,
        Events = events.ToList(),
        Checkpoints = new List<SessionCheckpoint>(),
        Bookmarks = new List<SessionBookmark>(),
    };

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"inkcanvas-ai-timeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void RemoveDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 测试清理失败不影响断言 */ }
    }

    private static InkStroke Stroke()
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 2, TaperKind = TaperKind.None });
        stroke.AddPoint(1, 2);
        stroke.AddPoint(20, 30);
        stroke.Complete();
        return stroke;
    }

    private static AiPasteMetadata Meta() => new()
    {
        Kind = AiPasteKind.BoardPlan,
        Schema = 1,
        Latex = "x^2",
        Text = "AI 板书",
        Lines = new List<string> { "第一行", "第二行" },
        StyleJson = "{\"fontSize\":24}",
        Instruction = "整理当前页板书",
        CreatedAt = 1730000000000,
        Notes = new List<string> { "低置信" },
    };

    private static BoardImageAsset Card(byte[] data = null) => new()
    {
        Name = "AI 板书·文本",
        Data = data ?? new byte[] { 1, 2, 3, 4 },
        ContentType = "image/png",
        Left = 10,
        Top = 20,
        Width = 200,
        Height = 100,
        Metadata = Meta().Serialize(),
    };

    // ---- 回放 ------------------------------------------------------------

    [Fact]
    public void BuildObjectFrame_AppliesAddUpdateRemove()
    {
        var session = Session(
            Page(0, 0),
            Ai(100, 0, 0, "add", Obj("o1", 1), Obj("o2", 2)),
            Ai(200, 0, 0, "update", Obj("o1", 1, text: "updated")),
            Ai(300, 0, 0, "remove", Obj("o2", 2)));
        var timeline = new InkTimeline(session);

        Assert.Empty(timeline.BuildObjectFrame(50));

        IReadOnlyList<TimelineAiObject> at150 = timeline.BuildObjectFrame(150);
        Assert.Equal(2, at150.Count);
        Assert.Equal(new[] { "o1", "o2" }, at150.Select(item => item.Id));
        // 尚未发生的 update 不能提前显示。
        Assert.Equal("t-o1", at150.Single(item => item.Id == "o1").Text);

        IReadOnlyList<TimelineAiObject> at250 = timeline.BuildObjectFrame(250);
        Assert.Equal(2, at250.Count);
        Assert.Equal("updated", at250.Single(item => item.Id == "o1").Text);

        IReadOnlyList<TimelineAiObject> at350 = timeline.BuildObjectFrame(350);
        Assert.Single(at350);
        Assert.Equal("o1", at350[0].Id);
    }

    [Fact]
    public void BuildObjectFrame_ReplaysStructuredNativeObjectsWithTimeCutoff()
    {
        var session = Session(
            Page(0, 0),
            AiNative(100, 0, 0, "add", NativeText("n1", 1, "一"), NativeFormula("f1", 2, "x^2")),
            AiNative(200, 0, 0, "update", NativeText("n1", 1, "二")),
            AiNative(300, 0, 0, "remove", NativeFormula("f1", 2, "x^2")));
        var timeline = new InkTimeline(session);

        IReadOnlyList<TimelineAiObject> at150 = timeline.BuildObjectFrame(150);
        Assert.Equal(2, at150.Count);
        Assert.All(at150, item => Assert.True(item.IsNative && !item.HasRenderPng));
        Assert.Equal("一", at150.Single(item => item.Id == "n1").Content);
        Assert.Equal("x^2", at150.Single(item => item.Id == "f1").Latex);

        IReadOnlyList<TimelineAiObject> at250 = timeline.BuildObjectFrame(250);
        Assert.Equal("二", at250.Single(item => item.Id == "n1").Content);

        IReadOnlyList<TimelineAiObject> at350 = timeline.BuildObjectFrame(350);
        Assert.Single(at350);
        Assert.Equal("n1", at350[0].Id);

        var checkpointed = new InkTimeline(session, eventsPerCheckpoint: 1, checkpointIntervalMs: 1);
        IReadOnlyList<TimelineAiObject> checkpointFrame = checkpointed.BuildObjectFrame(250);
        Assert.Equal(2, checkpointFrame.Count);
        Assert.All(session.Checkpoints, checkpoint => Assert.NotNull(checkpoint.LiveObjectIds));
    }

    [Fact]
    public void BuildObjectFrame_FiltersByPageAndBoardId()
    {
        var session = Session(
            Page(0, 0),
            Ai(100, 0, 0, "add", Obj("main-o", 1)),
            Ai(200, 0, 1, "add", Obj("side-o", 2)),
            Page(300, 1),
            Ai(400, 1, 0, "add", Obj("page1-o", 3)));
        var timeline = new InkTimeline(session);

        IReadOnlyList<TimelineAiObject> page0Main = timeline.BuildObjectFrame(250);
        Assert.Single(page0Main);
        Assert.Equal("main-o", page0Main[0].Id);

        IReadOnlyList<TimelineAiObject> page0Side = timeline.BuildObjectFrame(250, 1);
        Assert.Single(page0Side);
        Assert.Equal("side-o", page0Side[0].Id);

        IReadOnlyList<TimelineAiObject> page1 = timeline.BuildObjectFrame(450);
        Assert.Single(page1);
        Assert.Equal("page1-o", page1[0].Id);
    }

    [Fact]
    public void BuildObjectFrame_UsesCheckpointIncrementallyAndSurvivesReload()
    {
        var session = Session(
            Page(0, 0),
            Ai(100, 0, 0, "add", Obj("o1", 1)),
            Ai(200, 0, 0, "add", Obj("o2", 2)),
            Ai(300, 0, 0, "update", Obj("o1", 1, text: "after")));
        var timeline = new InkTimeline(session, eventsPerCheckpoint: 1, checkpointIntervalMs: 1);

        IReadOnlyList<TimelineAiObject> frame = timeline.BuildObjectFrame(300);
        Assert.Equal(2, frame.Count);
        Assert.NotEmpty(session.Checkpoints);
        Assert.All(session.Checkpoints, checkpoint => Assert.NotNull(checkpoint.LiveObjectIds));
        Assert.Contains("o2", session.Checkpoints[^1].LiveObjectIds);

        // 重新加载（复用已落盘检查点）仍需得到同一对象集合。
        var reloaded = new InkTimeline(session);
        IReadOnlyList<TimelineAiObject> again = reloaded.BuildObjectFrame(300);
        Assert.Equal(new[] { "o1", "o2" }, again.Select(item => item.Id));
        Assert.Equal("after", again.Single(item => item.Id == "o1").Text);
    }

    [Fact]
    public void LegacyCheckpointWithoutLiveObjectIds_RebuildsFromStart()
    {
        var session = Session(
            Page(0, 0),
            Ai(100, 0, 0, "add", Obj("legacy-o", 1)));
        session.Checkpoints.Add(new SessionCheckpoint
        {
            TimeMs = 100,
            EventIndex = 1,
            Seq = 2,
            PageIndex = 0,
            Background = "Black",
            LiveStrokeIds = Array.Empty<string>(),
            // 旧会话没有该字段：不能作为对象恢复起点。
            LiveObjectIds = null,
        });

        var timeline = new InkTimeline(session);

        IReadOnlyList<TimelineAiObject> frame = timeline.BuildObjectFrame(150);
        Assert.Single(frame);
        Assert.Equal("legacy-o", frame[0].Id);
    }

    [Fact]
    public void SessionSaveLoad_PreservesAiEventsAndBoardId()
    {
        string dir = TempDir();
        try
        {
            var session = Session(
                Page(0, 0),
                Ai(100, 0, 1, "add", Obj("side-card", 1)));
            string path = ClassroomSessionStore.Save(session, dir);

            ClassroomSession loaded = ClassroomSessionStore.Load(path);
            TimelineEvent ai = Assert.Single(loaded.Events, item => item.Kind == TimelineEventKind.AiStep);
            Assert.Equal(1, ai.BoardId);
            Assert.True(AiObjectPayloadCodec.TryDecode(ai.Payload, out AiObjectsPayload payload));
            Assert.Equal("add", payload.Action);
            Assert.Single(payload.Objects);

            var timeline = new InkTimeline(loaded);
            Assert.Single(timeline.BuildObjectFrame(150, 1));
            Assert.Empty(timeline.BuildObjectFrame(150, 0));
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void LegacySessionWithoutBoardId_DefaultsToMainBoard()
    {
        var payload = AiObjectPayloadCodec.Encode("add", 0, 0, new[] { Obj("legacy", 1) });
        var legacy = new
        {
            id = "legacy-session",
            name = "旧会话",
            createdAt = "2026-01-01T00:00:00",
            events = new[]
            {
                new { timeMs = 100L, pageIndex = 0, kind = TimelineEventKind.AiStep, payload },
            },
            checkpoints = Array.Empty<object>(),
        };
        string json = JsonSerializer.Serialize(legacy);

        var session = JsonSerializer.Deserialize<ClassroomSession>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        Assert.NotNull(session);
        Assert.Equal(0, session!.Events[0].BoardId);

        var timeline = new InkTimeline(session);
        Assert.Single(timeline.BuildObjectFrame(150));
    }

    [Fact]
    public void Payload_StartsWithObjectIdAndAction_ForDedupeSignature()
    {
        string payload = AiObjectPayloadCodec.Encode("update", 2, 1, new[] { Obj("object-42", 5) });

        Assert.StartsWith("{\"objectId\":\"object-42\",\"action\":\"update\",", payload);
        Assert.True(AiObjectPayloadCodec.TryDecode(payload, out AiObjectsPayload decoded));
        Assert.Equal(2, decoded.PageIndex);
        Assert.Equal(1, decoded.BoardId);
        Assert.Equal("object-42", decoded.Objects[0].Id);
    }

    [Fact]
    public void ReplayPath_UsesStoredPngAndHasNoNetworkDependency()
    {
        byte[] png = new byte[] { 9, 8, 7, 6 };
        string base64 = Convert.ToBase64String(png);
        var session = Session(
            Page(0, 0),
            Ai(100, 0, 0, "add", Obj("stored", 1, png: base64)));
        var timeline = new InkTimeline(session);

        TimelineAiObject item = Assert.Single(timeline.BuildObjectFrame(150));
        Assert.Equal(base64, item.RenderPngBase64);
        Assert.False(item.RenderMissing);

        // 静态隔离：回放所在的 Core 程序集不依赖 Platform / AI 客户端 / HTTP。
        var references = typeof(InkTimeline).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();
        Assert.DoesNotContain("InkCanvas.Platform", references);
        Assert.DoesNotContain("System.Net.Http", references);
        Assert.Null(typeof(InkTimeline).Assembly.GetType("InkCanvas.Platform.Ai.AiClient"));
    }

    // ---- 录制 ------------------------------------------------------------

    [Fact]
    public void Recorder_RecordsAiAddUpdateRemoveAndBoardId()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board, boardId: 1) { SessionDirectory = dir };
            recorder.Start("AI 板书录制");

            BoardImageAsset card = Card();
            string originalPng = Convert.ToBase64String(card.Data);
            Assert.Single(board.AddImagesAtomic(new[] { card }, "AI 板书"));

            var updated = Meta();
            updated.Text = "更新后的文本";
            byte[] newData = new byte[] { 9, 9, 9, 9 };
            Assert.True(board.UpdateImage(card, newData, updated.Serialize()));

            board.RemoveImage(card);

            List<TimelineEvent> aiEvents = recorder.Session.Events
                .Where(item => item.Kind == TimelineEventKind.AiStep)
                .ToList();
            Assert.Equal(3, aiEvents.Count);
            Assert.All(aiEvents, item => Assert.Equal(1, item.BoardId));

            Assert.Equal("add", Decode(aiEvents[0]).Action);
            Assert.Equal(originalPng, Decode(aiEvents[0]).Objects[0].RenderPngBase64);
            Assert.Equal("update", Decode(aiEvents[1]).Action);
            Assert.Equal("更新后的文本", Decode(aiEvents[1]).Objects[0].Text);
            Assert.Equal("remove", Decode(aiEvents[2]).Action);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void Recorder_RecordsReplaceAsStrokesRemovedThenAiStep()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            InkStroke stroke = Stroke();
            board.Current.Document.Add(stroke);

            using var recorder = new InkRecorder(board) { SessionDirectory = dir };
            recorder.Start("AI 替换录制");

            BoardImageAsset card = Card();
            Assert.True(board.ReplaceStrokesWithImagesAtomic(new[] { stroke }, new[] { card }, "AI 板书替换"));

            List<TimelineEvent> events = recorder.Session.Events
                .Where(item => item.Kind is TimelineEventKind.StrokesRemoved or TimelineEventKind.AiStep)
                .ToList();
            Assert.Equal(2, events.Count);
            Assert.Equal(TimelineEventKind.StrokesRemoved, events[0].Kind);
            Assert.Contains(stroke.Id.ToString(), events[0].StrokeIds);
            Assert.Equal(TimelineEventKind.AiStep, events[1].Kind);
            Assert.Equal("replace", Decode(events[1]).Action);
            Assert.Equal(card.Id.ToString(), Decode(events[1]).Objects[0].Id);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void Recorder_OversizedPngFallsBackToMissingPlaceholder()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board) { SessionDirectory = dir };
            recorder.Start("大 PNG 录制");

            var large = Card(new byte[AiObjectPayloadCodec.MaxRenderPngBytes + 1]);
            Assert.Single(board.AddImagesAtomic(new[] { large }, "AI 板书"));

            TimelineEvent ai = Assert.Single(recorder.Session.Events,
                item => item.Kind == TimelineEventKind.AiStep);
            AiObjectsPayload payload = Decode(ai);
            Assert.True(payload.Objects[0].RenderMissing);
            Assert.Equal("", payload.Objects[0].RenderPngBase64);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void Recorder_WritesStructuredNativePayloadAndCheckpointReplay()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board) { SessionDirectory = dir };
            recorder.Start("原生结构化录制");

            var text = new BoardTextObject
            {
                Type = "wordList",
                Content = "判别式\n配方法",
                Title = "关键词",
                Author = "教师",
                Dynasty = "唐",
                FontFamily = "Microsoft YaHei UI",
                FontSize = 28,
                Color = "#FF112233",
                LineHeight = 1.6,
                Align = "center",
                Columns = 2,
                Left = 12,
                Top = 24,
                Width = 280,
                Height = 140,
                Source = "ai",
                Instruction = "结构化回放",
                CreatedAt = 1_730_000_000_000,
            };
            var formula = new BoardFormulaObject
            {
                Latex = "\\frac{1}{2}x^2",
                FontFamily = "Cambria Math",
                FontSize = 30,
                Color = "#FF112233",
                Left = 12,
                Top = 200,
                Width = 220,
                Height = 90,
                Source = "ai",
                Instruction = "结构化回放",
                CreatedAt = 1_730_000_000_000,
            };

            board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "native-group");
            recorder.Stop();

            ClassroomSession session = recorder.Session;
            TimelineEvent ai = Assert.Single(session.Events,
                item => item.Kind == TimelineEventKind.AiStep);
            AiObjectsPayload payload = Decode(ai);

            Assert.Empty(payload.Objects);
            Assert.Equal(2, payload.NativeObjects.Count);
            Assert.All(payload.NativeObjects, item =>
                Assert.True(item.IsNative && !item.HasRenderPng && item.RenderPngBase64.Length == 0));
            TimelineAiObject textSnapshot = payload.NativeObjects.Single(item => item.Kind == "wordList");
            Assert.Equal("关键词", textSnapshot.Title);
            Assert.Equal(2, textSnapshot.Columns);
            Assert.Equal(12, textSnapshot.Left, 3);
            TimelineAiObject formulaSnapshot = payload.NativeObjects.Single(item => item.Kind == "formula");
            Assert.Equal("\\frac{1}{2}x^2", formulaSnapshot.Latex);

            long time = session.Events[^1].TimeMs;
            IReadOnlyList<TimelineAiObject> frame = new InkTimeline(session).BuildObjectFrame(time);
            Assert.Equal(2, frame.Count);
            Assert.All(frame, item => Assert.True(item.IsNative && !item.HasRenderPng));

            var checkpointed = new InkTimeline(session, eventsPerCheckpoint: 1, checkpointIntervalMs: 1);
            IReadOnlyList<TimelineAiObject> checkpointFrame = checkpointed.BuildObjectFrame(time);
            Assert.Equal(2, checkpointFrame.Count);
            Assert.NotEmpty(session.Checkpoints);
            Assert.All(session.Checkpoints, checkpoint => Assert.NotNull(checkpoint.LiveObjectIds));

            IReadOnlyList<TimelineAiObject> reloaded = new InkTimeline(session).BuildObjectFrame(time);
            Assert.Equal(2, reloaded.Count);
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    [Fact]
    public void Recorder_WritesStructuredUpdateRemoveAndReplaceEvents()
    {
        string dir = TempDir();
        try
        {
            var board = new BoardService();
            using var recorder = new InkRecorder(board) { SessionDirectory = dir };
            recorder.Start("原生动作录制");

            var text = new BoardTextObject
            {
                Type = "text", Content = "初始", Title = "文本",
                FontSize = 24, Left = 10, Top = 10, Width = 200, Height = 80,
                Source = "ai", Instruction = "结构化动作", CreatedAt = 1_730_000_000_000,
            };
            var formula = new BoardFormulaObject
            {
                Latex = "x^2", FontSize = 30, Left = 10, Top = 120, Width = 180, Height = 70,
                Source = "ai", Instruction = "结构化动作", CreatedAt = 1_730_000_000_000,
            };
            board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "group-a");
            Assert.True(board.UpdateAiObject(text, target => target.Content = "更新"));
            Assert.True(board.RemoveAiObjects(new ISelectionElement[] { formula }, "删除 AI 对象"));

            InkStroke stroke = Stroke();
            board.Current.Document.Add(stroke);
            var replacement = new BoardTextObject
            {
                Type = "text", Content = "替换", Left = 20, Top = 20, Width = 200, Height = 80,
                Source = "ai", Instruction = "结构化动作", CreatedAt = 1_730_000_000_000,
            };
            Assert.Single(board.ReplaceStrokesWithAiObjectsAtomic(
                new[] { stroke }, new[] { replacement }, null, "替换 AI 板书", "group-b"));
            recorder.Stop();

            List<TimelineEvent> aiEvents = recorder.Session.Events
                .Where(item => item.Kind == TimelineEventKind.AiStep)
                .ToList();
            Assert.Equal(4, aiEvents.Count);
            Assert.Equal(new[] { "add", "update", "remove", "replace" },
                aiEvents.Select(item => Decode(item).Action));
            Assert.All(aiEvents, item =>
            {
                AiObjectsPayload payload = Decode(item);
                Assert.Empty(payload.Objects);
                Assert.NotEmpty(payload.NativeObjects);
                Assert.All(payload.NativeObjects,
                    obj => Assert.True(obj.IsNative && !obj.HasRenderPng));
            });
        }
        finally
        {
            RemoveDir(dir);
        }
    }

    private static AiObjectsPayload Decode(TimelineEvent item)
    {
        Assert.True(AiObjectPayloadCodec.TryDecode(item.Payload, out AiObjectsPayload payload));
        return payload;
    }
}