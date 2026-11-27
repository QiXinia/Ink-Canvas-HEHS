using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

/// <summary>A trusted native document module owns its pages and persistence.</summary>
public interface IDocumentWorkspace
{
    Window Window { get; }
    InkController Controller { get; }
    GestureSettings Gestures { get; }
    bool SupportsViewRotation { get; }
    bool CanEdit => true;
    event Action ControllerChanged;
    event Action InputBegan;
    void ApplySettings();
    void ClearCanvas(bool inkOnly) { if (inkOnly) Controller.ClearHandwriting(); else Controller.Clear(); }
    Task<bool> SaveAsync(bool saveAs = false);
    Task<bool> CloseAsync();
}

public interface IDocumentWorkspaceHost
{
    IAiDocumentBridge DocumentAi => null;
    Window Owner { get; }
    AppSettings Settings { get; }
    string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InkCanvas");
    Rect ToolbarScreenBounds { get; }
    BoardService Board { get; }
    void ConfigureWindow(Window window, int width = 1120, int height = 820);
    void ConfigureController(InkController controller);
    void SavePreferences() { }
    void OfferResume(string title, string description, Func<Task> resume, Func<Task> dismiss) { }
    void Attach(IDocumentWorkspace workspace);
    void Detach(IDocumentWorkspace workspace);
    void ShowBoard();
}

public interface IPdfWorkspaceService
{
    Task OpenAsync(string path = null);
    Task ImportToBoardAsync(string path = null);
}

public interface IPdfResumeService
{
    Task InitializeResumeAsync(bool allowRestore);
    Task FlushResumeAsync();
}
