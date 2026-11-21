using InkCanvas.Core.Classroom;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace InkCanvas.Features.Classroom;

/// <summary>
/// 录制中的增量日志（JSONL）。每个事件追加一行，按「2 秒或 50 个事件」批量刷盘，
/// 因此异常退出最多丢最后约 2 秒。
/// <para>
/// 文件位于 <c>%APPDATA%\InkCanvas\sessions\&lt;id&gt;.journal</c>；正常结束录制时由
/// <see cref="InkRecorder"/> 合并为正式会话 JSON 并删除日志。恢复时结合最后一个完整快照
/// （<c>&lt;id&gt;.snapshot.json</c>）与日志事件，截断的最后一行直接忽略。
/// </para>
/// </summary>
public sealed class SessionJournal : IDisposable
{
    public const long DefaultFlushIntervalMs = 2_000;
    public const int DefaultFlushEventCount = 50;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly string _sessionId;
    private readonly string _directory;
    private StreamWriter _writer;
    private long _lastFlushTimestamp;
    private int _pendingCount;

    private SessionJournal(string sessionId, string directory, StreamWriter writer)
    {
        _sessionId = sessionId;
        _directory = directory;
        _writer = writer;
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
    }

    public string SessionId => _sessionId;

    public string Path => PathFor(_sessionId, _directory);

    /// <summary>日志文件路径（默认会话目录）。</summary>
    public static string PathFor(string sessionId, string directory = null)
        => System.IO.Path.Combine(directory ?? ClassroomSessionStore.DefaultDirectory, sessionId + ".journal");

    /// <summary>打开（或续写）会话日志；首次写入时补一行会话元数据。</summary>
    public static SessionJournal Open(string sessionId, string directory = null, string name = null, DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("会话 id 不能为空。", nameof(sessionId));

        string dir = directory ?? ClassroomSessionStore.DefaultDirectory;
        Directory.CreateDirectory(dir);
        string path = PathFor(sessionId, dir);
        bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
        var journal = new SessionJournal(sessionId, dir, writer);

        if (fresh)
        {
            journal.WriteLineLocked(new JournalLine
            {
                Type = "meta",
                Id = sessionId,
                Name = name,
                CreatedAt = createdAt ?? DateTime.Now,
            });
            journal.FlushLocked();
        }
        return journal;
    }

    /// <summary>是否还有未完成的日志（用于启动时检测异常退出）。</summary>
    public static bool HasPending(string sessionId, string directory = null)
    {
        string path = PathFor(sessionId, directory);
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    /// <summary>
    /// 读取 journal 与最后一个完整快照，返回已落盘的末事件时间（毫秒）。
    /// 两者都没有有效事件时返回 -1。用于量化异常恢复最多丢多少时间。
    /// </summary>
    public static long LastEventTimeMs(string sessionId, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return -1;

        long last = -1;
        var (_, events) = ReadJournal(sessionId, directory);
        foreach (var ev in events)
        {
            if (ev is not null && ev.TimeMs > last) last = ev.TimeMs;
        }

        var snapshot = TryLoadSnapshot(sessionId, directory);
        if (snapshot?.Events is { Count: > 0 })
        {
            foreach (var ev in snapshot.Events)
            {
                if (ev is not null && ev.TimeMs > last) last = ev.TimeMs;
            }
        }
        return last;
    }

    /// <summary>读取最后一个完整快照；损坏 / 不可读时返回 null（不影响末事件时间查询）。</summary>
    private static ClassroomSession TryLoadSnapshot(string sessionId, string directory)
    {
        try { return ClassroomSessionStore.LoadSnapshot(sessionId, directory); }
        catch { return null; }
    }

    /// <summary>列出目录中所有未完成日志的会话 id。</summary>
    public static IReadOnlyList<string> PendingSessionIds(string directory = null)
    {
        string dir = directory ?? ClassroomSessionStore.DefaultDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.journal")
            .Select(file => System.IO.Path.GetFileNameWithoutExtension(file))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 追加一个事件。累计 50 个事件或距上次刷盘 2 秒时自动落盘。
    /// 线程安全，调用方无需额外加锁。
    /// </summary>
    public void Append(TimelineEvent ev)
    {
        if (ev is null) return;

        lock (_gate)
        {
            if (_writer is null) return;
            WriteLineLocked(new JournalLine { Type = "event", Event = ev });
            _pendingCount++;

            long now = Stopwatch.GetTimestamp();
            long elapsedMs = (now - _lastFlushTimestamp) * 1000 / Stopwatch.Frequency;
            if (_pendingCount >= DefaultFlushEventCount || elapsedMs >= DefaultFlushIntervalMs)
                FlushLocked();
        }
    }

    /// <summary>立即刷盘。</summary>
    public void Flush()
    {
        lock (_gate) FlushLocked();
    }

    /// <summary>刷盘并关闭写入器；日志文件保留，供 <see cref="Recover"/> 使用。</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_writer is null) return;
            FlushLocked();
            _writer.Dispose();
            _writer = null;
        }
    }

    /// <summary>关闭并删除日志文件（正常结束录制后调用）。</summary>
    public void Delete()
    {
        Close();
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch
        {
            // 删除失败不应影响停止录制。
        }
    }

    /// <summary>
    /// 从未完成日志 + 最后一个完整快照恢复会话。两者都不存在时返回 null。
    /// 日志末尾被截断的半行会被忽略，不会抛异常。
    /// </summary>
    public static ClassroomSession Recover(string sessionId, string directory = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;

        var snapshot = ClassroomSessionStore.LoadSnapshot(sessionId, directory)
            ?? ClassroomSessionStore.Load(ClassroomSessionStore.PathFor(sessionId, directory));
        var (meta, events) = ReadJournal(sessionId, directory);

        if (snapshot is null && events.Count == 0 && meta is null) return null;

        ClassroomSession session;
        if (snapshot is not null)
        {
            session = snapshot;
            // 日志从录制开始即完整记录，事件比快照多时以日志为准；日志被截断而少于快照时
            // 保留快照事件，避免倒退。
            if (events.Count > session.Events.Count) session.Events = events;
        }
        else
        {
            session = new ClassroomSession
            {
                Id = sessionId,
                Name = string.IsNullOrWhiteSpace(meta?.Name) ? $"板书 {DateTime.Now:yyyy-MM-dd HH:mm}" : meta.Name,
                CreatedAt = meta?.CreatedAt ?? DateTime.Now,
                Events = events,
                Checkpoints = new List<SessionCheckpoint>(),
                Bookmarks = new List<SessionBookmark>(),
            };
        }

        session.Id = sessionId;
        session.Events ??= new List<TimelineEvent>();
        session.Checkpoints ??= new List<SessionCheckpoint>();
        session.Bookmarks ??= new List<SessionBookmark>();
        return session;
    }

    private static (JournalMeta Meta, List<TimelineEvent> Events) ReadJournal(string sessionId, string directory)
    {
        string path = PathFor(sessionId, directory);
        JournalMeta meta = null;
        var events = new List<TimelineEvent>();
        if (!File.Exists(path)) return (null, events);

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return (null, events); }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JournalLine parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<JournalLine>(line, Options);
            }
            catch (JsonException)
            {
                // 异常退出时最后一行可能是半条记录。
                continue;
            }
            if (parsed is null) continue;

            if (string.Equals(parsed.Type, "meta", StringComparison.OrdinalIgnoreCase))
            {
                meta = new JournalMeta
                {
                    Id = parsed.Id,
                    Name = parsed.Name,
                    CreatedAt = parsed.CreatedAt ?? DateTime.Now,
                };
            }
            else if (parsed.Event is not null)
            {
                events.Add(parsed.Event);
            }
        }
        return (meta, events);
    }

    private void WriteLineLocked(JournalLine line)
    {
        string json = JsonSerializer.Serialize(line, Options);
        _writer.Write(json);
        _writer.Write('\n');
    }

    private void FlushLocked()
    {
        if (_writer is null) return;
        try
        {
            _writer.Flush();
            _writer.BaseStream.Flush();
        }
        catch
        {
            // 记录过程中的磁盘错误不应打断录制。
        }
        _lastFlushTimestamp = Stopwatch.GetTimestamp();
        _pendingCount = 0;
    }

    public void Dispose() => Close();

    private sealed class JournalLine
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime? CreatedAt { get; set; }
        public TimelineEvent Event { get; set; }
    }

    private sealed class JournalMeta
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}