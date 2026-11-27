using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;

namespace InkCanvas.Host.Contracts;

public sealed record AssistantSource(string Presentation, int SlideId, int SlideIndex, int AnimationStep);

/// <summary>Canvas and classroom capabilities supplied to the optional native AI plugin.</summary>
public interface IAiAssistantHost
{
    AppSettings Settings { get; }
    InkController ActiveController { get; }
    BoardService Board { get; }
    BoardService CaptureBoard => Board;
    bool BoardOpen { get; }
    double DpiScale { get; }
    ScreenRect OverlayBounds { get; }
    ScreenRect? AssistantPlacementBounds => null;
    AssistantSource Source { get; }
    bool IsClassroomPresentation { get; }
    IAssistantToolRegistry Tools => null;
    SlideContext GetSlideContext(int slideId, int slideIndex);
    void ConfigureWindow(Window window);
    void ConfigureEdgeHandle(Window window, bool topmost) { }
    void SaveSettings();
    void ShowBoard();
    Task NotifyAsync(string title, string message);
}

public interface IAiAssistantService
{
    bool IsOpen { get; }
    void Open();
    Task OpenRegionAsync(ScreenRect rect);
    void Review(int slideId, int slideIndex, int animationStep, string text);
    void NotifySlideChanged(int slideId, int slideIndex, int animationStep);
    void NotifyBoardChanged();
    void SetCanvasVisible(bool visible) { }
}
