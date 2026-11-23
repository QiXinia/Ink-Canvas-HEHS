using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InkCanvas.App.Services;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Features.Classroom;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

/// <summary>
/// AI 板书原生对象诊断（<c>--aiboardcheck=&lt;dir&gt;</c>）：在真实 App 与真实
/// <see cref="BoardService"/> 中验证 BoardPlanWriter 只生成文本/公式对象（不生成 PNG）、
/// 原子写入/撤销、替换原板书、保存重开、子板 Scope、组选择/取消分组与主/子板路由。
/// 不发起任何 AI HTTP 请求。
/// </summary>
internal static class AiBoardRegression
{
    private const string LogPrefix = "[AI][AiBoardCheck]";
    private const long FixedCreatedAt = 1_730_000_000_000;

    private sealed record CheckItem(string Check, bool Passed, bool Optional, double ElapsedMs,
        Dictionary<string, object> Details);

    private sealed class CheckLog
    {
        private readonly List<CheckItem> _items = new();
        private readonly Stopwatch _phase = Stopwatch.StartNew();

        public void Add(string check, bool passed, params (string Key, object Value)[] details)
            => AddCore(check, passed, optional: false, details);

        public void AddOptional(string check, bool passed, params (string Key, object Value)[] details)
            => AddCore(check, passed, optional: true, details);

        private void AddCore(string check, bool passed, bool optional, (string Key, object Value)[] details)
        {
            var values = new Dictionary<string, object>();
            foreach (var (key, value) in details) values[key] = value;
            _items.Add(new CheckItem(check, passed, optional, Math.Round(_phase.Elapsed.TotalMilliseconds, 1), values));
            _phase.Restart();
            FileLog.WriteLogToFile(
                $"{LogPrefix} {(passed ? "PASS" : "FAIL")} {check}",
                passed ? LogType.Info : LogType.Error);
        }

        public IReadOnlyList<CheckItem> Items => _items;

        public bool Passed => _items.All(item => item.Passed || item.Optional);
    }

    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var log = new CheckLog();
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        string textPath = Path.Combine(directory, "ai-board-check.txt");
        string jsonPath = Path.Combine(directory, "ai-board-check.json");
        FileLog.WriteLogToFile($"{LogPrefix} 开始，输出目录：{directory}", LogType.Info);

        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(directory, "captures");
        using var shell = new ShellController(services);

        try
        {
            shell.Start();
            shell.StartInClickThrough();
            await Task.Delay(250);

            AssistantBoardPlan plan = BuildPlan();
            BoardPlanNativeResult created = BoardPlanWriter.Create(plan, "aiboardcheck: 整理当前页板书", FixedCreatedAt);
            log.Add("board plan writer creates native objects only",
                created.Success && created.Texts.Count == 3 && created.Formulas.Count == 1,
                ("texts", created.Texts.Count), ("formulas", created.Formulas.Count),
                ("error", created.Error));
            if (!created.Success) throw new InvalidOperationException("BoardPlanWriter 未生成原生对象：" + created.Error);

            bool fieldsOk = created.Texts.All(text => text.Source == "ai" && text.Instruction.Length > 0)
                && created.Texts.Any(text => text.Type == "wordList" && text.Columns == 2)
                && created.Texts.Any(text => text.Type == "poem" && text.Title == "静夜思" && text.Author.Contains("李白"))
                && created.Formulas[0].Latex.Contains("x^2");
            log.Add("native text/formula fields keep block style and source",
                fieldsOk,
                ("types", string.Join(",", created.Texts.Select(text => text.Type))),
                ("wordColumns", created.Texts.FirstOrDefault(text => text.Type == "wordList")?.Columns ?? 0),
                ("poem", created.Texts.FirstOrDefault(text => text.Type == "poem")?.Title ?? ""),
                ("latex", created.Formulas[0].Latex));

            var board = services.Board;
            int imagesBefore = board.CurrentImages.Count;
            string groupId = Guid.NewGuid().ToString("N");
            IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
                created.Texts, created.Formulas, "AI 板书", groupId);
            log.Add("atomic add creates native objects and no images",
                ids.Count == 4
                    && board.CurrentTextObjects.Count == 3
                    && board.CurrentFormulaObjects.Count == 1
                    && board.CurrentImages.Count == imagesBefore
                    && board.Current.History.UndoCount == 1,
                ("ids", ids.Count), ("texts", board.CurrentTextObjects.Count),
                ("formulas", board.CurrentFormulaObjects.Count),
                ("images", board.CurrentImages.Count), ("undoCount", board.Current.History.UndoCount));

            board.Current.Undo();
            bool undoOk = board.CurrentTextObjects.Count == 0 && board.CurrentFormulaObjects.Count == 0;
            board.Current.Redo();
            bool redoOk = board.CurrentTextObjects.Count == 3 && board.CurrentFormulaObjects.Count == 1;
            log.Add("one undo removes all native objects and redo restores them",
                undoOk && redoOk,
                ("textsAfterUndo", undoOk ? 0 : board.CurrentTextObjects.Count),
                ("textsAfterRedo", board.CurrentTextObjects.Count),
                ("formulasAfterRedo", board.CurrentFormulaObjects.Count));

            // 组选择：选中一个对象应带出同 GroupId 的全部对象。
            var first = board.GetSelectionElements()
                .FirstOrDefault(element => element is IGroupedSelectionElement grouped
                    && grouped.SelectionGroupId == groupId);
            if (first is not null) board.Current.SelectElement(first);
            log.Add("selecting one native object selects the whole AI group",
                board.Current.SelectedElements.Count == 4,
                ("selected", board.Current.SelectedElements.Count), ("group", groupId));

            int ungrouped = board.UngroupAiObjects(groupId);
            bool ungroupOk = ungrouped == 4
                && board.CurrentTextObjects.All(text => string.IsNullOrEmpty(text.GroupId))
                && board.CurrentFormulaObjects.All(formula => string.IsNullOrEmpty(formula.GroupId));
            board.Current.Undo();
            bool regroupOk = board.CurrentTextObjects.All(text => text.GroupId == groupId)
                && board.CurrentFormulaObjects.All(formula => formula.GroupId == groupId);
            log.Add("ungroup is one undo step and restores the group",
                ungroupOk && regroupOk,
                ("ungrouped", ungrouped),
                ("regrouped", board.CurrentTextObjects.All(text => text.GroupId == groupId)));

            CheckReplace(log, createdAt: FixedCreatedAt);
            await CheckSaveAndReopenAsync(log, created, directory);
            await CheckSideBoardAsync(log, directory);
            CheckTimeMachine(log, created, directory);
            CheckRouting(log, shell, services);

            log.Add("diagnostic completed without exception", true);
        }
        catch (Exception ex)
        {
            log.Add("aiboardcheck exception", false, ("error", ex.ToString()));
        }
        finally
        {
            try { shell.Dispose(); } catch { /* 清理不影响结果 */ }
            WriteReport(textPath, jsonPath, log, jsonOptions);
            bool passed = log.Passed;
            FileLog.WriteLogToFile(
                $"{LogPrefix} 完成：{log.Items.Count(item => item.Passed)}/{log.Items.Count} 通过，退出码 {(passed ? 0 : 1)}",
                passed ? LogType.Info : LogType.Error);
            Environment.ExitCode = passed ? 0 : 1;
            Application.Current.Exit();
        }
    }

    private static AssistantBoardPlan BuildPlan() => new()
    {
        Schema = 1,
        Intent = "organize",
        TargetBoard = "main",
        TargetPageIndex = 0,
        Blocks = new[]
        {
            new AssistantBoardBlock
            {
                Id = "b1",
                Kind = "formula",
                Latex = "\\frac{1}{2}x^2-3x+2=0",
                Style = new AssistantBoardStyle { FontSize = 28, Align = "center", Columns = 1, LineHeight = 1.4 },
            },
            new AssistantBoardBlock
            {
                Id = "b2",
                Kind = "text",
                Title = "一元二次方程",
                Text = "配方法与判别式",
                Style = new AssistantBoardStyle { FontSize = 24, Align = "left", Columns = 1, LineHeight = 1.4 },
            },
            new AssistantBoardBlock
            {
                Id = "b3",
                Kind = "wordlist",
                Title = "关键词",
                Lines = new[] { "判别式", "配方法", "因式分解" },
                Style = new AssistantBoardStyle { FontSize = 24, Align = "left", Columns = 2, LineHeight = 1.5 },
            },
            new AssistantBoardBlock
            {
                Id = "b4",
                Kind = "poem",
                Title = "静夜思",
                Author = "唐·李白",
                Lines = new[] { "床前明月光", "疑是地上霜", "举头望明月", "低头思故乡" },
                Style = new AssistantBoardStyle { FontSize = 26, Align = "center", Columns = 1, LineHeight = 1.6 },
            },
        },
    };

    private static void CheckReplace(CheckLog log, long createdAt)
    {
        var board = new BoardService();
        InkStroke first = SyntheticStroke(120, 120);
        InkStroke second = SyntheticStroke(220, 180);
        board.Current.Document.Add(first);
        board.Current.Document.Add(second);

        var texts = new[]
        {
            new BoardTextObject
            {
                Type = "text", Content = "替换文本", Left = 40, Top = 40, Width = 300, Height = 120,
                Source = "ai", Instruction = "替换", CreatedAt = createdAt,
            },
        };
        var formulas = new[]
        {
            new BoardFormulaObject
            {
                Latex = "a^2+b^2=c^2", Left = 40, Top = 180, Width = 240, Height = 90,
                Source = "ai", Instruction = "替换", CreatedAt = createdAt,
            },
        };

        IReadOnlyList<Guid> ids = board.ReplaceStrokesWithAiObjectsAtomic(
            new[] { first, second }, texts, formulas, "AI 板书替换", "replace-group");
        bool replaceOk = ids.Count == 2 && board.Current.Document.Count == 0
            && board.CurrentTextObjects.Count == 1 && board.CurrentFormulaObjects.Count == 1
            && board.CurrentImages.Count == 0 && board.Current.History.UndoCount == 1;

        board.Current.Undo();
        bool undoOk = board.Current.Document.Count == 2
            && board.CurrentTextObjects.Count == 0 && board.CurrentFormulaObjects.Count == 0;

        board.Current.Redo();
        bool redoOk = board.Current.Document.Count == 0
            && board.CurrentTextObjects.Count == 1 && board.CurrentFormulaObjects.Count == 1;

        log.Add("replace strokes with native AI objects is one undo step",
            replaceOk && undoOk && redoOk,
            ("ids", ids.Count), ("strokesAfterReplace", replaceOk ? 0 : board.Current.Document.Count),
            ("texts", board.CurrentTextObjects.Count), ("formulas", board.CurrentFormulaObjects.Count),
            ("images", board.CurrentImages.Count),
            ("strokesAfterUndo", undoOk ? 2 : board.Current.Document.Count));
    }

    private static async Task CheckSaveAndReopenAsync(
        CheckLog log, BoardPlanNativeResult created, string directory)
    {
        var board = new BoardService();
        board.AddAiObjectsAtomic(created.Texts, created.Formulas, "AI 板书", "saved-group");
        string path = Path.Combine(directory, "ai-board.icink");
        await board.SaveBoardAsync(path);
        int version = ReadBoardVersion(path);

        var loaded = new BoardService();
        await loaded.LoadBoardAsync(path);
        var mismatches = new List<string>();
        if (loaded.CurrentTextObjects.Count != 3) mismatches.Add($"texts={loaded.CurrentTextObjects.Count}");
        if (loaded.CurrentFormulaObjects.Count != 1) mismatches.Add($"formulas={loaded.CurrentFormulaObjects.Count}");
        if (loaded.CurrentImages.Count != 0) mismatches.Add($"images={loaded.CurrentImages.Count}");

        BoardTextObject? savedWord = loaded.CurrentTextObjects.FirstOrDefault(text => text.Type == "wordList");
        if (savedWord is null || savedWord.Columns != 2) mismatches.Add("wordList.columns");
        BoardTextObject? savedPoem = loaded.CurrentTextObjects.FirstOrDefault(text => text.Type == "poem");
        if (savedPoem is null || savedPoem.Title != "静夜思" || !savedPoem.Author.Contains("李白"))
            mismatches.Add("poem.title/author");
        BoardFormulaObject? savedFormula = loaded.CurrentFormulaObjects.FirstOrDefault();
        if (savedFormula is null || !savedFormula.Latex.Contains("x^2")) mismatches.Add("formula.latex");
        if (loaded.CurrentTextObjects.Any(text => text.Source != "ai")) mismatches.Add("text.source");

        log.Add("native AI objects save/reopen with v4 and full fields",
            version == 4 && mismatches.Count == 0,
            ("version", version), ("texts", loaded.CurrentTextObjects.Count),
            ("formulas", loaded.CurrentFormulaObjects.Count), ("images", loaded.CurrentImages.Count),
            ("mismatches", string.Join("; ", mismatches)), ("path", "ai-board.icink"));
    }

    private static async Task CheckSideBoardAsync(CheckLog log, string directory)
    {
        var main = new BoardService();
        var side = new BoardService();
        main.SideBoardSource = side;

        var expectedText = new BoardTextObject
        {
            Type = "wordList",
            Content = "判别式\n配方法",
            Title = "子板词语",
            Author = "教师",
            FontFamily = "Microsoft YaHei UI",
            FontSize = 30,
            Color = "#FF112233",
            LineHeight = 1.7,
            Align = "center",
            Columns = 2,
            Left = 20,
            Top = 24,
            Width = 260,
            Height = 140,
            Source = "ai",
            Instruction = "子板原生对象",
            CreatedAt = FixedCreatedAt,
        };
        var expectedFormula = new BoardFormulaObject
        {
            Latex = "a^2+b^2=c^2",
            FontFamily = "Cambria Math",
            FontSize = 28,
            Color = "#FF112233",
            Left = 20,
            Top = 190,
            Width = 220,
            Height = 90,
            Source = "ai",
            Instruction = "子板原生对象",
            CreatedAt = FixedCreatedAt,
        };
        side.AddAiObjectsAtomic(new[] { expectedText }, new[] { expectedFormula }, "AI 板书", "side-group");

        string path = Path.Combine(directory, "ai-board-side.icink");
        await main.SaveBoardAsync(path);

        // 原生文本/公式内联在 board.json；只有图片/媒体才会写 Blob。
        bool hasSideAssets = side.CurrentImages.Count > 0 || side.CurrentMedia.Count > 0;
        bool sideBlob = HasSideBlob(path);
        string blobScope = !hasSideAssets ? "notApplicable" : sideBlob ? "side" : "missing";

        var loadedSide = new BoardService();
        var loadedMain = new BoardService { SideBoardSource = loadedSide };
        await loadedMain.LoadBoardAsync(path);

        var mismatches = new List<string>();
        if (loadedMain.CurrentImages.Count != 0)
            mismatches.Add($"mainImages={loadedMain.CurrentImages.Count}");
        if (loadedSide.CurrentTextObjects.Count != 1)
            mismatches.Add($"sideTexts={loadedSide.CurrentTextObjects.Count}");
        if (loadedSide.CurrentFormulaObjects.Count != 1)
            mismatches.Add($"sideFormulas={loadedSide.CurrentFormulaObjects.Count}");

        BoardTextObject? actualText = loadedSide.CurrentTextObjects.FirstOrDefault();
        if (actualText is null)
        {
            mismatches.Add("sideText.missing");
        }
        else
        {
            if (actualText.Type != expectedText.Type) mismatches.Add("text.type");
            if (actualText.Content != expectedText.Content) mismatches.Add("text.content");
            if (actualText.Title != expectedText.Title) mismatches.Add("text.title");
            if (actualText.Author != expectedText.Author) mismatches.Add("text.author");
            if (actualText.Columns != expectedText.Columns) mismatches.Add("text.columns");
            if (actualText.FontFamily != expectedText.FontFamily) mismatches.Add("text.fontFamily");
            if (Math.Abs(actualText.FontSize - expectedText.FontSize) > 0.01) mismatches.Add("text.fontSize");
            if (actualText.Color != expectedText.Color) mismatches.Add("text.color");
            if (Math.Abs(actualText.LineHeight - expectedText.LineHeight) > 0.01) mismatches.Add("text.lineHeight");
            if (actualText.Align != expectedText.Align) mismatches.Add("text.align");
            if (Math.Abs(actualText.Left - expectedText.Left) > 0.01) mismatches.Add("text.left");
            if (Math.Abs(actualText.Top - expectedText.Top) > 0.01) mismatches.Add("text.top");
            if (Math.Abs(actualText.Width - expectedText.Width) > 0.01) mismatches.Add("text.width");
            if (Math.Abs(actualText.Height - expectedText.Height) > 0.01) mismatches.Add("text.height");
            if (actualText.GroupId != "side-group") mismatches.Add("text.groupId");
            if (actualText.Source != expectedText.Source) mismatches.Add("text.source");
            if (actualText.Instruction != expectedText.Instruction) mismatches.Add("text.instruction");
            if (actualText.CreatedAt != expectedText.CreatedAt) mismatches.Add("text.createdAt");
        }

        BoardFormulaObject? actualFormula = loadedSide.CurrentFormulaObjects.FirstOrDefault();
        if (actualFormula is null)
        {
            mismatches.Add("sideFormula.missing");
        }
        else
        {
            if (actualFormula.Latex != expectedFormula.Latex) mismatches.Add("formula.latex");
            if (actualFormula.FontFamily != expectedFormula.FontFamily) mismatches.Add("formula.fontFamily");
            if (Math.Abs(actualFormula.FontSize - expectedFormula.FontSize) > 0.01) mismatches.Add("formula.fontSize");
            if (actualFormula.Color != expectedFormula.Color) mismatches.Add("formula.color");
            if (Math.Abs(actualFormula.Left - expectedFormula.Left) > 0.01) mismatches.Add("formula.left");
            if (Math.Abs(actualFormula.Top - expectedFormula.Top) > 0.01) mismatches.Add("formula.top");
            if (Math.Abs(actualFormula.Width - expectedFormula.Width) > 0.01) mismatches.Add("formula.width");
            if (Math.Abs(actualFormula.Height - expectedFormula.Height) > 0.01) mismatches.Add("formula.height");
            if (actualFormula.GroupId != "side-group") mismatches.Add("formula.groupId");
            if (actualFormula.Source != expectedFormula.Source) mismatches.Add("formula.source");
            if (actualFormula.Instruction != expectedFormula.Instruction) mismatches.Add("formula.instruction");
            if (actualFormula.CreatedAt != expectedFormula.CreatedAt) mismatches.Add("formula.createdAt");
        }

        bool scopeOk = !hasSideAssets || sideBlob;
        log.Add("side board native text/formula round-trip (blob scope only when assets exist)",
            mismatches.Count == 0 && scopeOk,
            ("mainImages", loadedMain.CurrentImages.Count),
            ("sideTexts", loadedSide.CurrentTextObjects.Count),
            ("sideFormulas", loadedSide.CurrentFormulaObjects.Count),
            ("textColumns", actualText?.Columns ?? 0),
            ("textTitle", actualText?.Title ?? ""),
            ("textAuthor", actualText?.Author ?? ""),
            ("formulaLatex", actualFormula?.Latex ?? ""),
            ("blobScope", blobScope),
            ("mismatches", string.Join("; ", mismatches)),
            ("path", "ai-board-side.icink"));
    }

    private static void CheckTimeMachine(CheckLog log, BoardPlanNativeResult created, string directory)
    {
        string sessionsDir = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(sessionsDir);

        var board = new BoardService();
        ClassroomSession session;
        using (var recorder = new InkRecorder(board) { SessionDirectory = sessionsDir })
        {
            recorder.Start("aiboardcheck 时光机");
            board.AddAiObjectsAtomic(
                created.Texts.Select(text => text.Copy(preserveId: false)).ToList(),
                created.Formulas.Select(formula => formula.Copy(preserveId: false)).ToList(),
                "AI 板书", "replay-group");
            recorder.Stop();
            session = recorder.Session;
        }

        List<TimelineEvent> aiEvents = session.Events
            .Where(item => item.Kind == TimelineEventKind.AiStep)
            .ToList();
        bool payloadOk = aiEvents.Count == 1
            && AiObjectPayloadCodec.TryDecode(aiEvents[0].Payload, out AiObjectsPayload payload)
            && payload.Objects.Count == 0
            && payload.NativeObjects.Count == 4
            && payload.NativeObjects.All(item => item.IsNative && !item.HasRenderPng);

        long time = session.Events.Count == 0 ? 0 : session.Events[^1].TimeMs;
        IReadOnlyList<TimelineAiObject> frame = new InkTimeline(session).BuildObjectFrame(time);
        bool frameOk = frame.Count == 4
            && frame.All(item => item.IsNative && !item.HasRenderPng)
            && frame.Any(item => item.Kind == "formula" && item.Latex.Contains("x^2"))
            && frame.Any(item => item.Kind == "wordList" && item.Columns == 2);

        var checkpointed = new InkTimeline(session, eventsPerCheckpoint: 1, checkpointIntervalMs: 1);
        IReadOnlyList<TimelineAiObject> checkpointFrame = checkpointed.BuildObjectFrame(time);
        bool checkpointOk = checkpointFrame.Count == 4
            && session.Checkpoints.Count > 0
            && session.Checkpoints.All(checkpoint => checkpoint.LiveObjectIds is not null);

        IReadOnlyList<TimelineAiObject> reloaded = new InkTimeline(session).BuildObjectFrame(time);
        string sessionPath = ClassroomSessionStore.Save(session, sessionsDir);
        ClassroomSession loadedSession = ClassroomSessionStore.Load(sessionPath);
        IReadOnlyList<TimelineAiObject> loadedFrame = new InkTimeline(loadedSession).BuildObjectFrame(time);

        log.Add("time machine replays structured native AI objects (no PNG/model)",
            payloadOk && frameOk && checkpointOk
                && reloaded.Count == 4 && loadedFrame.Count == 4
                && loadedFrame.All(item => item.IsNative && !item.HasRenderPng),
            ("aiEvents", aiEvents.Count),
            ("nativeObjects", payloadOk ? 4 : 0),
            ("pngObjects", aiEvents.Count == 1
                && AiObjectPayloadCodec.TryDecode(aiEvents[0].Payload, out AiObjectsPayload probe)
                ? probe.Objects.Count : -1),
            ("replayObjects", frame.Count),
            ("checkpointObjects", checkpointFrame.Count),
            ("reloadedObjects", loadedFrame.Count),
            ("session", "sessions/" + Path.GetFileName(sessionPath)));
    }

    private static void CheckRouting(CheckLog log, ShellController shell, AppServices services)
    {
        FieldInfo? targetField = typeof(ShellController).GetField(
            "_aiTarget", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo? boardMethod = typeof(ShellController).GetMethod(
            "AiTargetBoard", BindingFlags.NonPublic | BindingFlags.Instance);
        if (targetField is null || boardMethod is null)
        {
            log.Add("shell AI target routing is reachable", false,
                ("field", targetField is not null), ("method", boardMethod is not null));
            return;
        }

        object? original = targetField.GetValue(shell);
        bool mapped = true;
        var mapping = new List<string>();
        try
        {
            foreach (AssistantCanvasKind kind in new[]
                     {
                         AssistantCanvasKind.MainBoard,
                         AssistantCanvasKind.SideBoard,
                         AssistantCanvasKind.Overlay,
                     })
            {
                targetField.SetValue(shell, new AssistantTarget { Kind = kind });
                var resolved = boardMethod.Invoke(shell, null) as BoardService;
                BoardService expected = kind == AssistantCanvasKind.SideBoard
                    ? services.SideBoard : services.Board;
                bool ok = ReferenceEquals(resolved, expected);
                mapped &= ok;
                mapping.Add($"{kind}->{(resolved is null ? "null" : ReferenceEquals(resolved, services.SideBoard) ? "side" : "main")}");
            }
        }
        finally
        {
            targetField.SetValue(shell, original);
        }

        object sidePage = services.SideBoard.Current;
        var requested = new AssistantTarget
        {
            Canvas = services.SideBoard, Page = sidePage, Kind = AssistantCanvasKind.SideBoard,
            PageIndex = services.SideBoard.CurrentIndex, SessionId = "aiboardcheck-route",
        };
        var same = new AssistantTarget
        {
            Canvas = services.SideBoard, Page = sidePage, Kind = AssistantCanvasKind.SideBoard,
            PageIndex = services.SideBoard.CurrentIndex, SessionId = "aiboardcheck-route",
        };
        var otherPage = new AssistantTarget
        {
            Canvas = services.SideBoard, Page = new object(), Kind = AssistantCanvasKind.SideBoard,
            PageIndex = services.SideBoard.CurrentIndex + 1, SessionId = "aiboardcheck-route",
        };
        var otherBoard = new AssistantTarget
        {
            Canvas = services.Board, Page = services.Board.Current, Kind = AssistantCanvasKind.MainBoard,
            PageIndex = services.SideBoard.CurrentIndex, SessionId = "aiboardcheck-route",
        };
        bool policyOk = AssistantTargetPolicy.CanWriteBack(requested, same)
            && !AssistantTargetPolicy.CanWriteBack(requested, otherPage)
            && !AssistantTargetPolicy.CanWriteBack(requested, otherBoard);

        log.Add("shell routes AI board writes to the right board", mapped && policyOk,
            ("mapping", string.Join(",", mapping)),
            ("samePageWriteBack", AssistantTargetPolicy.CanWriteBack(requested, same)),
            ("otherPageWriteBack", AssistantTargetPolicy.CanWriteBack(requested, otherPage)),
            ("otherBoardWriteBack", AssistantTargetPolicy.CanWriteBack(requested, otherBoard)));
    }

    private static InkStroke SyntheticStroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 2, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 40, y + 20);
        stroke.Complete();
        return stroke;
    }

    private static int ReadBoardVersion(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry? entry = archive.GetEntry("board.pretty.json");
        if (entry is null) return -1;
        using var reader = new StreamReader(entry.Open());
        JsonNode? node = JsonNode.Parse(reader.ReadToEnd());
        return node?["Version"]?.GetValue<int>() ?? -1;
    }

    private static bool HasSideBlob(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry? entry = archive.GetEntry("board.pretty.json");
        if (entry is null) return false;
        using var reader = new StreamReader(entry.Open());
        JsonNode? node = JsonNode.Parse(reader.ReadToEnd());
        if (node?["Blobs"] is not JsonArray blobs) return false;

        foreach (JsonNode? blob in blobs)
        {
            if (blob is not JsonObject item) continue;
            if (item["Scope"] is JsonValue scope
                && scope.TryGetValue<string>(out string value)
                && string.Equals(value, "side", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void WriteReport(string textPath, string jsonPath, CheckLog log, JsonSerializerOptions options)
    {
        var builder = new StringBuilder();
        foreach (var item in log.Items)
        {
            builder.Append(item.Passed ? "[PASS] " : "[FAIL] ");
            if (item.Optional) builder.Append("(optional) ");
            builder.Append(item.Check)
                .Append(" (").Append(item.ElapsedMs.ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms)");
            foreach (var pair in item.Details)
                builder.Append(' ').Append(pair.Key).Append('=').Append(FormatValue(pair.Value));
            builder.AppendLine();
        }

        int passed = log.Items.Count(item => item.Passed);
        int failed = log.Items.Count(item => !item.Passed && !item.Optional);
        int optional = log.Items.Count(item => item.Optional);
        double totalElapsed = log.Items.Sum(item => item.ElapsedMs);
        builder.AppendLine();
        builder.AppendLine($"summary: {passed}/{log.Items.Count} passed, {failed} failed, {optional} optional, "
            + $"total={totalElapsed.ToString("0.0", CultureInfo.InvariantCulture)} ms");
        builder.AppendLine("note: no AI HTTP request is issued; objects are native text/formula (no PNG cards).");
        File.WriteAllText(textPath, builder.ToString());

        var document = new
        {
            generatedAt = DateTimeOffset.Now,
            passed = log.Passed,
            totalElapsedMs = Math.Round(totalElapsed, 1),
            networkUsed = false,
            nativeObjectsOnly = true,
            checks = log.Items,
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(document, options));
    }

    private static string FormatValue(object value) => value switch
    {
        null => "null",
        string text => text.ReplaceLineEndings(" "),
        bool flag => flag ? "true" : "false",
        IReadOnlyList<string> list => string.Join(" | ", list),
        IReadOnlyList<int> list => string.Join(" | ", list),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}