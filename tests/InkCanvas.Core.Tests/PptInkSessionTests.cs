using System.Reflection;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PptInkSessionTests
{
    [Fact]
    public void RecreatedOfficeProxyDoesNotReloadUnchangedPage()
    {
        using var interop = new PptInteropService();
        using var session = new PptInkSession(interop);
        int applications = 0;
        session.ApplyCurrentInk = _ => applications++;
        var first = Snapshot("proxy-one", 1);
        Process(session, first);
        for (int index = 0; index < 20; index++) Process(session, first with
        { Presentation = first.Presentation with { Identity = "proxy-" + index } });
        Assert.Equal(1, applications);
    }

    [Fact]
    public void RealSlideChangeStillRestoresItsOwnInkAfterProxyChanges()
    {
        using var interop = new PptInteropService();
        using var session = new PptInkSession(interop);
        IReadOnlyList<InkStroke> current = Array.Empty<InkStroke>();
        session.ApplyCurrentInk = strokes => current = strokes;
        session.CaptureCurrentInk = () => current;
        Process(session, Snapshot("proxy-one", 1));
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.AddPoint(20, 30); stroke.Complete();
        current = new[] { stroke };
        Process(session, Snapshot("proxy-two", 2));
        Assert.Empty(current);
        Process(session, Snapshot("proxy-three", 1));
        Assert.Equal(stroke.Id, Assert.Single(current).Id);
        Assert.NotSame(stroke, current[0]);
    }

    private static PptSnapshot Snapshot(string identity, int index) => new()
    {
        State = PptConnectionState.SlideShow,
        Presentation = new(identity, "Lesson", "C:\\Lessons\\Lesson.pptx", true, 2, 960, 540, "PowerPoint.Application"),
        Slide = new(index + 255, index, "Slide " + index, false, false, 0),
        WindowHandle = 1,
        ShowPosition = index,
    };

    private static void Process(PptInkSession session, PptSnapshot snapshot)
        => typeof(PptInkSession).GetMethod("ProcessTransition", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(session, new object[] { snapshot });
}
