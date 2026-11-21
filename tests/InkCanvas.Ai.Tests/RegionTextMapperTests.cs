using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>圈选矩形到文本区间的定点映射与局部替换。</summary>
public sealed class RegionTextMapperTests
{
    private static readonly AssistantRect Anchor = new(0, 0, 100, 100);

    [Fact]
    public void Map_LeftThird_SelectsLeadingCharacters()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(0, 0, 30, 100), "0123456789");
        Assert.Equal(0, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void Map_MiddleRange_SelectsInteriorCharacters()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(40, 0, 60, 100), "0123456789");
        Assert.Equal(4, start);
        Assert.Equal(2, length);
    }

    [Fact]
    public void Map_RightRange_SelectsTrailingCharacters()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(70, 0, 100, 100), "0123456789");
        Assert.Equal(7, start);
        Assert.Equal(3, length);
    }

    [Fact]
    public void Map_RegionOutsideLeft_ClampsToEmptyPrefix()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(-50, 0, -10, 100), "0123456789");
        Assert.Equal(0, start);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Map_RegionOutsideRight_ClampsToSingleLastCharacter()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(120, 0, 150, 100), "0123456789");
        Assert.Equal(9, start);
        Assert.Equal(1, length);
    }

    [Fact]
    public void Map_ReversedRegion_NormalizesDirection()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(80, 0, 20, 100), "0123456789");
        Assert.Equal(2, start);
        Assert.Equal(6, length);
    }

    [Fact]
    public void Map_NonEmptyTextAndNonZeroRatio_AlwaysReturnsAtLeastOneCharacter()
    {
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(40, 0, 60, 100), "x");
        Assert.Equal(0, start);
        Assert.Equal(1, length);
    }

    [Fact]
    public void Map_EmptyTextOrZeroAnchorWidth_ReturnsEmptyRange()
    {
        Assert.Equal((0, 0), RegionTextMapper.Map(Anchor, new AssistantRect(0, 0, 50, 100), ""));
        Assert.Equal((0, 0), RegionTextMapper.Map(Anchor, new AssistantRect(0, 0, 50, 100), null));
        Assert.Equal((0, 0), RegionTextMapper.Map(new AssistantRect(10, 10, 10, 20), new AssistantRect(0, 0, 5, 20), "abc"));
    }

    [Fact]
    public void Apply_ChangesOnlyTargetRange_KeepingPrefixAndSuffix()
    {
        string text = "ABCDEFGHIJ";
        string updated = RegionTextMapper.Apply(text, 3, 2, "xy");

        Assert.Equal("ABCxyFGHIJ", updated);
        Assert.Equal(text.Substring(0, 3), updated.Substring(0, 3));
        Assert.Equal(text.Substring(5), updated.Substring(5));
    }

    [Fact]
    public void Apply_ClampsOutOfRangeOffsets()
    {
        Assert.Equal("X", RegionTextMapper.Apply("abc", -10, 100, "X"));
        Assert.Equal("abcX", RegionTextMapper.Apply("abc", 3, 100, "X"));
        Assert.Equal("aXbc", RegionTextMapper.Apply("abc", 1, 0, "X"));
        Assert.Equal("abc", RegionTextMapper.Apply("abc", 1, -5, ""));
        Assert.Equal("X", RegionTextMapper.Apply(null, 0, 0, "X"));
    }

    [Fact]
    public void Apply_RepeatedLocalReplacement_PreservesUntouchedContent()
    {
        string first = RegionTextMapper.Apply("0123456789", 4, 2, "AB");
        Assert.Equal("0123AB6789", first);

        string second = RegionTextMapper.Apply(first, 0, 1, "z");
        Assert.Equal("z123AB6789", second);
        Assert.Equal("123AB6789", second.Substring(1));
    }

    [Fact]
    public void Apply_MapThenReplace_OnlyTheSelectedRegionDiffers()
    {
        const string text = "面积 = 长 × 宽";
        var (start, length) = RegionTextMapper.Map(Anchor, new AssistantRect(40, 0, 60, 100), text);
        string updated = RegionTextMapper.Apply(text, start, length, "高");

        Assert.Equal(text.Substring(0, start), updated.Substring(0, start));
        Assert.Equal(text.Substring(start + length), updated.Substring(start + "高".Length));
        Assert.Equal(text.Length - length + "高".Length, updated.Length);
    }
}