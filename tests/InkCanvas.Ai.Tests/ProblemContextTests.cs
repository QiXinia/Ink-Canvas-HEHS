using System;
using System.Linq;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Platform.Ai;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class ProblemContextTests
{
    private static ProblemRegion Region(int seed)
        => new()
        {
            Anchor = new AssistantRect(seed * 10, seed * 10, seed * 10 + 100, seed * 10 + 80),
            ImageBase64 = "img-" + seed,
            Source = "selection",
        };

    [Fact]
    public void Add_AssignsIdOrderAndSession()
    {
        var context = new ProblemContext();

        var first = ProblemContextPolicy.Add(context, Region(1), nowMs: 100);
        var second = ProblemContextPolicy.Add(context, Region(2), nowMs: 200);

        Assert.Equal("1", first.Id);
        Assert.Equal(1, first.Order);
        Assert.Equal("2", second.Id);
        Assert.Equal(2, second.Order);
        Assert.Equal(2, context.Count);
        Assert.False(string.IsNullOrEmpty(context.SessionId));
        Assert.Equal(200, context.UpdatedAtMs);
    }

    [Fact]
    public void Add_IgnoresEmptyRegion()
    {
        var context = new ProblemContext();

        Assert.Null(ProblemContextPolicy.Add(context, new ProblemRegion()));
        Assert.True(context.IsEmpty);
        Assert.Empty(context.SessionId);
    }

    [Fact]
    public void Add_DuplicateIdGetsNextFreeId()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1), "a");

        var duplicate = ProblemContextPolicy.Add(context, Region(2), "a");

        Assert.NotEqual("a", duplicate.Id);
        Assert.Single(context.Regions, r => r.Id == "a");
    }

    [Fact]
    public void Remove_ReindexesRemainingRegions()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1), "a");
        ProblemContextPolicy.Add(context, Region(2), "b");
        ProblemContextPolicy.Add(context, Region(3), "c");

        Assert.True(ProblemContextPolicy.Remove(context, "b"));
        Assert.Equal(2, context.Count);
        Assert.Equal(1, context.Regions[0].Order);
        Assert.Equal(2, context.Regions[1].Order);
        Assert.False(ProblemContextPolicy.Remove(context, "missing"));
    }

    [Fact]
    public void Move_ReordersAndBlocksOutOfBounds()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1), "a");
        ProblemContextPolicy.Add(context, Region(2), "b");

        Assert.True(ProblemContextPolicy.Move(context, "b", -1));
        Assert.Equal("b", context.Regions[0].Id);
        Assert.Equal(1, context.Regions[0].Order);
        Assert.Equal(2, context.Regions[1].Order);

        Assert.False(ProblemContextPolicy.Move(context, "b", -1));
        Assert.True(ProblemContextPolicy.Move(context, "b", 1));
        Assert.Equal("a", context.Regions[0].Id);
        Assert.Equal("b", context.Regions[1].Id);
        Assert.False(ProblemContextPolicy.Move(context, "b", 1));
    }

    [Fact]
    public void CombinedAnchor_UnionsAllRegions()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1));
        ProblemContextPolicy.Add(context, Region(2));

        AssistantRect anchor = ProblemContextPolicy.CombinedAnchor(context);

        Assert.Equal(10, anchor.Left);
        Assert.Equal(10, anchor.Top);
        Assert.Equal(120, anchor.Right);
        Assert.Equal(100, anchor.Bottom);
    }

    [Fact]
    public void CombinedAnchor_IgnoresEmptyAnchors()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, new ProblemRegion { ImageBase64 = "only-image" });

        Assert.True(ProblemContextPolicy.CombinedAnchor(context).IsEmpty);
    }

    [Fact]
    public void PrimaryImage_UsesFirstOrderedNonEmptyImage()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1));
        ProblemContextPolicy.Add(context, Region(2));

        Assert.Equal("img-1", ProblemContextPolicy.PrimaryImage(context));

        ProblemContextPolicy.Move(context, "2", -1);
        Assert.Equal("img-2", ProblemContextPolicy.PrimaryImage(context));
    }

    [Fact]
    public void DescribeSlideChange_OnlyWarnsForRealSlideChange()
    {
        var context = new ProblemContext { SlideId = 3, SlideIndex = 2 };
        ProblemContextPolicy.Add(context, Region(1));

        Assert.Null(ProblemContextPolicy.DescribeSlideChange(context, 3, 2));
        Assert.Null(ProblemContextPolicy.DescribeSlideChange(context, 0, 0));

        string notice = ProblemContextPolicy.DescribeSlideChange(context, 9, 5);
        Assert.Contains("第 5 页", notice);
    }

    [Fact]
    public void Ordered_ReturnsRegionsByOrder()
    {
        var context = new ProblemContext
        {
            Regions =
            {
                new ProblemRegion { Id = "b", Order = 2, ImageBase64 = "b" },
                new ProblemRegion { Id = "a", Order = 1, ImageBase64 = "a" },
            },
        };

        Assert.Equal(new[] { "a", "b" }, ProblemContextPolicy.Ordered(context).Select(r => r.Id));
    }

    [Fact]
    public void Payload_AppendsImagesArray()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1), "r1");
        ProblemContextPolicy.Add(context, Region(2), "r2");

        var request = new AssistantRequest
        {
            ImageBase64 = ProblemContextPolicy.PrimaryImage(context),
            Images = context.Regions,
            Mode = AssistantMode.Auto,
            SessionId = context.SessionId,
        };

        string payload = AiClient.BuildPayload(request, "");

        Assert.Contains("\"images\":[", payload);
        Assert.Contains("\"id\":\"r1\"", payload);
        Assert.Contains("\"imageBase64\":\"img-1\"", payload);
        Assert.Contains("\"imageBase64\":\"img-2\"", payload);
    }

    [Fact]
    public void Payload_InkOnlyStageStillSerializesImagesWithoutPayloadData()
    {
        var context = new ProblemContext();
        ProblemContextPolicy.Add(context, Region(1), "r1");

        var request = new AssistantRequest
        {
            Stage = AssistantStage.InkOnly,
            Images = context.Regions,
        };

        string payload = AiClient.BuildPayload(request, "");

        Assert.Contains("\"id\":\"r1\"", payload);
        Assert.Contains("\"imageBase64\":\"\"", payload);
    }
}