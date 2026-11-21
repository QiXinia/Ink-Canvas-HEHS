namespace InkCanvas.Core.Classroom;

public sealed class ClassSummaryReference
{
    public string Kind { get; set; } = "";

    public string Id { get; set; } = "";

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    public int BoardPageIndex { get; set; } = -1;

    public long TimeMs { get; set; }
}

/// <summary>课后课堂报告的摘要草稿：只作可编辑草稿，保留来源页与时间点，不替换原始数据。</summary>
public sealed class ClassSummaryDraft
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Text { get; set; } = "";

    public bool Editable { get; set; } = true;

    public List<ClassSummaryReference> References { get; set; } = new();

    public long CreatedAtMs { get; set; }
}

public sealed class ClassSummarySource
{
    public string TeachingSessionId { get; set; } = "";

    public List<int> SlideIds { get; set; } = new();

    public List<int> BoardPageIndexes { get; set; } = new();

    public List<string> ExerciseIds { get; set; } = new();

    public List<string> ExportPaths { get; set; } = new();
}

/// <summary>AI 课堂摘要扩展点（P2 仅预留）。实现方必须返回可编辑草稿并保留来源引用。</summary>
public interface IAiClassSummaryService
{
    bool IsAvailable { get; }

    Task<ClassSummaryDraft> DraftAsync(ClassSummarySource source, CancellationToken cancellationToken);
}

public sealed class UnavailableAiClassSummaryService : IAiClassSummaryService
{
    public static UnavailableAiClassSummaryService Instance { get; } = new();

    public bool IsAvailable => false;

    public Task<ClassSummaryDraft> DraftAsync(
        ClassSummarySource source, CancellationToken cancellationToken)
        => Task.FromResult<ClassSummaryDraft>(null);
}