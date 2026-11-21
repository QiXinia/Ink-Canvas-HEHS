using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Classroom;
using InkCanvas.Features.Ppt;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 需求 3.1「完整课堂链路」单一自动化回归用例：
/// 打开课件 → 进入放映 → 翻页批注 → AI 框选讲题 → 贴回白板 → 新建练习页
/// → 启动倒计时 → 保存快照 → 时光机回放 → 导出课堂成果。
/// <para>
/// 全流程纯逻辑执行：不启动 UI、不触发真实 COM / WPS / AI 网络、不写真实 PPT。
/// <see cref="InkRecorder"/> 依赖 <c>BoardService</c>（UI），因此本用例用
/// <see cref="InkTimeline"/> + 测试内部的最小事件日志适配器代替其「归属与序号分配」行为。
/// </para>
/// </summary>
public sealed class ClassroomChainRegressionTests
{
    // 链路时间轴（毫秒，相对录制开始），时间严格递增以保证回放定位稳定。
    private const long T_Page0 = 0;
    private const long T_PptSlide1 = 20;
    private const long T_StrokePage0 = 100;
    private const long T_PptSlide2 = 200;
    private const long T_Page1 = 210;
    private const long T_StrokePage1 = 250;
    private const long T_AiStep = 300;
    private const long T_PasteBack = 400;
    private const long T_PracticePage = 500;
    private const long T_TimerStarted = 510;
    private const long T_PracticeInk = 530;
    private const long T_TimerEnded = 600;

    private const int SlideIdOne = 101;
    private const int SlideIdTwo = 102;

    private readonly ITestOutputHelper _output;

    public ClassroomChainRegressionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FullClassroomChain_FromSlideshowToExport_IsDeterministic()
    {
        // ---------- 构造会话与统一事件日志（替代 InkRecorder 的 UI 依赖） ----------
        var session = new ClassroomSession
        {
            Id = "chain-session-" + Guid.NewGuid().ToString("N"),
            Name = "完整课堂链路回归",
            CreatedAt = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
            Events = new List<TimelineEvent>(),
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
        };
        var log = new ChainEventLog(session);

        InkStroke strokePage0 = NewStroke(10f);
        InkStroke strokePage1 = NewStroke(30f);
        InkStroke strokePaste = NewStroke(50f);
        InkStroke strokePractice = NewStroke(70f);

        // ---------- 1. 打开课件并进入放映 ----------
        log.Append(PageChanged(T_Page0, 0));
        log.Append(BackgroundChanged(T_Page0, 0, "Black"));
        TimelineEvent pptFirst = log.Append(PptSlideChanged(T_PptSlide1, 0, SlideIdOne, 1));
        TimelineEvent inkPage0 = log.Append(StrokeAdded(T_StrokePage0, 0, strokePage0));

        var timeline = new InkTimeline(session);

        Assert.Equal(0, timeline.PageAt(T_PptSlide1));
        Assert.Equal(1, timeline.SceneAt(T_PptSlide1).PptSlideIndex);
        Assert.Equal(SlideIdOne, pptFirst.PptSlideId);
        Assert.Equal(SlideIdOne, InkTimeline.DecodePptSlideChanged(pptFirst.Payload).SlideId);
        Assert.True(pptFirst.SessionId == session.Id,
            "课件事件必须归属当前课堂会话，否则回放会串会话。");

        // 翻到课件第 2 页（放映翻页），再做两页批注。
        TimelineEvent pptSecond = log.Append(PptSlideChanged(T_PptSlide2, 0, SlideIdTwo, 2));
        log.Append(PageChanged(T_Page1, 1));
        TimelineEvent inkPage1 = log.Append(StrokeAdded(T_StrokePage1, 1, strokePage1));

        Assert.Equal(0, pptSecond.PageIndex);
        Assert.Equal(SlideIdTwo, pptSecond.PptSlideId);
        Assert.Equal(2, timeline.SceneAt(T_PptSlide2).PptSlideIndex);
        Assert.Equal(1, timeline.SceneAt(T_StrokePage1).PageIndex);

        // ---------- 2. 两页各 1 笔：SessionId / Seq 递增，且不串页 ----------
        Assert.True(inkPage0.PageIndex == 0 && inkPage1.PageIndex == 1,
            "第 2 步：两页批注必须分别落在第 1 页与第 2 页，不能串页。");
        Assert.Equal(strokePage0.Id.ToString(), inkPage0.StrokeId);
        Assert.Equal(strokePage1.Id.ToString(), inkPage1.StrokeId);

        Assert.All(session.Events, e => Assert.True(e.SessionId == session.Id,
            "第 2 步：所有事件 SessionId 必须等于课堂会话 Id。"));
        Assert.True(IsSeqStrictlyIncreasing(session.Events),
            "第 2 步：事件序号 Seq 必须随写入顺序严格递增。");

        Assert.Equal(new[] { StrokeSignature(strokePage0) }, FrameSignatures(timeline.BuildFrame(T_StrokePage0)));
        Assert.Equal(new[] { StrokeSignature(strokePage1) }, FrameSignatures(timeline.BuildFrame(T_StrokePage1)));
        _output.WriteLine($"步骤 1-2：课件页 {SlideIdOne}/{SlideIdTwo} 归属正确，两页各 1 笔且不串页。");

        // ---------- 3. AI 框选讲题（至少 2 个选区 + 分步模式裁剪） ----------
        var problem = new ProblemContext { SlideId = SlideIdTwo, SlideIndex = 2 };
        ProblemContextPolicy.Add(problem, new ProblemRegion
        {
            ImageBase64 = "region-题干",
            Anchor = new AssistantRect(10, 10, 110, 90),
            Source = "selection",
        }, id: "r-1", nowMs: T_AiStep);
        ProblemContextPolicy.Add(problem, new ProblemRegion
        {
            ImageBase64 = "region-图形",
            Anchor = new AssistantRect(80, 40, 180, 140),
            Source = "selection",
        }, id: "r-2", nowMs: T_AiStep);

        Assert.True(problem.Count == 2, "第 3 步：AI 讲题至少需要 2 个选区（题干 + 图形）。");
        Assert.Equal(new[] { "r-1", "r-2" }, ProblemContextPolicy.Ordered(problem).Select(r => r.Id).ToArray());
        Assert.True(problem.SessionId.Length > 0, "第 3 步：拼题上下文必须带独立 SessionId 供回放关联。");

        string[] steps = { "第一步：读出已知条件", "第二步：变形求解", "结论：x = 2" };
        AssistantTeachingMode mode = AssistantTeachingMode.StepByStep;
        int visible = AssistantTeachingPolicy.VisibleSteps(mode, revealed: 1, total: steps.Length);
        string revealedText = string.Join("\n", steps.Take(visible));

        Assert.Equal(1, visible);
        Assert.True(!revealedText.Contains("结论"),
            "第 3 步：分步模式下未揭示的结论不得提前出现在讲题结果中。");
        Assert.False(AssistantTeachingPolicy.ShowsFinalAnswer(mode, 1, steps.Length));
        Assert.True(AssistantTeachingPolicy.HasMoreSteps(mode, 1, steps.Length));
        Assert.Equal("step", AssistantTeachingPolicy.Wire(mode));

        // 全部揭示后才允许出现结论。
        Assert.True(AssistantTeachingPolicy.ShowsFinalAnswer(mode, steps.Length, steps.Length));
        Assert.True(string.Join("\n", steps.Take(
                AssistantTeachingPolicy.VisibleSteps(mode, steps.Length, steps.Length))).Contains("结论"),
            "第 3 步：全部步骤展开后结论才允许出现。");

        log.Append(new TimelineEvent
        {
            TimeMs = T_AiStep,
            PageIndex = 1,
            PptSlideId = SlideIdTwo,
            Kind = TimelineEventKind.AiStep,
            Payload = "problem-context:" + problem.Count,
        });
        _output.WriteLine("步骤 3：2 选区拼题 + 分步裁剪正确，未揭示结论未泄露。");

        // ---------- 4. 过期响应：旧世代结果必须被丢弃 ----------
        var gate = new AiResponseGate();
        int staleGeneration = gate.Begin("ai-request-old");
        int currentGeneration = gate.Begin("ai-request-new");
        var answers = new List<string>();

        // 旧请求的迟到响应先到达。
        if (gate.IsCurrent(staleGeneration, "ai-request-old")) answers.Add("旧题答案");
        // 当前请求的响应随后到达。
        if (gate.IsCurrent(currentGeneration, "ai-request-new")) answers.Add("新题答案");

        Assert.Equal(new[] { "新题答案" }, answers);
        Assert.False(gate.IsCurrent(staleGeneration), "第 4 步：旧世代响应必须被丢弃。");
        Assert.True(gate.IsCurrent(currentGeneration, "ai-request-new"));
        Assert.True(!answers.Contains("旧题答案"), "第 4 步：旧题答案不得写入新题目。");
        _output.WriteLine("步骤 4：AiResponseGate 丢弃旧世代响应，新题只收当前世代结果。");

        // ---------- 5. 贴回白板：可编辑元数据序列化往返 ----------
        var paste = new AiPasteMetadata
        {
            Kind = AiPasteKind.Formula,
            Latex = "\\frac{x+1}{2}=3",
            Text = "由题意得 (x+1)/2 = 3",
            Lines = new List<string> { "由题意得 (x+1)/2 = 3", "解得 x = 5" },
            Annotations = new List<List<double>> { new() { 1, 2, 3, 4 } },
        };

        string pasteJson = paste.Serialize();
        AiPasteMetadata restoredPaste = AiPasteMetadata.TryParse(pasteJson);

        Assert.NotNull(restoredPaste);
        Assert.Equal(AiPasteKind.Formula, restoredPaste.Kind);
        Assert.Equal(paste.Latex, restoredPaste.Latex);
        Assert.Equal(paste.Text, restoredPaste.Text);
        Assert.Equal(paste.Lines, restoredPaste.Lines);
        Assert.Single(restoredPaste.Annotations);
        Assert.Equal(4d, restoredPaste.Annotations[0][3], 6);

        TimelineEvent pasteEvent = log.Append(StrokeAdded(T_PasteBack, 1, strokePaste));
        Assert.Equal(1, pasteEvent.PageIndex);
        Assert.Equal(new[] { StrokeSignature(strokePage1), StrokeSignature(strokePaste) },
            FrameSignatures(timeline.BuildFrame(T_PasteBack)));
        _output.WriteLine("步骤 5：贴回元数据（LaTeX + 文本）序列化往返一致，贴回笔画落在讲题页。");

        // ---------- 6. 新建练习页：新页码必须与原题页不同 ----------
        const int originalBoardPage = 1;
        const int practiceBoardPage = 2;
        VariantQuestionDraft draft = ExerciseFlowPolicy.Parse(
            "已知 f(x)=x^2+1，求 f(2) 的值。\n答案：5\n知识点：函数求值",
            ExerciseDifficulty.Basic,
            sourceSessionId: session.Id,
            slideId: SlideIdTwo,
            slideIndex: 2,
            nowMs: T_AiStep);

        Assert.True(ExerciseFlowPolicy.CanStart(draft), "第 6 步：贴回题目必须是可开始练习的有效草稿。");
        PracticePlan plan = ExerciseFlowPolicy.Plan(draft, practiceBoardPage, countdownMinutes: 3);

        Assert.True(plan.BoardPageIndex != originalBoardPage,
            "第 6 步：练习页页码必须与原题页不同，不能覆盖原题。");
        Assert.Equal(practiceBoardPage, plan.BoardPageIndex);
        Assert.Equal(draft.Id, plan.ExerciseId);
        Assert.Equal(3, plan.CountdownMinutes);
        Assert.Equal("练习结束", plan.BookmarkName);
        Assert.Equal("5", draft.Answer);
        Assert.Equal("函数求值", draft.KnowledgePoint);

        log.Append(PageChanged(T_PracticePage, practiceBoardPage));
        Assert.Equal(practiceBoardPage, timeline.PageAt(T_PracticePage));
        Assert.True(timeline.PageAt(T_PracticePage) != timeline.PageAt(T_PasteBack),
            "第 6 步：练习页事件之后当前页必须变化到新练习页。");
        _output.WriteLine($"步骤 6：练习计划落到第 {plan.BoardPageIndex} 页（原题页 {originalBoardPage}），未覆盖原题。");

        // ---------- 7. 倒计时开始 / 结束 + 「练习结束」书签 ----------
        log.Append(new TimelineEvent
        {
            TimeMs = T_TimerStarted,
            PageIndex = practiceBoardPage,
            Kind = TimelineEventKind.TimerStarted,
            Payload = "180000",
        });
        log.Append(StrokeAdded(T_PracticeInk, practiceBoardPage, strokePractice));
        TimelineEvent timerEnded = log.Append(new TimelineEvent
        {
            TimeMs = T_TimerEnded,
            PageIndex = practiceBoardPage,
            Kind = TimelineEventKind.TimerEnded,
            Payload = "0",
        });

        Assert.Equal(180000, timeline.SceneAt(T_TimerStarted).TimerRemainingMs);
        Assert.Equal(0, timeline.SceneAt(T_TimerEnded).TimerRemainingMs);
        Assert.Equal(practiceBoardPage, timerEnded.PageIndex);

        int bookmarkIndex = timeline.AddBookmark(timerEnded.TimeMs, "练习结束", "timer");
        SessionBookmark practiceBookmark = session.Bookmarks[bookmarkIndex];
        Assert.True(practiceBookmark.TimeMs == timerEnded.TimeMs,
            "第 7 步：『练习结束』书签时间必须与 TimerEnded 事件时间完全一致。");
        Assert.Equal("练习结束", practiceBookmark.Name);
        Assert.Equal(practiceBoardPage, practiceBookmark.PageIndex);

        // 录屏中按自动规则也会生成倒计时书签；自动书签保留合并窗口内的首个时间，
        // 因此这里确认它的锚点落在结束事件的 2 秒合并窗口内，而不是等于事件时间。
        SessionBookmark autoTimer = timeline.AutoBookmarks().Last(b => b.Kind == "timer");
        Assert.True(autoTimer.TimeMs <= timerEnded.TimeMs
                    && timerEnded.TimeMs - autoTimer.TimeMs < 2000,
            "第 7 步：自动倒计时书签的锚点必须落在 TimerEnded 的合并窗口内。");
        Assert.Equal("倒计时结束", autoTimer.Name);
        _output.WriteLine($"步骤 7：倒计时 180000ms → 0，书签『{practiceBookmark.Name}』@{practiceBookmark.TimeMs}ms 与事件一致。");

        // ---------- 保存快照（录屏/练习收尾的等价纯逻辑模型） ----------
        PracticeFinishRecord finish = PracticeFinishPolicy.Plan(plan, isRecording: true, snapshotSaved: true);
        Assert.True(finish.BookmarkCreated, "快照收尾：录制中必须保留书签记录。");
        Assert.Equal("练习结束", finish.BookmarkName);

        TeachingSession teaching = TeachingSession.Create("完整课堂链路回归", nowMs: T_Page0);
        teaching.Id = session.Id;
        Assert.True(teaching.SetCurrentSlide(SlideIdTwo, 2, 0, T_PptSlide2));
        teaching.Link("ai", problem.SessionId, SlideIdTwo, 2, boardPageIndex: originalBoardPage, nowMs: T_AiStep);
        teaching.Link("snapshot", "practice-snapshot-1", SlideIdTwo, 2, boardPageIndex: practiceBoardPage, nowMs: T_TimerEnded);
        teaching.Link("exercise", plan.ExerciseId, SlideIdTwo, 2, boardPageIndex: plan.BoardPageIndex, nowMs: T_TimerEnded);

        Assert.True(teaching.HasLink("ai", problem.SessionId));
        Assert.True(teaching.HasLink("exercise", plan.ExerciseId));
        Assert.Contains(problem.SessionId, teaching.AiSessionIds);
        Assert.Contains(plan.ExerciseId, teaching.ExerciseIds);

        TeachingSession restoredTeaching = TeachingSession.Deserialize(teaching.Serialize());
        Assert.NotNull(restoredTeaching);
        Assert.Equal(session.Id, restoredTeaching.Id);
        Assert.Equal(3, restoredTeaching.LinksForSlide(SlideIdTwo).Count);

        // ---------- 8. 时光机回放：从头 / 直接定位 / 倒退 / 乱序 四种路径帧指纹一致 ----------
        long[] keyTimes = { T_StrokePage0, T_StrokePage1, T_PasteBack, T_PracticeInk, T_TimerEnded };

        Dictionary<long, string> fromStart = WalkTimes(session, keyTimes);
        Dictionary<long, string> directSeek = new();
        foreach (long time in keyTimes)
        {
            // 每次都用全新的时间轴（冷启动），模拟从磁盘加载后直接定位到该时刻。
            directSeek[time] = Fingerprint(CloneSession(session), time);
        }
        Dictionary<long, string> backwards = WalkTimes(session, keyTimes.Reverse().ToArray());
        Dictionary<long, string> shuffled = WalkTimes(session, new[]
        {
            T_PasteBack, T_StrokePage0, T_TimerEnded, T_StrokePage1, T_PracticeInk,
        });

        foreach (long time in keyTimes)
        {
            Assert.True(fromStart[time] == directSeek[time],
                $"第 8 步：{time}ms 的『从头回放』与『直接定位』帧指纹必须完全一致。");
            Assert.True(fromStart[time] == backwards[time],
                $"第 8 步：{time}ms 的『从头回放』与『倒退回放』帧指纹必须完全一致。");
            Assert.True(fromStart[time] == shuffled[time],
                $"第 8 步：{time}ms 的『从头回放』与『乱序回放』帧指纹必须完全一致。");
        }

        // 回放帧必须跟随当前页，不能把其它页的笔画混进同一帧。
        Assert.Equal(new[] { StrokeSignature(strokePractice) }, FrameSignatures(timeline.BuildFrame(T_TimerEnded)));
        Assert.DoesNotContain(StrokeSignature(strokePage0), FrameSignatures(timeline.BuildFrame(T_TimerEnded)));
        _output.WriteLine("步骤 8：四路回放（从头/直接定位/倒退/乱序）在 5 个关键时间点帧指纹完全一致。");

        // ---------- 9. 导出课堂成果（内存合成 + 帧采样，不写真实 PPT/MP4） ----------
        var handoutPages = new List<HandoutPageSource>
        {
            new() { SlideId = SlideIdOne, Index = 1, Title = "第 1 页", AnnotationMetadataJson = "{\"inkStrokes\":1}" },
            new() { SlideId = SlideIdTwo, Index = 2, Title = "第 2 页", AnnotationMetadataJson = "{\"inkStrokes\":2}" },
            new() { SlideId = 103, Index = 3, Title = "第 3 页", AnnotationMetadataJson = "" },
        };
        var handoutOptions = new HandoutOptions
        {
            Content = HandoutContentMode.AnnotationsOnly,
            SelectedSlideIds = new List<int> { SlideIdOne, SlideIdTwo },
        };

        IReadOnlyList<HandoutPageSource> selectedPages = HandoutComposer.Select(handoutPages, handoutOptions);
        Assert.Equal(2, selectedPages.Count);
        Assert.Equal(new[] { SlideIdOne, SlideIdTwo }, selectedPages.Select(p => p.SlideId).ToArray());
        Assert.All(selectedPages, page => Assert.True(page.HasAnnotations,
            "第 9 步：仅批注模式导出的每页都必须带批注元数据。"));

        // 导出区间 = 书签时刻作为终点；仅使用纯采样函数，不真正编码文件。
        var exportOptions = new ExportOptions(
            Path.Combine(Path.GetTempPath(), "inkcanvas-classroom-chain-export.mp4"),
            T_StrokePage0,
            practiceBookmark.TimeMs,
            Fps: 10);

        IReadOnlyList<long> frameTimes = TimeMachineExporter.SampleFrameTimes(exportOptions);
        int estimated = TimeMachineExporter.EstimateFrameCount(exportOptions);

        Assert.Equal(estimated, frameTimes.Count);
        Assert.Equal(5, frameTimes.Count);
        Assert.Equal(T_StrokePage0, frameTimes[0]);
        Assert.True(frameTimes[^1] <= practiceBookmark.TimeMs,
            "第 9 步：采样帧时刻不得越过导出区间终点。");
        Assert.Equal(practiceBookmark.TimeMs - T_StrokePage0,
            TimeMachineExporter.SourceDurationMs(exportOptions));
        Assert.True(IsAscending(frameTimes), "第 9 步：采样帧时刻必须严格递增。");
        _output.WriteLine($"步骤 9：讲义选页 2 页、采样帧 {frameTimes.Count} 帧（区间 {T_StrokePage0}–{practiceBookmark.TimeMs}ms）。");

        // ---------- 10. 最终一致性断言 ----------
        Assert.All(session.Events, e => Assert.True(e.SessionId == session.Id,
            "第 10 步：整条链路所有事件必须属于同一 SessionId。"));
        Assert.True(IsSeqStrictlyIncreasing(session.Events),
            "第 10 步：整条链路事件 Seq 必须持续递增，不允许回退。");
        Assert.Equal(session.Id, teaching.Id);

        // 页码不串：每个关键时间点的帧只包含该页的笔画。
        Assert.Equal(new[] { StrokeSignature(strokePage0) }, FrameSignatures(timeline.BuildFrame(T_StrokePage0)));
        Assert.Equal(new[] { StrokeSignature(strokePage1) }, FrameSignatures(timeline.BuildFrame(T_StrokePage1)));
        Assert.Equal(new[] { StrokeSignature(strokePage1), StrokeSignature(strokePaste) },
            FrameSignatures(timeline.BuildFrame(T_PasteBack)));
        Assert.Equal(new[] { StrokeSignature(strokePractice) }, FrameSignatures(timeline.BuildFrame(T_PracticeInk)));

        // 书签时间、倒计时结束事件时间与导出终点必须一致。
        Assert.True(practiceBookmark.TimeMs == timerEnded.TimeMs && timerEnded.TimeMs == exportOptions.EndMs,
            "第 10 步：书签时间、倒计时结束时刻与导出区间终点必须完全一致。");

        // 导出页必须落在课件事件覆盖过的 slideId 集合内。
        HashSet<int> slideIdsOnTimeline = session.Events
            .Where(e => e.Kind == TimelineEventKind.PptSlideChanged)
            .Select(e => e.PptSlideId)
            .Where(id => id > 0)
            .ToHashSet();
        Assert.All(selectedPages, page => Assert.True(slideIdsOnTimeline.Contains(page.SlideId),
            "第 10 步：导出的每一页都必须来自本会话课件翻页事件覆盖的 slideId。"));

        // 全链路事件数量与关键节点数量对齐，防止静默丢事件。
        Assert.Equal(13, session.Events.Count);

        _output.WriteLine("步骤 10：SessionId 统一、页码不串、书签与导出区间一致，链路回归通过。");
    }

    // ---- 链路辅助 -----------------------------------------------------------

    /// <summary>
    /// 测试内部的最小事件日志适配器：替代依赖 UI 的 <see cref="InkRecorder"/>，
    /// 负责统一分配 SessionId / Seq，并从 PPT 载荷回填 <see cref="TimelineEvent.PptSlideId"/>。
    /// </summary>
    private sealed class ChainEventLog
    {
        private readonly ClassroomSession _session;
        private int _nextSeq = 1;

        public ChainEventLog(ClassroomSession session) => _session = session;

        public TimelineEvent Append(TimelineEvent ev)
        {
            ev.SessionId = _session.Id;
            ev.Seq = _nextSeq++;

            if (ev.PptSlideId <= 0 && ev.Kind == TimelineEventKind.PptSlideChanged)
            {
                PptSlideChange change = InkTimeline.DecodePptSlideChanged(ev.Payload);
                if (change.IsValid && change.SlideId > 0) ev.PptSlideId = change.SlideId;
            }

            _session.Events.Add(ev);
            return ev;
        }
    }

    private static TimelineEvent PageChanged(long timeMs, int page) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PageChanged,
    };

    private static TimelineEvent BackgroundChanged(long timeMs, int page, string name) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.BackgroundChanged,
        Payload = name,
    };

    private static TimelineEvent PptSlideChanged(long timeMs, int page, int slideId, int index) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.PptSlideChanged,
        Payload = InkTimeline.EncodePptSlideChanged(slideId, index, ""),
    };

    private static TimelineEvent StrokeAdded(long timeMs, int page, InkStroke stroke) => new()
    {
        TimeMs = timeMs,
        PageIndex = page,
        Kind = TimelineEventKind.StrokeAdded,
        StrokeId = stroke.Id.ToString(),
        StrokeJson = StrokeJsonCodec.Serialize(stroke),
    };

    private static InkStroke NewStroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.AddPoint(x + 10f, 12f);
        stroke.Complete();
        return stroke;
    }

    private static string[] FrameSignatures(IReadOnlyList<InkStroke> frame)
        => frame.Select(StrokeSignature).ToArray();

    /// <summary>
    /// 笔画的稳定几何签名。<see cref="StrokeJsonCodec"/> 往返会重新生成实例 Id，
    /// 因此跨时间轴实例比较时必须用几何 / 点数而不是 Id。
    /// </summary>
    private static string StrokeSignature(InkStroke stroke)
    {
        IReadOnlyList<InkPoint> points = stroke.Points;
        if (points.Count == 0) return "empty";
        InkPoint first = points[0];
        InkPoint last = points[^1];
        return points.Count.ToString()
            + ":" + first.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "," + first.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + last.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + "," + last.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + stroke.Attributes.BaseWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool IsSeqStrictlyIncreasing(IReadOnlyList<TimelineEvent> events)
    {
        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].Seq <= events[i - 1].Seq) return false;
        }
        return events.Count > 0;
    }

    private static bool IsAscending(IReadOnlyList<long> times)
    {
        for (int i = 1; i < times.Count; i++)
        {
            if (times[i] <= times[i - 1]) return false;
        }
        return true;
    }

    /// <summary>滚动访问一组时刻，返回每个时刻的帧指纹（同一条时间轴实例）。</summary>
    private static Dictionary<long, string> WalkTimes(ClassroomSession source, IReadOnlyList<long> order)
    {
        ClassroomSession copy = CloneSession(source);
        var timeline = new InkTimeline(copy);
        var result = new Dictionary<long, string>();
        foreach (long time in order)
        {
            result[time] = Fingerprint(timeline, time);
        }
        return result;
    }

    /// <summary>冷启动定位：新建时间轴后只取一个目标时刻的帧指纹。</summary>
    private static string Fingerprint(ClassroomSession session, long timeMs)
        => Fingerprint(new InkTimeline(session), timeMs);

    /// <summary>帧指纹：页面 + 课件页 + 笔画几何签名，保证兼容跨实例比较。</summary>
    private static string Fingerprint(InkTimeline timeline, long timeMs)
    {
        ClassroomScene scene = timeline.SceneAt(timeMs);
        IReadOnlyList<InkStroke> frame = timeline.BuildFrame(timeMs);
        string strokes = string.Join(",", frame.Select(StrokeSignature));
        return scene.PageIndex + "/" + scene.PptSlideIndex + "/" + strokes;
    }

    /// <summary>深拷贝会话事件，避免四条回放路径共享检查点与笔画缓存。</summary>
    private static ClassroomSession CloneSession(ClassroomSession source)
    {
        var clone = new ClassroomSession
        {
            Id = source.Id,
            Name = source.Name,
            CreatedAt = source.CreatedAt,
            Checkpoints = new List<SessionCheckpoint>(),
            Bookmarks = new List<SessionBookmark>(),
            Events = source.Events.Select(CloneEvent).ToList(),
        };
        return clone;
    }

    private static TimelineEvent CloneEvent(TimelineEvent source) => new()
    {
        SessionId = source.SessionId,
        Seq = source.Seq,
        TimeMs = source.TimeMs,
        PageIndex = source.PageIndex,
        PptSlideId = source.PptSlideId,
        Kind = source.Kind,
        StrokeId = source.StrokeId,
        StrokeJson = source.StrokeJson,
        StrokeIds = source.StrokeIds,
        Payload = source.Payload,
    };
}
