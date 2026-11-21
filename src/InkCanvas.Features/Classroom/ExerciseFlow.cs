using InkCanvas.Core.AiAssistant;

namespace InkCanvas.Features.Classroom;

public enum ExerciseDifficulty
{
    Basic,
    Advanced,
}

/// <summary>AI 变式题草稿。先生成、教师预览编辑，确认后才允许贴入新画板页。</summary>
public sealed class VariantQuestionDraft
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = "";

    public ExerciseDifficulty Difficulty { get; set; }

    public string Text { get; set; } = "";

    public string Answer { get; set; } = "";

    public string KnowledgePoint { get; set; } = "";

    public string SourceSessionId { get; set; } = "";

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    public long CreatedAtMs { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Text);

    public VariantQuestionDraft Clone() => new()
    {
        Version = Version,
        Id = Id,
        Difficulty = Difficulty,
        Text = Text,
        Answer = Answer,
        KnowledgePoint = KnowledgePoint,
        SourceSessionId = SourceSessionId,
        SlideId = SlideId,
        SlideIndex = SlideIndex,
        CreatedAtMs = CreatedAtMs,
    };
}

/// <summary>「开始练习」动作清单：贴题 → 切页 → 启动倒计时 → 进入练习场景。</summary>
public sealed class PracticePlan
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ExerciseId { get; set; } = "";

    public int BoardPageIndex { get; set; } = -1;

    public int CountdownMinutes { get; set; } = 3;

    public string SceneId { get; set; } = "practice";

    public bool SaveSnapshotOnFinish { get; set; } = true;

    public string BookmarkName { get; set; } = "练习结束";
}

public static class ExerciseFlowPolicy
{
    public static AssistantMode ModeFor(ExerciseDifficulty difficulty)
        => difficulty == ExerciseDifficulty.Advanced ? AssistantMode.Solve : AssistantMode.Auto;

    /// <summary>变式题请求：携带合并后的拼题上下文，不覆盖原题。</summary>
    public static AssistantRequest BuildRequest(
        ProblemContext context,
        ExerciseDifficulty difficulty,
        string sessionId,
        AssistantTeachingMode teaching = AssistantTeachingMode.StepByStep)
    {
        IReadOnlyList<ProblemRegion> ordered = ProblemContextPolicy.Ordered(context);
        return new AssistantRequest
        {
            ImageBase64 = ProblemContextPolicy.PrimaryImage(context),
            Images = ordered,
            Viewport = new AssistantViewport { Rect = ProblemContextPolicy.CombinedAnchor(context) },
            Mode = ModeFor(difficulty),
            SessionId = sessionId ?? "",
            Teaching = teaching,
            Purpose = "variant",
            Difficulty = difficulty == ExerciseDifficulty.Advanced ? "advanced" : "basic",
        };
    }

    /// <summary>把 AI 文本切成题干与答案；没有明确标记时整段作为题干。</summary>
    public static VariantQuestionDraft Parse(
        string aiText,
        ExerciseDifficulty difficulty,
        string knowledgePoint = null,
        string sourceSessionId = null,
        int slideId = 0,
        int slideIndex = 0,
        long nowMs = 0)
    {
        var draft = new VariantQuestionDraft
        {
            Id = Guid.NewGuid().ToString("N"),
            Difficulty = difficulty,
            KnowledgePoint = knowledgePoint ?? "",
            SourceSessionId = sourceSessionId ?? "",
            SlideId = slideId,
            SlideIndex = slideIndex,
            CreatedAtMs = nowMs,
            Text = (aiText ?? "").Trim(),
        };

        foreach (string rawLine in (aiText ?? "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("答案：", StringComparison.Ordinal)
                || line.StartsWith("答案:", StringComparison.Ordinal))
            {
                draft.Answer = line.Substring(3).Trim();
                draft.Text = RemoveLine(draft.Text, line);
            }
            else if (line.StartsWith("知识点：", StringComparison.Ordinal)
                || line.StartsWith("知识点:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(draft.KnowledgePoint))
                    draft.KnowledgePoint = line.Substring(4).Trim();
                draft.Text = RemoveLine(draft.Text, line);
            }
        }

        draft.Text = draft.Text.Trim();
        return draft;
    }

    public static bool CanStart(VariantQuestionDraft draft) => draft is not null && draft.IsValid;

    /// <summary>开始练习：只贴到新页（newBoardPageIndex 由调用方先新建），不覆盖原题。</summary>
    public static PracticePlan Plan(
        VariantQuestionDraft draft,
        int newBoardPageIndex,
        int countdownMinutes,
        string sceneId = "practice")
    {
        return new PracticePlan
        {
            ExerciseId = draft?.Id ?? "",
            BoardPageIndex = Math.Max(0, newBoardPageIndex),
            CountdownMinutes = Math.Max(1, countdownMinutes),
            SceneId = string.IsNullOrWhiteSpace(sceneId) ? "practice" : sceneId,
            SaveSnapshotOnFinish = true,
            BookmarkName = "练习结束",
        };
    }

    public static IReadOnlyList<string> BuildPasteLines(VariantQuestionDraft draft)
    {
        var lines = new List<string>();
        if (draft is null) return lines;
        if (!string.IsNullOrWhiteSpace(draft.KnowledgePoint)) lines.Add("知识点：" + draft.KnowledgePoint);
        foreach (string line in (draft.Text ?? "").Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0) lines.Add(trimmed);
        }

        if (!string.IsNullOrWhiteSpace(draft.Answer)) lines.Add("参考答案：" + draft.Answer.Trim());
        return lines;
    }

    private static string RemoveLine(string text, string line)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var kept = new List<string>();
        foreach (string candidate in text.Split('\n'))
        {
            if (!string.Equals(candidate.Trim(), line, StringComparison.Ordinal)) kept.Add(candidate);
        }

        return string.Join("\n", kept);
    }
}

/// <summary>倒计时结束时的练习收尾数据；由 Shell 层落快照与书签。</summary>
public sealed class PracticeFinishRecord
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string ExerciseId { get; set; } = "";

    public int BoardPageIndex { get; set; } = -1;

    public string SnapshotPath { get; set; } = "";

    public string BookmarkName { get; set; } = "练习结束";

    public bool BookmarkCreated { get; set; }

    public long FinishedAtMs { get; set; }
}

public static class PracticeFinishPolicy
{
    /// <summary>结束时无论是否录制都要保留板书快照；录制中额外生成章节书签。</summary>
    public static PracticeFinishRecord Plan(PracticePlan plan, bool isRecording, bool snapshotSaved)
        => new()
        {
            ExerciseId = plan?.ExerciseId ?? "",
            BoardPageIndex = plan?.BoardPageIndex ?? -1,
            BookmarkName = plan?.BookmarkName ?? "练习结束",
            BookmarkCreated = isRecording,
            FinishedAtMs = 0,
        };

    public static bool ShouldNotify(bool isRecording) => isRecording;
}