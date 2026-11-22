namespace InkCanvas.Host.Contracts;

public interface IStylusInputHost
{
    string DataDirectory { get; }
    ICanvasToolsHost Canvas { get; }
    bool CanHandleShortcuts { get; }
    bool CanLearnShortcuts { get; }
    bool CanHandlePointer(InkCanvas.Ink.InkController controller);
}
