using System;
using System.Collections.Generic;
using System.Linq;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.P0.Tests;

public sealed class SlideContentIndexTests
{
    private static readonly PptPresentationInfo Info =
        new("id-1", "三角形", "D:\\课件\\三角形.pptx", true, 3, 960, 540, "PowerPoint.Application");

    private static PptPresentationDetails Details(params (int Id, int Index, string Name)[] slides)
    {
        var list = slides
            .Select(s => new PptSlideInfo(s.Id, s.Index, s.Name, null, null, null))
            .ToList();
        return new PptPresentationDetails(Info, list, null);
    }

    private static InkStroke Stroke()
    {
        var stroke = new InkStroke(new StrokeAttributes());
        stroke.RestorePoints(new[]
        {
            new InkPoint(0, 0, 1, 1, 0),
            new InkPoint(10, 10, 1, 1, 1),
        });
        return stroke;
    }

    private static PptInkArchive Archive(params (int Id, int Index, int Count)[] slides)
    {
        var stored = slides
            .Select(s => new PptStoredSlide(
                s.Id,
                s.Index,
                Enumerable.Range(0, s.Count).Select(_ => Stroke()).ToList()))
            .ToList();
        return new PptInkArchive(Info, null, null, stored);
    }

    [Fact]
    public void Build_MergesSlideOrderAndInk()
    {
        var details = Details((11, 1, "引入"), (12, 2, "新授"), (13, 3, "练习"));
        var archive = Archive((11, 1, 2), (13, 3, 1));

        SlideContentIndex index = SlideContentIndex.Build(details, archive);

        Assert.Equal(3, index.Count);
        Assert.Equal(new[] { 11, 12, 13 }, index.Slides.Select(s => s.SlideId));
        Assert.True(index.Slides[0].HasInk);
        Assert.Equal(2, index.Slides[0].InkStrokeCount);
        Assert.False(index.Slides[1].HasInk);
        Assert.True(index.Slides[2].HasInk);
        Assert.Equal("id-1", index.Identity);
        Assert.Equal(SlideContentIndex.CurrentVersion, index.Version);
    }

    [Fact]
    public void Build_LegacySlidesMatchByIndex()
    {
        var details = Details((21, 1, "第一页"), (22, 2, "第二页"));
        var archive = Archive((0, 2, 1));

        SlideContentIndex index = SlideContentIndex.Build(details, archive);

        Assert.False(index.Slides[0].HasInk);
        Assert.True(index.Slides[1].HasInk);
    }

    [Fact]
    public void Build_KeepsFavoriteAiThumbnailAndText()
    {
        var previous = SlideContentIndex.Build(Details((31, 1, "A"), (32, 2, "B")), Archive((31, 1, 1)));
        previous.Slides[0].Favorite = true;
        previous.Slides[0].HasAi = true;
        previous.Slides[0].ThumbnailPath = "thumb-31.png";
        previous.Slides[0].Text = "标题：全等三角形";

        SlideContentIndex rebuilt = SlideContentIndex.Build(
            Details((31, 1, "A"), (32, 2, "B")),
            Archive((31, 1, 1)),
            previous);

        Assert.True(rebuilt.Slides[0].Favorite);
        Assert.True(rebuilt.Slides[0].HasAi);
        Assert.Equal("thumb-31.png", rebuilt.Slides[0].ThumbnailPath);
        Assert.Equal("标题：全等三角形", rebuilt.Slides[0].Text);
    }

    [Fact]
    public void Build_FollowsSlideIdWhenPagesReorder()
    {
        var first = SlideContentIndex.Build(Details((41, 1, "A"), (42, 2, "B")), Archive());
        first.Slides[0].Favorite = true;

        SlideContentIndex reordered = SlideContentIndex.Build(
            Details((42, 1, "B"), (41, 2, "A")),
            Archive(),
            first);

        Assert.False(reordered.Slides[0].Favorite);
        Assert.True(reordered.Slides[1].Favorite);
        Assert.Equal(42, reordered.Slides[0].SlideId);
    }

    [Fact]
    public void Build_RemovedSlideDropsItsFlags()
    {
        var previous = SlideContentIndex.Build(Details((51, 1, "A"), (52, 2, "B")), Archive());
        previous.Slides[1].Favorite = true;

        SlideContentIndex rebuilt = SlideContentIndex.Build(Details((51, 1, "A")), Archive(), previous);

        Assert.Single(rebuilt.Slides);
        Assert.All(rebuilt.Slides, s => Assert.False(s.Favorite));
    }

    [Fact]
    public void Filter_SeparatesAnnotatedAndFavorite()
    {
        var index = SlideContentIndex.Build(
            Details((61, 1, "A"), (62, 2, "B"), (63, 3, "C")),
            Archive((61, 1, 1), (63, 3, 2)));
        index.SetFavorite(62, true);

        Assert.Equal(3, index.Filter(SlideFilter.All).Count);
        Assert.Equal(new[] { 61, 63 }, index.Filter(SlideFilter.Annotated).Select(s => s.SlideId));
        Assert.Equal(new[] { 62 }, index.Filter(SlideFilter.Favorite).Select(s => s.SlideId));
    }

    [Fact]
    public void ToggleFavorite_FlipsAndReportsState()
    {
        var index = SlideContentIndex.Build(Details((71, 1, "A")), Archive());

        Assert.True(index.ToggleFavorite(71));
        Assert.True(index.Find(71).Favorite);
        Assert.False(index.ToggleFavorite(71));
        Assert.False(index.Find(71).Favorite);
        Assert.False(index.ToggleFavorite(999));
    }

    [Fact]
    public void MarkAiAndAttachThumbnail_TargetExistingSlideOnly()
    {
        var index = SlideContentIndex.Build(Details((81, 1, "A")), Archive());

        Assert.True(index.MarkAi(81));
        Assert.True(index.Find(81).HasAi);
        Assert.True(index.AttachThumbnail(81, "thumb-81.png"));
        Assert.Equal("thumb-81.png", index.Find(81).ThumbnailPath);
        Assert.True(index.SetText(81, "正文"));

        Assert.False(index.MarkAi(999));
        Assert.False(index.AttachThumbnail(81, ""));
        Assert.False(index.AttachThumbnail(999, "x.png"));
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var index = SlideContentIndex.Build(Details((91, 1, "A")), Archive((91, 1, 1)));
        index.SetFavorite(91, true);
        index.AttachThumbnail(91, "thumb-91.png");
        index.SetText(91, "标题");

        SlideContentIndex restored = SlideContentIndex.Deserialize(index.Serialize());

        Assert.NotNull(restored);
        Assert.Equal(1, restored.Count);
        Assert.True(restored.Slides[0].Favorite);
        Assert.Equal("thumb-91.png", restored.Slides[0].ThumbnailPath);
        Assert.Equal("标题", restored.Slides[0].Text);
        Assert.True(restored.Slides[0].HasInk);
    }

    [Fact]
    public void Deserialize_DamagedJsonReturnsNull()
    {
        Assert.Null(SlideContentIndex.Deserialize(""));
        Assert.Null(SlideContentIndex.Deserialize("{not json"));
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var index = SlideContentIndex.Build(Details((101, 1, "A")), Archive());
        SlideContentIndex copy = index.Clone();

        copy.Slides[0].Favorite = true;

        Assert.False(index.Slides[0].Favorite);
        Assert.True(copy.Slides[0].Favorite);
    }
}