using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Features.Classroom;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.App.Diagnostics;

/// <summary>
/// AI 板书运行时诊断（<c>--aiboardcheck=&lt;dir&gt;</c>）：在真实 App、真实 UI 线程与真实
/// <see cref="BoardService"/> 中，用本地构造的 <see cref="AssistantBoardPlan"/> 渲染四类卡片，
/// 覆盖原子写入/撤销、替换原板书、保存重开、子板 Scope、时光机对象回放与主/子板路由。
/// 不发起任何 AI HTTP 请求，AI 卡片全部由本地渲染器生成。
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

        BoardWindow boardWindow = null;
        Window formulaWindow = null;
        FrameworkElement formulaHost = null;

        try
        {
            shell.Start();
            shell.StartInClickThrough();
            await Task.Delay(350);

            shell.ToggleBoard();
            await Task.Delay(450);
            boardWindow = shell.OpenWindows.OfType<BoardWindow>().SingleOrDefault();
            log.Add("main board window opens for formula host", boardWindow is not null,
                ("openWindows", shell.OpenWindows.Count()));

            formulaHost = boardWindow?.Content as FrameworkElement;
            if (formulaHost is null)
            {
                // 主展板窗口不可用时仍尝试独立宿主，保证公式路径有 XamlRoot；
                // 若宿主仍不可用，BoardPlanWriter 会回退文本并在元数据记录原因。
                formulaWindow = new Window
                {
                    Title = "AI 板书诊断公式宿主",
                    Content = new Grid { Width = 800, Height = 600 },
                };
                formulaWindow.Activate();
                await Task.Delay(250);
                formulaHost = formulaWindow.Content as FrameworkElement;
            }

            log.Add("formula host available", formulaHost is not null,
                ("hostType", formulaHost?.GetType().Name ?? "null"),
                ("xamlRoot", formulaHost?.XamlRoot is not null));

            AssistantBoardPlan plan = BuildPlan();
            BoardFormulaRenderResult formulaProbe = await BoardFormulaRenderer.RenderPngAsync(
                plan.Blocks[0].Latex, 560, 2.0, formulaHost);
            log.AddOptional("formula card rendered by BoardFormulaRenderer", formulaProbe.Success,
                ("error", formulaProbe.Error), ("warnings", formulaProbe.Warnings),
                ("width", Math.Round(formulaProbe.Width, 1)), ("height", Math.Round(formulaProbe.Height, 1)),
                ("pngBytes", formulaProbe.Png.Length));

            const string instruction = "aiboardcheck: 整理当前页板书";
            BoardPlanRenderResult rendered = await BoardPlanWriter.RenderAsync(
                plan, instruction, FixedCreatedAt, formulaHost);

            log.Add("board plan writer renders four cards",
                rendered.Success && rendered.Cards.Count == 4,
                ("cards", rendered.Cards.Count), ("error", rendered.Error));
            if (!rendered.Success || rendered.Cards.Count != 4)
            {
                log.Add("board plan cards are valid", false,
                    ("expected", 4), ("actual", rendered.Cards.Count), ("error", rendered.Error));
                return;
            }

            var metas = rendered.Cards.Select(card => AiPasteMetadata.TryParse(card.Metadata)).ToList();
            bool metadataValid = metas[0] is { } formulaMeta
                && metas[1] is { } textMeta
                && metas[2] is { } wordMeta
                && metas[3] is { } poemMeta
                && formulaMeta.Kind == AiPasteKind.BoardPlan && formulaMeta.BlockKind == "formula"
                && formulaMeta.Latex == plan.Blocks[0].Latex
                && textMeta.BlockKind == "text" && textMeta.Title == plan.Blocks[1].Title
                && wordMeta.BlockKind == "wordlist" && wordMeta.Lines.SequenceEqual(plan.Blocks[2].Lines)
                && poemMeta.BlockKind == "poem" && poemMeta.Title == plan.Blocks[3].Title
                && poemMeta.Author == plan.Blocks[3].Author
                && poemMeta.Lines.SequenceEqual(plan.Blocks[3].Lines);
            bool formulaFallbackRecorded = metas[0]?.Notes.Any(note =>
                note.Contains("回退", StringComparison.OrdinalIgnoreCase)) == true;
            log.Add("board plan metadata keeps kind/blockKind/latex/lines",
                metadataValid,
                ("blockKinds", string.Join(",", metas.Select(meta => meta?.BlockKind ?? "null"))),
                ("formulaLatex", metas[0]?.Latex ?? ""),
                ("poemLines", metas[3]?.Lines.Count ?? 0),
                ("poemAuthor", metas[3]?.Author ?? ""));
            log.AddOptional("formula fallback is recorded when renderer fails",
                formulaProbe.Success || formulaFallbackRecorded,
                ("probeSuccess", formulaProbe.Success), ("fallbackNote", formulaFallbackRecorded),
                ("notes", string.Join(" | ", metas[0]?.Notes ?? new List<string>())));

            List<BoardImageAsset> cards = ToAssets(rendered.Cards);
            IReadOnlyList<int> indexes = services.Board.AddImagesAtomic(cards, "AI 板书");
            log.Add("atomic add inserts four AI cards into main board",
                services.Board.CurrentImages.Count == 4 && indexes.Count == 4,
                ("images", services.Board.CurrentImages.Count), ("indexes", indexes),
                ("undoCount", services.Board.Current.History.UndoCount));

            services.Board.Current.Undo();
            bool undoOk = services.Board.CurrentImages.Count == 0;
            services.Board.Current.Redo();
            bool redoOk = services.Board.CurrentImages.Count == 4;
            log.Add("one undo removes all four cards and redo restores them", undoOk && redoOk,
                ("imagesAfterUndo", undoOk ? 0 : services.Board.CurrentImages.Count),
                ("imagesAfterRedo", services.Board.CurrentImages.Count),
                ("undoCount", services.Board.Current.History.UndoCount),
                ("redoCount", services.Board.Current.History.RedoCount));

            CheckReplace(log);
            await CheckSaveAndReopenAsync(log, cards, directory);
            await CheckSideBoardAsync(log, cards[0], directory);
            CheckTimeMachine(log, cards, directory);
            CheckRouting(log, shell, services);

            log.Add("diagnostic completed without exception", true);
        }
        catch (Exception ex)
        {
            log.Add("aiboardcheck exception", false, ("error", ex.ToString()));
        }
        finally
        {
            try { formulaWindow?.Close(); } catch { /* 清理不影响结果 */ }
            foreach (var window in shell.OpenWindows.OfType<BoardWindow>().ToArray())
            {
                try { window.Close(); } catch { /* 清理不影响结果 */ }
            }

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
        Intent = "write",
        TargetBoard = "main",
        TargetPageIndex = 0,
        Notes = new[] { "aiboardcheck plan note" },
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
                Style = new AssistantBoardStyle { FontSize = 24, Align = "left", Columns = 1, LineHeight = 1.5 },
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

    private static List<BoardImageAsset> ToAssets(IReadOnlyList<BoardPlanCardDraft> cards)
    {
        var assets = new List<BoardImageAsset>(cards.Count);
        for (int index = 0; index < cards.Count; index++)
        {
            BoardPlanCardDraft card = cards[index];
            assets.Add(new BoardImageAsset
            {
                Name = card.Name,
                Data = card.Png,
                ContentType = "image/png",
                Left = 24 + index * 32,
                Top = 24 + index * 24,
                Width = (float)card.Width,
                Height = (float)card.Height,
                Metadata = card.Metadata,
            });
        }

        return assets;
    }

    private static void CheckReplace(CheckLog log)
    {
        var board = new BoardService();
        InkStroke first = SyntheticStroke(120, 120);
        InkStroke second = SyntheticStroke(220, 180);
        board.Current.Document.Add(first);
        board.Current.Document.Add(second);
        int strokesBefore = board.Current.Document.Count;

        var cards = new[]
        {
            Card("AI 板书·公式", 1),
            Card("AI 板书·文本", 2),
        };

        bool replaced = board.ReplaceStrokesWithImagesAtomic(new[] { first, second }, cards, "AI 板书替换");
        bool replaceOk = replaced && board.Current.Document.Count == 0 && board.CurrentImages.Count == 2;

        board.Current.Undo();
        bool undoOk = board.Current.Document.Count == strokesBefore && board.CurrentImages.Count == 0;

        board.Current.Redo();
        bool redoOk = board.Current.Document.Count == 0 && board.CurrentImages.Count == 2;

        log.Add("replace strokes with two AI cards is atomic", replaceOk && undoOk && redoOk,
            ("strokesBefore", strokesBefore), ("replaced", replaced),
            ("strokesAfterReplace", replaceOk ? 0 : board.Current.Document.Count),
            ("imagesAfterReplace", 2),
            ("strokesAfterUndo", undoOk ? strokesBefore : board.Current.Document.Count),
            ("imagesAfterUndo", undoOk ? 0 : board.CurrentImages.Count),
            ("strokesAfterRedo", board.Current.Document.Count),
            ("imagesAfterRedo", board.CurrentImages.Count));
    }

    private static async Task CheckSaveAndReopenAsync(
        CheckLog log, IReadOnlyList<BoardImageAsset> source, string directory)
    {
        var board = new BoardService();
        var copies = source.Select((asset, index) =>
        {
            BoardImageAsset copy = asset.Copy(preserveId: false);
            copy.Left = 30 + index * 20;
            copy.Top = 30 + index * 20;
            return copy;
        }).ToList();
        board.AddImagesAtomic(copies, "AI 板书");

        string path = Path.Combine(directory, "ai-board.icink");
        await board.SaveBoardAsync(path);
        int version = ReadBoardVersion(path);

        var loaded = new BoardService();
        await loaded.LoadBoardAsync(path);

        var mismatches = new List<string>();
        if (loaded.CurrentImages.Count != copies.Count)
            mismatches.Add($"count expected={copies.Count} actual={loaded.CurrentImages.Count}");
        for (int index = 0; index < Math.Min(copies.Count, loaded.CurrentImages.Count); index++)
        {
            BoardImageAsset expected = copies[index];
            BoardImageAsset actual = loaded.CurrentImages[index];
            if (!expected.Data.SequenceEqual(actual.Data))
                mismatches.Add($"card{index}.data differs");
            AiPasteMetadata expectedMeta = AiPasteMetadata.TryParse(expected.Metadata);
            AiPasteMetadata actualMeta = AiPasteMetadata.TryParse(actual.Metadata);
            mismatches.AddRange(CompareMetadata($"card{index}", expectedMeta, actualMeta));
        }

        log.Add("board save/reopen preserves four AI cards and full metadata",
            version == 4 && loaded.CurrentImages.Count == 4 && mismatches.Count == 0,
            ("version", version), ("images", loaded.CurrentImages.Count),
            ("mismatches", string.Join("; ", mismatches)),
            ("path", "ai-board.icink"));
        log.Add("board file keeps version 4", version == 4, ("version", version));
    }

    private static async Task CheckSideBoardAsync(CheckLog log, BoardImageAsset source, string directory)
    {
        var main = new BoardService();
        var side = new BoardService();
        main.SideBoardSource = side;

        BoardImageAsset sideCard = source.Copy(preserveId: false);
        sideCard.Left = 48;
        sideCard.Top = 64;
        side.AddImagesAtomic(new[] { sideCard }, "AI 板书");

        string path = Path.Combine(directory, "ai-board-side.icink");
        await main.SaveBoardAsync(path);
        bool sideScope = HasSideBlob(path);

        var loadedSide = new BoardService();
        var loadedMain = new BoardService { SideBoardSource = loadedSide };
        await loadedMain.LoadBoardAsync(path);

        bool sideOk = loadedMain.CurrentImages.Count == 0 && loadedSide.CurrentImages.Count == 1;
        var mismatches = new List<string>();
        if (loadedSide.CurrentImages.Count == 1)
        {
            BoardImageAsset actual = loadedSide.CurrentImages[0];
            if (!sideCard.Data.SequenceEqual(actual.Data)) mismatches.Add("side.data differs");
            mismatches.AddRange(CompareMetadata(
                "side", AiPasteMetadata.TryParse(sideCard.Metadata), AiPasteMetadata.TryParse(actual.Metadata)));
        }

        log.Add("side board AI card round-trips with Blob Scope=side",
            sideOk && sideScope && mismatches.Count == 0,
            ("mainImages", loadedMain.CurrentImages.Count),
            ("sideImages", loadedSide.CurrentImages.Count),
            ("scopeSide", sideScope),
            ("mismatches", string.Join("; ", mismatches)),
            ("path", "ai-board-side.icink"));
    }

    private static void CheckTimeMachine(CheckLog log, IReadOnlyList<BoardImageAsset> source, string directory)
    {
        string sessionsDir = Path.Combine(directory, "sessions");
        Directory.CreateDirectory(sessionsDir);

        var board = new BoardService();
        ClassroomSession session;
        using (var recorder = new InkRecorder(board) { SessionDirectory = sessionsDir })
        {
            recorder.Start("aiboardcheck 时光机");
            board.AddImagesAtomic(source.Select(asset =>
            {
                BoardImageAsset copy = asset.Copy(preserveId: false);
                return copy;
            }).ToList(), "AI 板书");
            recorder.Stop();
            session = recorder.Session;
        }

        List<TimelineEvent> aiEvents = session.Events
            .Where(item => item.Kind == TimelineEventKind.AiStep)
            .ToList();
        long time = session.Events.Count == 0 ? 0 : session.Events[^1].TimeMs;
        var timeline = new InkTimeline(session);
        IReadOnlyList<TimelineAiObject> frame = timeline.BuildObjectFrame(time);
        string[] expectedPng = source.Select(asset => Convert.ToBase64String(asset.Data)).ToArray();
        bool signed = aiEvents.Count == 1 && aiEvents[0].BoardId == 0;

        log.Add("time machine records AI objects and replays stored PNG",
            signed && frame.Count == source.Count
                && frame.Select(item => item.RenderPngBase64).SequenceEqual(expectedPng),
            ("aiEvents", aiEvents.Count), ("boardId", aiEvents.Count == 0 ? -1 : aiEvents[0].BoardId),
            ("objects", frame.Count), ("expectedObjects", source.Count),
            ("pngMatch", frame.Count == expectedPng.Length
                && frame.Select(item => item.RenderPngBase64).SequenceEqual(expectedPng)));

        var checkpointed = new InkTimeline(session, eventsPerCheckpoint: 1, checkpointIntervalMs: 1);
        IReadOnlyList<TimelineAiObject> checkpointFrame = checkpointed.BuildObjectFrame(time);
        bool checkpointOk = session.Checkpoints.Count > 0
            && session.Checkpoints.All(checkpoint => checkpoint.LiveObjectIds is not null)
            && checkpointFrame.Select(item => item.RenderPngBase64).SequenceEqual(expectedPng);

        IReadOnlyList<TimelineAiObject> reloadedFrame = new InkTimeline(session).BuildObjectFrame(time);
        bool reloadOk = reloadedFrame.Select(item => item.RenderPngBase64).SequenceEqual(expectedPng);

        log.Add("checkpoint increment and reload keep the same replay objects",
            checkpointOk && reloadOk,
            ("checkpoints", session.Checkpoints.Count),
            ("lastLiveObjects", session.Checkpoints.Count == 0 ? 0 : session.Checkpoints[^1].LiveObjectIds.Count),
            ("checkpointObjects", checkpointFrame.Count), ("reloadedObjects", reloadedFrame.Count));

        string sessionPath = ClassroomSessionStore.Save(session, sessionsDir);
        ClassroomSession loadedSession = ClassroomSessionStore.Load(sessionPath);
        IReadOnlyList<TimelineAiObject> savedFrame = new InkTimeline(loadedSession).BuildObjectFrame(time);
        log.Add("saved session reload keeps AI replay objects",
            savedFrame.Count == source.Count
                && savedFrame.Select(item => item.RenderPngBase64).SequenceEqual(expectedPng),
            ("session", "sessions/" + Path.GetFileName(sessionPath)),
            ("objects", savedFrame.Count),
            ("pngMatch", savedFrame.Select(item => item.RenderPngBase64).SequenceEqual(expectedPng)));

        string[] references = typeof(InkTimeline).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? "")
            .ToArray();
        bool noNetwork = !references.Contains("InkCanvas.Platform")
            && !references.Contains("System.Net.Http")
            && typeof(InkTimeline).Assembly.GetType("InkCanvas.Platform.Ai.AiClient") is null;
        log.Add("replay path has no network/AI-client dependency", noNetwork,
            ("references", string.Join(",", references)),
            ("platformReferenced", references.Contains("InkCanvas.Platform")),
            ("httpReferenced", references.Contains("System.Net.Http")));
    }

    private static void CheckRouting(CheckLog log, ShellController shell, AppServices services)
    {
        FieldInfo targetField = typeof(ShellController).GetField(
            "_aiTarget", BindingFlags.NonPublic | BindingFlags.Instance);
        MethodInfo boardMethod = typeof(ShellController).GetMethod(
            "AiTargetBoard", BindingFlags.NonPublic | BindingFlags.Instance);
        if (targetField is null || boardMethod is null)
        {
            log.Add("shell AI target routing is reachable", false,
                ("field", targetField is not null), ("method", boardMethod is not null));
            return;
        }

        object original = targetField.GetValue(shell);
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
                    ? services.SideBoard
                    : services.Board;
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
            Canvas = services.SideBoard,
            Page = sidePage,
            Kind = AssistantCanvasKind.SideBoard,
            PageIndex = services.SideBoard.CurrentIndex,
            SessionId = "aiboardcheck-route",
        };
        var same = new AssistantTarget
        {
            Canvas = services.SideBoard,
            Page = sidePage,
            Kind = AssistantCanvasKind.SideBoard,
            PageIndex = services.SideBoard.CurrentIndex,
            SessionId = "aiboardcheck-route",
        };
        var otherPage = new AssistantTarget
        {
            Canvas = services.SideBoard,
            Page = new object(),
            Kind = AssistantCanvasKind.SideBoard,
            PageIndex = services.SideBoard.CurrentIndex + 1,
            SessionId = "aiboardcheck-route",
        };
        var otherBoard = new AssistantTarget
        {
            Canvas = services.Board,
            Page = services.Board.Current,
            Kind = AssistantCanvasKind.MainBoard,
            PageIndex = services.SideBoard.CurrentIndex,
            SessionId = "aiboardcheck-route",
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

    private static BoardImageAsset Card(string name, byte fill) => new()
    {
        Name = name,
        Data = new byte[] { fill, 2, 3, 4 },
        ContentType = "image/png",
        Width = 220,
        Height = 110,
        Left = 40,
        Top = 40,
    };

    private static InkStroke SyntheticStroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 2, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 40, y + 20);
        stroke.Complete();
        return stroke;
    }

    private static IEnumerable<string> CompareMetadata(
        string label, AiPasteMetadata expected, AiPasteMetadata actual)
    {
        if (actual is null)
        {
            yield return label + ".metadata missing";
            yield break;
        }

        if (expected is null)
        {
            yield return label + ".expected metadata missing";
            yield break;
        }

        if (expected.Kind != actual.Kind) yield return $"{label}.kind {expected.Kind}!={actual.Kind}";
        if (expected.BlockKind != actual.BlockKind) yield return $"{label}.blockKind {expected.BlockKind}!={actual.BlockKind}";
        if (expected.Schema != actual.Schema) yield return $"{label}.schema {expected.Schema}!={actual.Schema}";
        if (expected.Latex != actual.Latex) yield return $"{label}.latex differs";
        if (expected.Text != actual.Text) yield return $"{label}.text differs";
        if (!expected.Lines.SequenceEqual(actual.Lines)) yield return $"{label}.lines differ";
        if (expected.Title != actual.Title) yield return $"{label}.title differs";
        if (expected.Author != actual.Author) yield return $"{label}.author differs";
        if (expected.StyleJson != actual.StyleJson) yield return $"{label}.styleJson differs";
        if (expected.Instruction != actual.Instruction) yield return $"{label}.instruction differs";
        if (expected.CreatedAt != actual.CreatedAt) yield return $"{label}.createdAt differs";
        if (!expected.Notes.SequenceEqual(actual.Notes)) yield return $"{label}.notes differ";
    }

    private static int ReadBoardVersion(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = archive.GetEntry("board.pretty.json");
        if (entry is null) return -1;
        using var reader = new StreamReader(entry.Open());
        JsonNode node = JsonNode.Parse(reader.ReadToEnd());
        return node?["Version"]?.GetValue<int>() ?? -1;
    }

    private static bool HasSideBlob(string path)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = archive.GetEntry("board.pretty.json");
        if (entry is null) return false;
        using var reader = new StreamReader(entry.Open());
        JsonNode node = JsonNode.Parse(reader.ReadToEnd());
        if (node?["Blobs"] is not JsonArray blobs) return false;

        foreach (JsonNode blob in blobs)
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
        builder.AppendLine("note: no AI HTTP request is issued; cards are rendered locally from a constructed BoardPlan.");
        File.WriteAllText(textPath, builder.ToString());

        var document = new
        {
            generatedAt = DateTimeOffset.Now,
            passed = log.Passed,
            totalElapsedMs = Math.Round(totalElapsed, 1),
            networkUsed = false,
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