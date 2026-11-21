namespace InkCanvas.Core.Classroom;

public enum StudentInteractionKind
{
    Poll,
    ChoiceQuestion,
    RaiseHand,
    SubmitAnswer,
}

public enum StudentInteractionState
{
    Offline,
    WaitingForStudents,
    Closed,
}

public sealed class StudentInteractionRequest
{
    public StudentInteractionKind Kind { get; set; }

    public string Prompt { get; set; } = "";

    public List<string> Options { get; set; } = new();

    public long OpenedAtMs { get; set; }

    public long ClosedAtMs { get; set; }
}

public sealed class StudentResponse
{
    public string StudentId { get; set; } = "";

    public StudentInteractionKind Kind { get; set; }

    public string Choice { get; set; } = "";

    public string Answer { get; set; } = "";

    public long SubmittedAtMs { get; set; }
}

public sealed class StudentInteractionSnapshot
{
    public StudentInteractionState State { get; set; } = StudentInteractionState.Offline;

    public StudentInteractionRequest Request { get; set; }

    public List<StudentResponse> Responses { get; set; } = new();

    public string Reason { get; set; } = "";
}

/// <summary>
/// 学生互动扩展点（P2 仅预留）。本地授课不依赖实现方；离线时应返回
/// <see cref="StudentInteractionState.Offline"/>，不得阻塞白板、批注与 AI 之外的本地工具。
/// </summary>
public interface IStudentInteractionService
{
    bool IsAvailable { get; }

    StudentInteractionSnapshot Current { get; }

    StudentInteractionSnapshot Open(StudentInteractionRequest request);

    void Close();
}

public sealed class UnavailableStudentInteractionService : IStudentInteractionService
{
    public static UnavailableStudentInteractionService Instance { get; } = new();

    public bool IsAvailable => false;

    public StudentInteractionSnapshot Current { get; } = new()
    {
        State = StudentInteractionState.Offline,
        Reason = "未接入学生端或局域网服务；本地授课与板书不受影响。",
    };

    public StudentInteractionSnapshot Open(StudentInteractionRequest request) => Current;

    public void Close()
    {
    }
}