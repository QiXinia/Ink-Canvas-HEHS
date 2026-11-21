using System;
using System.Collections.Generic;
using System.Linq;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Classroom;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AnnotationLayerTests
{
    private static AssistantSession SessionWithAnnotations(params int[] pointCounts)
    {
        var session = new AssistantSession
        {
            State = AssistantState.Done,
            TeachingMode = AssistantTeachingMode.StepByStep,
            RevealedSteps = 0,
        };

        foreach (int count in pointCounts)
        {
            var points = new List<IReadOnlyList<double>>();
            for (int i = 0; i < count; i++) points.Add(new List<double> { 0.1 * i, 0.2 * i });
            session.Annotations.Add(points);
            session.Steps.Add(new AssistantStep { Index = session.Steps.Count + 1, Text = "步骤" });
        }

        return session;
    }

    [Fact]
    public void Build_NoAnnotations_IsNotLocatable()
    {
        var session = new AssistantSession { State = AssistantState.Done };

        AnnotationLayer layer = AnnotationLayerPolicy.Build(
            session, new AssistantRect(0, 0, 100, 100));

        Assert.False(layer.CanLocate);
        Assert.Equal(AnnotationLayerPolicy.NoAnnotationReason, layer.Reason);
        Assert.Empty(layer.Items);
    }

    [Fact]
    public void Build_EmptyAnchor_RefusesToAnnotate()
    {
        AssistantSession session = SessionWithAnnotations(3, 3);

        AnnotationLayer layer = AnnotationLayerPolicy.Build(session, default);

        Assert.False(layer.CanLocate);
        Assert.Equal(AnnotationLayerPolicy.NoAnchorReason, layer.Reason);
    }

    [Fact]
    public void Build_ValidPoints_ProducesLocatableItems()
    {
        AssistantSession session = SessionWithAnnotations(3, 2);

        AnnotationLayer layer = AnnotationLayerPolicy.Build(
            session, new AssistantRect(10, 10, 210, 160));

        Assert.True(layer.CanLocate);
        Assert.Equal(2, layer.Items.Count);
        Assert.All(layer.Items, item => Assert.True(item.Locatable));
        Assert.All(layer.Items, item => Assert.True(item.Visible));
        Assert.Equal("", layer.Reason);
    }

    [Fact]
    public void Build_DegenerateAnnotation_MarksSingleItemUnlocatable()
    {
        AssistantSession session = SessionWithAnnotations(1, 3);

        AnnotationLayer layer = AnnotationLayerPolicy.Build(
            session, new AssistantRect(0, 0, 100, 100));

        Assert.True(layer.CanLocate);
        Assert.False(layer.Items[0].Locatable);
        Assert.True(layer.Items[1].Locatable);
    }

    [Fact]
    public void SyncVisibility_FollowsRevealedSteps()
    {
        AssistantSession session = SessionWithAnnotations(3, 3, 3);
        AnnotationLayer layer = AnnotationLayerPolicy.Build(
            session, new AssistantRect(0, 0, 100, 100));

        session.RevealedSteps = 1;
        AnnotationLayerPolicy.SyncVisibility(layer, session);
        Assert.Equal(1, layer.VisibleCount);

        session.RevealedSteps = 2;
        AnnotationLayerPolicy.SyncVisibility(layer, session);
        Assert.Equal(2, layer.VisibleCount);
    }

    [Fact]
    public void SyncVisibility_FullMode_RevealsAll()
    {
        AssistantSession session = SessionWithAnnotations(2, 2);
        session.TeachingMode = AssistantTeachingMode.Full;
        AnnotationLayer layer = AnnotationLayerPolicy.Build(
            session, new AssistantRect(0, 0, 100, 100));

        AnnotationLayerPolicy.SyncVisibility(layer, session);

        Assert.Equal(2, layer.VisibleCount);
    }

    [Fact]
    public void SetAllVisibleFalse_HidesEveryItem()
    {
        AssistantSession session = SessionWithAnnotations(2, 2);
        AnnotationLayer layer = AnnotationLayerPolicy.Build(
            session, new AssistantRect(0, 0, 100, 100));

        AnnotationLayerPolicy.SetAllVisible(layer, false);
        Assert.Equal(0, layer.VisibleCount);

        AnnotationLayerPolicy.SetAllVisible(layer, true);
        Assert.Equal(2, layer.VisibleCount);
    }
}

public sealed class SlideContextTests
{
    [Fact]
    public void ToPrompt_ContainsTitlePageTextAndSelectionPriority()
    {
        SlideContext context = SlideContextPolicy.Build(
            "函数与导数", 42, 3, "已知 f(x)=x^2", "上一页：集合", "下一页：积分", 1);

        string prompt = context.ToPrompt();

        Assert.Contains("函数与导数", prompt);
        Assert.Contains("第 3 页", prompt);
        Assert.Contains("动画阶段 1", prompt);
        Assert.Contains("f(x)=x^2", prompt);
        Assert.Contains("以用户框选的图片与墨迹为准", prompt);
    }

    [Fact]
    public void WithSelection_KeepsCurrentTextAndAddsSelection()
    {
        SlideContext context = SlideContextPolicy.Build("标题", 1, 1, "整页文本");

        SlideContextPolicy.WithSelection(context, "用户框选的补充条件");

        Assert.Equal("整页文本", context.CurrentText);
        Assert.Equal("用户框选的补充条件", context.SelectionText);
        Assert.True(context.HasText);
    }

    [Fact]
    public void Build_TruncatesLongCurrentText()
    {
        string longText = new string('题', 5000);
        SlideContext context = SlideContextPolicy.Build("标题", 1, 1, longText);

        string prompt = context.ToPrompt();

        Assert.Contains("…", prompt);
        Assert.True(prompt.Length < 5000);
    }

    [Fact]
    public void IsUsable_FalseWhenNoText()
    {
        SlideContext context = SlideContextPolicy.Build("", 1, 1, "");

        Assert.False(SlideContextPolicy.IsUsable(context));
    }
}

public sealed class AiResponseGateTests
{
    [Fact]
    public void Begin_IncrementsGenerationAndTracksSession()
    {
        var gate = new AiResponseGate();

        int first = gate.Begin("a");
        int second = gate.Begin("b");

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.False(gate.IsCurrent(first, "a"));
        Assert.True(gate.IsCurrent(second, "b"));
        Assert.False(gate.IsCurrent(second, "a"));
    }

    [Fact]
    public void Invalidate_MakesCurrentGenerationStale()
    {
        var gate = new AiResponseGate();
        int generation = gate.Begin("a");

        gate.Invalidate();

        Assert.False(gate.IsCurrent(generation, "a"));
    }
}

public sealed class TeachingSessionTests
{
    [Fact]
    public void Link_RecordsAiAndExerciseWithSlideAnchor()
    {
        TeachingSession session = TeachingSession.Create("第一节课", 100);

        session.Link("ai", "ai-1", 7, 2, 0, nowMs: 110);
        session.Link("exercise", "ex-1", 7, 2, 3, nowMs: 120);

        Assert.Contains("ai-1", session.AiSessionIds);
        Assert.Contains("ex-1", session.ExerciseIds);
        Assert.Equal(2, session.Links.Count);
        Assert.Equal(2, session.LinksForSlide(7).Count);
        Assert.True(session.HasLink("ai", "ai-1"));
        Assert.Equal(120, session.UpdatedAtMs);
    }

    [Fact]
    public void SlideKey_DistinguishesAnimationSteps()
    {
        Assert.NotEqual(
            TeachingSession.SlideKey(3, 2, 0),
            TeachingSession.SlideKey(3, 2, 1));
        Assert.Equal("slide:3@2#1", TeachingSession.SlideKey(3, 2, 1));
    }

    [Fact]
    public void SetCurrentSlide_UpdatesOnlyOnChange()
    {
        TeachingSession session = TeachingSession.Create("课", 10);
        session.SetCurrentSlide(4, 1, 0, 20);
        Assert.Equal(20, session.UpdatedAtMs);

        session.SetCurrentSlide(4, 1, 0, 30);
        Assert.Equal(20, session.UpdatedAtMs);

        session.SetCurrentSlide(4, 1, 2, 40);
        Assert.Equal(40, session.UpdatedAtMs);
    }

    [Fact]
    public void Serialize_RoundTripsLinksAndVersions()
    {
        TeachingSession session = TeachingSession.Create("课", 10);
        session.Link("ai", "ai-9", 2, 1, -1, nowMs: 15);
        session.RecordExport(@"C:\out\handout.pdf", 20);

        TeachingSession restored = TeachingSession.Deserialize(session.Serialize());

        Assert.NotNull(restored);
        Assert.Equal(session.Id, restored.Id);
        Assert.Single(restored.Links);
        Assert.Contains(@"C:\out\handout.pdf", restored.ExportPaths);
        Assert.True(restored.Version >= 1);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        Assert.Null(TeachingSession.Deserialize("{not json"));
        Assert.Null(TeachingSession.Deserialize(""));
    }
}

public sealed class ProblemContextSerializationTests
{
    [Fact]
    public void Serialize_RoundTripsRegionsAndAnchors()
    {
        var context = new ProblemContext { SlideId = 5, SlideIndex = 2 };
        ProblemContextPolicy.Add(context, new ProblemRegion
        {
            ImageBase64 = "abc",
            Anchor = new AssistantRect(1, 2, 3, 4),
            Source = "selection",
        }, nowMs: 10);

        ProblemContext restored = ProblemContext.Deserialize(context.Serialize());

        Assert.NotNull(restored);
        Assert.Equal(5, restored.SlideId);
        Assert.Single(restored.Regions);
        Assert.Equal("abc", restored.Regions[0].ImageBase64);
        Assert.Equal(3, restored.Regions[0].Anchor.Right);
        Assert.Equal(context.SessionId, restored.SessionId);
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        Assert.Null(ProblemContext.Deserialize("oops"));
    }

    [Fact]
    public void DescribeSlideChange_SamePageDifferentAnimation_Reminds()
    {
        var context = new ProblemContext { SlideId = 7, SlideIndex = 2, AnimationStep = 1 };
        ProblemContextPolicy.Add(context, new ProblemRegion
        {
            ImageBase64 = "img",
            Anchor = new AssistantRect(0, 0, 10, 10),
        });

        string sameStep = ProblemContextPolicy.DescribeSlideChange(context, 7, 2, 1);
        string nextStep = ProblemContextPolicy.DescribeSlideChange(context, 7, 2, 2);

        Assert.Null(sameStep);
        Assert.NotNull(nextStep);
        Assert.Contains("动画阶段", nextStep);
    }

    [Fact]
    public void Serialize_KeepsAnimationStep()
    {
        var context = new ProblemContext { SlideId = 3, SlideIndex = 1, AnimationStep = 4 };
        ProblemContextPolicy.Add(context, new ProblemRegion
        {
            ImageBase64 = "img",
            Anchor = new AssistantRect(0, 0, 10, 10),
        });

        ProblemContext restored = ProblemContext.Deserialize(context.Serialize());

        Assert.Equal(4, restored.AnimationStep);
    }
}