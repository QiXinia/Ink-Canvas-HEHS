using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Features.AiAssistant;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using System.Diagnostics;
using System.Numerics;

namespace InkCanvas.Features.Classroom;

/// <summary>
/// 笔画级录制器：订阅 <see cref="BoardService"/> 当前页控制器的笔画事件与切页 / 背景事件，
/// 生成可回放的 <see cref="ClassroomSession"/>。
/// <para>
/// 笔画添加 / 擦除 / 清空来自 <see cref="InkController"/> 的最小新增事件；
/// 撤销 / 重做没有独立事件，通过比较 <see cref="InkController.Invalidated"/> 前后的
/// 文档笔画 id 集合差异推导（<c>History</c> 的 Undo/Redo 计数用于区分撤销与重做）。
/// </para>
/// </summary>
public sealed class InkRecorder : IDisposable
{
    private const long SnapshotIntervalMs = 2_000;

    private readonly BoardService _board;
    private readonly int _boardId;
    private readonly Stopwatch _clock = new();
    private readonly object _gate = new();
    private InkController _controller;
    private HashSet<string> _knownIds = new();
    private int _lastUndoCount;
    private int _lastRedoCount;
    private InkTimeline _timeline;
    private SessionJournal _journal;
    private long _lastSnapshotTimestamp;
    private int _nextSeq = 1;

    // 暂停计时：ElapsedMs = 时钟已过 - (已累计暂停 + 当前暂停段)。
    private long _pausedTotalMs;
    private long _pauseStartedMs = -1;
    private const int DedupeSignaturePrefixLength = 64;


    public InkRecorder(BoardService board, int boardId = 0)
    {
        _board = board ?? throw new ArgumentNullException(nameof(board));
        _boardId = boardId;
    }

    public ClassroomSession Session { get; private set; }

    public bool IsRecording { get; private set; }

    /// <summary>录制已暂停：期间 <see cref="Append"/> 与自动事件全部丢弃，计时不计入暂停时长。</summary>
    public bool IsPaused { get; private set; }

    public InkTimeline Timeline => _timeline;

    /// <summary>录制中用于异常恢复的增量日志；未启用时为 null。</summary>
    public SessionJournal Journal => _journal;

    /// <summary>笔画提交时的抽稀容差（DIP）。默认 0.8，设为 0 表示不抽稀。</summary>
    public double SimplificationTolerance { get; set; } = StrokeSimplifier.DefaultTolerance;

    /// <summary>会话 / 日志 / 快照目录；默认 <see cref="ClassroomSessionStore.DefaultDirectory"/>。</summary>
    public string SessionDirectory { get; set; }

    /// <summary>
    /// 当前录制时钟（毫秒，相对录制开始，已扣除暂停时长）。供外部事件源打时间戳。
    /// 暂停期间继续增长，恢复后不产生空档：恢复前暂停的增量会累加进扣除值。
    /// </summary>
    public long ElapsedMs
    {
        get
        {
            long now = _clock.ElapsedMilliseconds;
            long paused = _pausedTotalMs;
            if (IsPaused && _pauseStartedMs >= 0) paused += Math.Max(0, now - _pauseStartedMs);
            return Math.Max(0, now - paused);
        }
    }

    /// <summary>暂停录制。暂停/恢复本身不写入时间轴，仅记 Trace 日志。</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRecording || IsPaused) return;
            IsPaused = true;
            _pauseStartedMs = _clock.ElapsedMilliseconds;
        }
        Log("板书录制已暂停（暂停期间事件不入时间轴）");
    }

    /// <summary>继续录制；暂停时长从 <see cref="ElapsedMs"/> 中扣除，时间轴保持连续。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (!IsRecording || !IsPaused) return;
            long now = _clock.ElapsedMilliseconds;
            if (_pauseStartedMs >= 0) _pausedTotalMs += Math.Max(0, now - _pauseStartedMs);
            _pauseStartedMs = -1;
            IsPaused = false;
        }

        // 暂停期间产生的笔画不补录（需求：全部丢弃），但同步已知集合，
        // 避免恢复后的撤销/重做把它们误判为本次操作差异。
        if (_controller is not null)
            _knownIds = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        SyncHistoryCounts();

        Log("板书录制已继续（暂停时长已扣除）");
    }

    private static void Log(string message)
    {
        try { Core.Diagnostics.FileLog.WriteLogToFile(message, Core.Diagnostics.LogType.Trace); }
        catch { /* 日志失败不影响录制 */ }
    }

    /// <summary>开始录制。会在 t=0 记录初始页码、背景，并把已有笔画作为起点快照。</summary>
    public void Start(string name = null)
    {
        if (IsRecording) return;

        Session = new ClassroomSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? $"板书 {DateTime.Now:yyyy-MM-dd HH:mm}" : name,
            CreatedAt = DateTime.Now,
            Events = new List<TimelineEvent>(),
            Checkpoints = new List<SessionCheckpoint>(),
        };
        _timeline = new InkTimeline(Session);
        OpenJournal();

        _clock.Restart();
        IsRecording = true;
        IsPaused = false;
        _pausedTotalMs = 0;
        _pauseStartedMs = -1;
        _nextSeq = 1;

        Bind(_board.Current);

        AddEvent(TimelineEventKind.PageChanged, _board.CurrentIndex);
        AddEvent(TimelineEventKind.BackgroundChanged, _board.CurrentIndex, payload: _board.Background.ToString());
        SeedExistingStrokes();
        SaveSnapshot();

        _board.PageChanged += OnBoardPageChanged;
        _board.BackgroundChanged += OnBoardBackgroundChanged;
        _board.AiObjectsChanged += OnAiObjectsChanged;
    }

    /// <summary>
    /// 停止录制：刷盘增量日志，合并为正式会话 JSON，并删除日志与快照。
    /// 合并失败时保留日志，下次启动可由 <see cref="ClassroomSessionStore.LoadWithJournal"/> 恢复。
    /// </summary>
    public void Stop()
    {
        if (!IsRecording) return;
        IsRecording = false;
        IsPaused = false;
        _pauseStartedMs = -1;
        _clock.Stop();

        _board.PageChanged -= OnBoardPageChanged;
        _board.BackgroundChanged -= OnBoardBackgroundChanged;
        _board.AiObjectsChanged -= OnAiObjectsChanged;
        UnbindController();
        FinishJournal();
    }

    /// <summary>保存当前会话到会话目录，返回文件路径。</summary>
    public string Save(string directory = null)
        => Session is null ? null : ClassroomSessionStore.Save(Session, directory);

    private void OpenJournal()
    {
        if (Session is null) return;
        try
        {
            _journal = SessionJournal.Open(Session.Id, SessionDirectory, Session.Name, Session.CreatedAt);
        }
        catch
        {
            // 日志不可用时录制照常进行，只是失去异常恢复能力。
            _journal = null;
        }
    }

    private void FinishJournal()
    {
        var journal = _journal;
        _journal = null;
        if (journal is null) return;

        try { journal.Close(); }
        catch { /* 已尽量刷盘 */ }

        bool saved = false;
        try
        {
            ClassroomSessionStore.Save(Session, SessionDirectory);
            saved = true;
        }
        catch
        {
            // 保存失败时保留日志供恢复。
        }

        if (saved) ClassroomSessionStore.DeleteRecoveryArtifacts(Session.Id, SessionDirectory);
    }

    private void SaveSnapshot()
    {
        if (Session is null) return;
        try
        {
            ClassroomSessionStore.SaveSnapshot(Session, SessionDirectory);
            _lastSnapshotTimestamp = Stopwatch.GetTimestamp();
        }
        catch
        {
            // 快照失败不影响录制。
        }
    }

    private void MaybeSaveSnapshot()
    {
        if (_journal is null || _lastSnapshotTimestamp == 0) return;
        long elapsed = (Stopwatch.GetTimestamp() - _lastSnapshotTimestamp) * 1000 / Stopwatch.Frequency;
        if (elapsed >= SnapshotIntervalMs) SaveSnapshot();
    }

    private string SerializeStroke(InkStroke stroke)
    {
        if (stroke is null) return null;
        if (!(SimplificationTolerance > 0)) return StrokeJsonCodec.Serialize(stroke);
        return StrokeJsonCodec.Serialize(StrokeSimplifier.Simplify(stroke, SimplificationTolerance));
    }

    private void SeedExistingStrokes()
    {
        for (int page = 0; page < _board.Pages.Count; page++)
        {
            foreach (var stroke in _board.Pages[page].Document.Strokes)
                AddEvent(TimelineEventKind.StrokeAdded, page, stroke.Id.ToString(), SerializeStroke(stroke));
        }
    }

    private void Bind(InkController controller)
    {
        UnbindController();
        _controller = controller;
        if (_controller is not null)
        {
            _controller.StrokeCommitted += OnStrokeCommitted;
            _controller.StrokesErased += OnStrokesErased;
            _controller.Cleared += OnCleared;
            _controller.Invalidated += OnControllerInvalidated;
            _controller.ViewChanged += OnViewChanged;
            _knownIds = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        }
        else
        {
            _knownIds = new HashSet<string>();
        }
        SyncHistoryCounts();
    }

    private void UnbindController()
    {
        if (_controller is null) return;
        _controller.StrokeCommitted -= OnStrokeCommitted;
        _controller.StrokesErased -= OnStrokesErased;
        _controller.Cleared -= OnCleared;
        _controller.Invalidated -= OnControllerInvalidated;
        _controller.ViewChanged -= OnViewChanged;
    }

    /// <summary>
    /// 视图缩放/平移：记录矩阵推导出的 scale=sqrt(|det|)、tx=M31、ty=M32。
    /// 暂停期间由 <see cref="AddEvent"/> 统一丢弃。
    /// </summary>
    private void OnViewChanged(Matrix3x2 transform)
    {
        if (!IsRecording || IsPaused) return;
        double scale = Math.Sqrt(Math.Abs(transform.GetDeterminant()));
        if (!double.IsFinite(scale)) scale = 1;
        AddEvent(TimelineEventKind.ViewChanged, _board.CurrentIndex,
            payload: InkTimeline.EncodeViewChanged(scale, transform.M31, transform.M32));
    }

    private void OnBoardPageChanged()
    {
        if (!IsRecording) return;
        Bind(_board.Current);
        AddEvent(TimelineEventKind.PageChanged, _board.CurrentIndex);
    }

    private void OnBoardBackgroundChanged()
    {
        if (!IsRecording) return;
        AddEvent(TimelineEventKind.BackgroundChanged, _board.CurrentIndex, payload: _board.Background.ToString());
    }

    private void OnStrokeCommitted(InkStroke stroke)
    {
        if (!IsRecording || IsPaused || stroke is null) return;
        string id = stroke.Id.ToString();
        _knownIds.Add(id);
        AddEvent(TimelineEventKind.StrokeAdded, _board.CurrentIndex, id, SerializeStroke(stroke));
        SyncHistoryCounts();
    }

    private void OnStrokesErased(IReadOnlyList<InkStroke> strokes)
    {
        if (!IsRecording || IsPaused || strokes is null || strokes.Count == 0) return;
        var ids = strokes.Select(s => s.Id.ToString()).ToList();
        foreach (var id in ids) _knownIds.Remove(id);
        AddEvent(TimelineEventKind.StrokesRemoved, _board.CurrentIndex, ids: ids);
        SyncHistoryCounts();
    }

    private void OnCleared()
    {
        if (!IsRecording || IsPaused) return;
        _knownIds.Clear();
        AddEvent(TimelineEventKind.Cleared, _board.CurrentIndex);
        SyncHistoryCounts();
    }

    /// <summary>撤销 / 重做：文档 id 集合的差异即本次操作的效果。</summary>
    private void OnControllerInvalidated()
    {
        if (!IsRecording || IsPaused || _controller is null) return;

        var current = new HashSet<string>(_controller.Document.Strokes.Select(s => s.Id.ToString()));
        if (current.SetEquals(_knownIds))
        {
            SyncHistoryCounts();
            return;
        }

        var removed = _knownIds.Where(id => !current.Contains(id)).ToList();
        var added = current.Where(id => !_knownIds.Contains(id)).ToList();

        bool isUndo = _controller.History.RedoCount > _lastRedoCount;
        bool isRedo = !isUndo && _controller.History.UndoCount > _lastUndoCount;
        var kind = isUndo ? TimelineEventKind.Undo
            : isRedo ? TimelineEventKind.Redo
            : (added.Count > 0 ? TimelineEventKind.Redo : TimelineEventKind.Undo);

        var addedSet = new HashSet<string>(added);
        var addedStrokes = _controller.Document.Strokes
            .Where(s => addedSet.Contains(s.Id.ToString()))
            .ToList();

        // 撤销/重做重新出现的笔画与 StrokeAdded 一样先抽稀，避免回放体积反弹。
        var replayStrokes = addedStrokes
            .Select(s => SimplificationTolerance > 0
                ? StrokeSimplifier.Simplify(s, SimplificationTolerance)
                : s)
            .ToList();

        AddEvent(kind, _board.CurrentIndex, ids: removed,
            payload: replayStrokes.Count > 0 ? InkTimeline.EncodeAddedStrokes(replayStrokes) : null);

        _knownIds = current;
        SyncHistoryCounts();
    }

    private void SyncHistoryCounts()
    {
        if (_controller is null) return;
        _lastUndoCount = _controller.History.UndoCount;
        _lastRedoCount = _controller.History.RedoCount;
    }

    private void AddEvent(TimelineEventKind kind, int page,
        string strokeId = null, string strokeJson = null,
        IReadOnlyList<string> ids = null, string payload = null,
        int? boardId = null)
    {
        if (!IsRecording || IsPaused || Session is null) return;

        Append(new TimelineEvent
        {
            TimeMs = ElapsedMs,
            PageIndex = page,
            BoardId = boardId ?? _boardId,
            Kind = kind,
            StrokeId = strokeId,
            StrokeJson = strokeJson,
            StrokeIds = ids,
            Payload = payload,
        });
    }

    /// <summary>
    /// AI 板书对象变化：replace 先补写既有 StrokesRemoved（删除原笔画），再写一条结构化
    /// AiStep；对象 PNG 直接来自 <see cref="BoardImageAsset.Data"/>，超过 400 KB 时只记录
    /// renderMissing=true 并告警，回放退化为占位框，不请求模型。
    /// </summary>
    private void OnAiObjectsChanged(BoardAiObjectChange change)
    {
        if (!IsRecording || IsPaused || change is null) return;
        if (change.Objects is null || change.Objects.Count == 0) return;

        int page = change.PageIndex >= 0 ? change.PageIndex : _board.CurrentIndex;

        if (change.RemovedStrokes is { Count: > 0 })
        {
            var removedIds = change.RemovedStrokes
                .Where(stroke => stroke is not null)
                .Select(stroke => stroke.Id.ToString())
                .ToList();
            if (removedIds.Count > 0)
            {
                foreach (string id in removedIds) _knownIds.Remove(id);
                AddEvent(TimelineEventKind.StrokesRemoved, page, ids: removedIds);
            }
        }

        var objects = change.Objects
            .Where(image => image is not null)
            .Select(image => ToTimelineObject(image, page))
            .ToList();
        if (objects.Count == 0) return;

        string payload = AiObjectPayloadCodec.Encode(change.Action, page, _boardId, objects);
        if (string.IsNullOrEmpty(payload)) return;
        AddEvent(TimelineEventKind.AiStep, page, payload: payload);
    }

    private static TimelineAiObject ToTimelineObject(BoardImageAsset image, int pageIndex)
    {
        AiPasteMetadata metadata = AiPasteMetadata.TryParse(image.Metadata);
        string kind = metadata?.Kind.ToString() ?? "image";

        bool tooLarge = image.Data is { Length: > AiObjectPayloadCodec.MaxRenderPngBytes };
        string png = "";
        if (!tooLarge && image.Data is { Length: > 0 })
            png = Convert.ToBase64String(image.Data);
        if (tooLarge)
        {
            Log($"[AI] 板书回放对象超过 PNG 阈值，回放显示占位：id={image.Id}，"
                + $"bytes={image.Data.Length}，limit={AiObjectPayloadCodec.MaxRenderPngBytes}");
        }

        return new TimelineAiObject
        {
            Id = image.Id.ToString(),
            Kind = kind,
            PageIndex = pageIndex,
            BoardId = 0,
            Left = image.Left,
            Top = image.Top,
            Width = image.Width,
            Height = image.Height,
            LayerOrder = image.LayerOrder,
            Latex = metadata?.Latex ?? "",
            Text = metadata?.Text ?? "",
            Lines = metadata is null ? Array.Empty<string>() : metadata.Lines,
            RenderPngBase64 = png,
            RenderMissing = tooLarge || string.IsNullOrEmpty(png),
            Notes = metadata is null ? Array.Empty<string>() : metadata.Notes,
        };
    }

    /// <summary>
    /// 线程安全地追加一个外部事件（PPT 翻页 / 点名 / 倒计时等）。
    /// 暂停期间丢弃；与尾部事件完全同签名（Kind + TimeMs + StrokeId + Payload 前 64 字符）时忽略；
    /// 事件时间早于最后一个事件时按时间插入，并重建检查点（增量游标只支持尾部追加）。
    /// 非相邻的重复事件是合法重放，予以保留。
    /// 标书签候选事件会自动补一条章节书签。
    /// </summary>
    public void Append(TimelineEvent ev)
    {
        if (ev is null || Session is null) return;

        lock (_gate)
        {
            if (!IsRecording || IsPaused) return;
            var events = Session.Events ??= new List<TimelineEvent>();

            // 归属与序号由录制器统一分配：外部事件源（PPT / 点名 / 倒计时）只需给出
            // 时间、页与载荷，禁止自行编号，避免同毫秒事件在恢复后失去稳定顺序。
            ev.SessionId = Session.Id;
            if (ev.Seq <= 0) ev.Seq = _nextSeq++;
            else if (ev.Seq >= _nextSeq) _nextSeq = ev.Seq + 1;

            if (ev.PptSlideId <= 0 && ev.Kind == TimelineEventKind.PptSlideChanged)
            {
                PptSlideChange change = InkTimeline.DecodePptSlideChanged(ev.Payload);
                if (change.IsValid && change.SlideId > 0) ev.PptSlideId = change.SlideId;
            }

            // 相邻完全重复事件（同一次手势/切换被多次上报）不再入时间轴。
            if (events.Count > 0 && SameSignature(events[^1], ev)) return;

            if (events.Count > 0 && ev.TimeMs < events[^1].TimeMs)
            {
                int index = 0;
                while (index < events.Count && events[index].TimeMs <= ev.TimeMs) index++;
                events.Insert(index, ev);
                Session.Checkpoints.Clear();
                _timeline = new InkTimeline(Session);
            }
            else
            {
                events.Add(ev);
            }

            if (IsBookmarkSource(ev.Kind)) _timeline?.ApplyAutoBookmarks();

            // 增量日志在每次追加后立即进入缓冲，2 秒 / 50 个事件自动刷盘。
            _journal?.Append(ev);
            MaybeSaveSnapshot();
        }
    }

    private static bool SameSignature(TimelineEvent a, TimelineEvent b)
    {
        if (a.Kind != b.Kind || a.TimeMs != b.TimeMs) return false;
        if (!string.Equals(a.StrokeId, b.StrokeId, StringComparison.Ordinal)) return false;

        // AI 对象事件以 objectId+action 开头，但同一对象连续 update 仍可能前缀相同；
        // 这里按完整 payload 比较，避免 64 字符截断把合法事件误判为重复。
        if (a.Kind == TimelineEventKind.AiStep)
            return string.Equals(a.Payload ?? "", b.Payload ?? "", StringComparison.Ordinal);

        string pa = a.Payload ?? "";
        string pb = b.Payload ?? "";
        int la = Math.Min(pa.Length, DedupeSignaturePrefixLength);
        int lb = Math.Min(pb.Length, DedupeSignaturePrefixLength);
        return la == lb && string.CompareOrdinal(pa, 0, pb, 0, la) == 0;
    }

    private static bool IsBookmarkSource(TimelineEventKind kind)
        => kind is TimelineEventKind.PageChanged
            or TimelineEventKind.Cleared
            or TimelineEventKind.PptSlideChanged
            or TimelineEventKind.RosterDrawn
            or TimelineEventKind.TimerEnded;

    public void Dispose()
    {
        Stop();
        _controller = null;
    }
}