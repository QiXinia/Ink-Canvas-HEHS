using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;
using InkCanvas.Core.Shapes;
using System.Numerics;
using Xunit;

namespace InkCanvas.Core.Tests;

public class ReplayClockTests
{
    private static ReplayClock Clock(float ms = 1000f) => new(TimeSpan.FromMilliseconds(ms));

    [Fact]
    public void StartsPausedAtZero()
    {
        var c = Clock();
        Assert.False(c.IsPlaying);
        Assert.Equal(0f, c.ElapsedMs);
    }

    [Fact]
    public void AdvanceDoesNothingWhilePaused()
    {
        var c = Clock();
        c.Advance(500f);
        Assert.Equal(0f, c.ElapsedMs);
    }

    [Fact]
    public void AdvanceAccumulatesWhenPlaying()
    {
        var c = Clock();
        c.Play();
        c.Advance(100f);
        c.Advance(150f);
        Assert.Equal(250f, c.ElapsedMs, 3);
    }

    [Fact]
    public void SpeedScalesAdvance()
    {
        var c = Clock();
        c.Speed = 2f;
        c.Play();
        c.Advance(100f);
        Assert.Equal(200f, c.ElapsedMs, 3);
    }

    [Fact]
    public void StopsAtEndAndReportsComplete()
    {
        var c = Clock(300f);
        c.Play();
        c.Advance(1000f);
        Assert.Equal(300f, c.ElapsedMs, 3);
        Assert.False(c.IsPlaying);
        Assert.True(c.IsComplete);
    }

    [Fact]
    public void SeekClampsToRange()
    {
        var c = Clock(500f);
        c.Seek(-50f);
        Assert.Equal(0f, c.ElapsedMs);
        c.Seek(9999f);
        Assert.Equal(500f, c.ElapsedMs);
    }

    [Fact]
    public void ProgressIsFractionOfDuration()
    {
        var c = Clock(400f);
        c.Seek(100f);
        Assert.Equal(0.25f, c.Progress, 3);
    }

    [Fact]
    public void ToggleFlipsPlaying()
    {
        var c = Clock();
        c.Toggle();
        Assert.True(c.IsPlaying);
        c.Toggle();
        Assert.False(c.IsPlaying);
    }

    [Fact]
    public void PlayingAfterCompletionRestartsFromBeginning()
    {
        var c = Clock(200f);
        c.Play();
        c.Advance(500f);
        Assert.True(c.IsComplete);
        c.Play();
        Assert.Equal(0f, c.ElapsedMs);
        Assert.True(c.IsPlaying);
    }
}

/// <summary>Tests for the pre-existing replay session, which previously had no coverage.</summary>
public class InkReplaySessionTests
{
    [Fact]
    public void ReusableFrameCanSeekBackWithoutMutatingSource()
    {
        var source = Stroke(40, 7);
        var original = source.Points.ToArray();
        var session = new InkReplaySession(new[] { source }, 10);
        var frame = new List<InkStroke>();
        session.WriteVisibleAt(0, frame); Assert.Empty(frame);
        session.WriteVisibleAt(200, frame); var partial = Assert.Single(frame); Assert.Equal(20, partial.Count);
        session.WriteVisibleAt(300, frame); Assert.Same(partial, Assert.Single(frame)); Assert.Equal(30, partial.Count);
        session.WriteVisibleAt(100, frame); Assert.Equal(10, Assert.Single(frame).Count);
        session.WriteVisibleAt(400, frame); Assert.Same(source, Assert.Single(frame));
        Assert.Equal(original, source.Points);
    }
    private static InkStroke Stroke(int points, float width = 5f)
    {
        var s = new InkStroke(new StrokeAttributes { BaseWidth = width, TaperKind = TaperKind.None });
        for (int i = 0; i < points; i++) s.AddPoint(i * 3f, i * 2f);
        s.Complete();
        return s;
    }

    [Fact]
    public void NothingVisibleBeforeStart()
    {
        var session = new InkReplaySession(new[] { Stroke(10), Stroke(10) }, msPerPoint: 10f);
        Assert.Empty(session.VisibleAt(-1f));
    }

    [Fact]
    public void EverythingVisibleAtDuration()
    {
        var strokes = new[] { Stroke(10), Stroke(10) };
        var session = new InkReplaySession(strokes, msPerPoint: 10f);
        var visible = session.VisibleAt((float)session.Duration.TotalMilliseconds);
        Assert.Equal(2, visible.Count);
        Assert.Equal(10, visible[0].Count);
        Assert.Equal(10, visible[1].Count);
    }

    [Fact]
    public void VisibleCountNeverDecreases()
    {
        var session = new InkReplaySession(new[] { Stroke(20), Stroke(20) }, msPerPoint: 10f);
        int previous = 0;
        for (float t = 0; t <= 400f; t += 10f)
        {
            int total = session.VisibleAt(t).Sum(s => s.Count);
            Assert.True(total >= previous, $"visible point count dropped at t={t}");
            previous = total;
        }
    }

    [Fact]
    public void ReplayPreservesStoredWidths()
    {
        var session = new InkReplaySession(new[] { Stroke(10, 7f) }, msPerPoint: 10f);
        var partial = session.VisibleAt(50f).Single();
        Assert.All(partial.Points, p => Assert.Equal(7f, p.Width));
    }

    [Fact]
    public void TwoPointDashGrowsContinuouslyWithoutHoldingADot()
    {
        var source = ShapeGenerator.Generate(ShapeKind.DashedLine, Vector2.Zero, new(100, 0),
            new StrokeAttributes { BaseWidth = 7, TaperKind = TaperKind.None });
        var original = source.Select(s => s.Points.ToArray()).ToArray();
        var session = new InkReplaySession(source);
        var frame = new List<InkStroke>();

        session.WriteVisibleAt(5, frame);
        var partial = Assert.Single(frame);
        Assert.Equal(2, partial.Count);
        Assert.Equal(1.25f, partial.Points[^1].X, 4);
        Assert.All(partial.Points, point => Assert.Equal(7, point.Width));
        long version = partial.GeometryVersion;
        session.WriteVisibleAt(10, frame);
        Assert.Same(partial, Assert.Single(frame));
        Assert.Equal(2.5f, partial.Points[^1].X, 4);
        Assert.True(partial.GeometryVersion > version);
        version = partial.GeometryVersion;
        session.WriteVisibleAt(10, frame);
        Assert.Equal(version, partial.GeometryVersion);
        session.WriteVisibleAt(2, frame);
        Assert.Equal(.5f, Assert.Single(frame).Points[^1].X, 4);
        Assert.Equal(2.5f, Assert.Single(session.VisibleAt(10)).Points[^1].X, 4);

        session.WriteVisibleAt((float)session.Duration.TotalMilliseconds, frame);
        Assert.Equal(source.Count, frame.Count);
        for (int i = 0; i < source.Count; i++)
        {
            Assert.Same(source[i], frame[i]);
            Assert.Equal(original[i], source[i].Points);
        }
    }

    [Fact]
    public void ShapeSpeedUsesDistanceRegardlessOfSamplingDensity()
    {
        var sparse = Shape(new(0, 0, .2f, 4, 0), new(30, 0, .8f, 10, 120));
        var uneven = Shape(new(0, 0, .2f, 4, 0), new(1, 0, .22f, 4.2f, 4),
            new(1, 0, .22f, 4.2f, 4), new(30, 0, .8f, 10, 120));
        var sparseReplay = new InkReplaySession(new[] { sparse });
        var unevenReplay = new InkReplaySession(new[] { uneven });
        Assert.Equal(sparseReplay.Duration, unevenReplay.Duration);
        Assert.Equal(120, sparseReplay.Duration.TotalMilliseconds);
        var expected = Assert.Single(sparseReplay.VisibleAt(60)).Points[^1];
        var actual = Assert.Single(unevenReplay.VisibleAt(60)).Points[^1];
        Assert.Equal(15, actual.X, 4);
        Assert.Equal(expected.Width, actual.Width, 4);
        Assert.Equal(expected.Pressure, actual.Pressure, 4);
        Assert.Equal(expected.TimeMs, actual.TimeMs, 4);
    }

    [Fact]
    public void CurvedDashFollowsItsPolylineAndPreservesSnapshotFrames()
    {
        var strokes = ShapeGenerator.Generate(ShapeKind.DashedCircleFromCenter, new(100, 100), new(200, 180),
            new StrokeAttributes { BaseWidth = 4 });
        var first = strokes[0];
        var session = new InkReplaySession(strokes);
        float duration = first.ArcLength() / 3 * 12;
        var snapshot = Assert.Single(session.VisibleAt(duration * .25f));
        var originalSnapshot = snapshot.Points.ToArray();
        var frame = new List<InkStroke>();
        for (int i = 1; i < 10; i++)
        {
            session.WriteVisibleAt(duration * i / 10, frame);
            var partial = Assert.Single(frame);
            Assert.Equal(first.ArcLength() * i / 10, partial.ArcLength(), 3);
            Assert.InRange(first.DistanceTo(partial.Points[^1].Position), 0, .0001f);
        }
        Assert.Equal(originalSnapshot, snapshot.Points);
        Assert.Empty(session.VisibleAt(0));
        session.WriteVisibleAt(0, frame);
        Assert.Empty(frame);
    }

    [Theory]
    [InlineData(ShapeKind.DashedLine)]
    [InlineData(ShapeKind.DashedCircle)]
    [InlineData(ShapeKind.Cylinder)]
    [InlineData(ShapeKind.Cone)]
    public void ShapeReplayKeepsDashGapsAndRestoresExactFinalGeometry(ShapeKind kind)
    {
        var source = ShapeGenerator.Generate(kind, new(20, 20), new(200, 160),
            new StrokeAttributes { BaseWidth = 6, TaperKind = TaperKind.None });
        var original = source.Select(s => s.Points.ToArray()).ToArray();
        var session = new InkReplaySession(source.Select(s => s.Clone()));
        var frame = new List<InkStroke>();
        float duration = (float)session.Duration.TotalMilliseconds;
        foreach (float progress in new[] { .01f, .1f, .5f, .95f, .1f, 1f })
        {
            session.WriteVisibleAt(duration * progress, frame);
            Assert.InRange(frame.Count, 1, source.Count);
            for (int i = 0; i < frame.Count; i++)
            {
                Assert.Equal(source[i].Points[0], frame[i].Points[0]);
                Assert.InRange(source[i].DistanceTo(frame[i].Points[^1].Position), 0, .001f);
                if (i < frame.Count - 1 || progress == 1) Assert.Equal(original[i], frame[i].Points);
            }
        }
        for (int i = 0; i < source.Count; i++) Assert.Equal(original[i], source[i].Points);
    }

    private static InkStroke Shape(params InkPoint[] points)
    {
        var stroke = new InkStroke(new StrokeAttributes()) { IsRecognizedShape = true };
        stroke.RestorePoints(points);
        stroke.Complete();
        return stroke;
    }
}
