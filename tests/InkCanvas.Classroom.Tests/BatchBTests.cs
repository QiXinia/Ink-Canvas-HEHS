using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>批次 B：邻页缓存、三级降级矩阵与翻页笔按键映射的纯逻辑测试。</summary>
public sealed class BatchBTests
{
    private static InkStroke Stroke(float x)
    {
        var stroke = new InkStroke(new StrokeAttributes
        {
            Color = InkColor.Black,
            BaseWidth = 2f,
            TaperKind = TaperKind.None,
        });
        stroke.AddPoint(x, 10f);
        stroke.AddPoint(x + 5f, 10f);
        stroke.Complete();
        return stroke;
    }

    // ---- SlideInkCache ---------------------------------------------------

    [Fact]
    public void Cache_GetMiss_ReturnsNull_AndCounts()
    {
        var cache = new SlideInkCache();
        Assert.Null(cache.Get(7));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void Cache_SetThenGet_ReturnsDeepCopy()
    {
        var cache = new SlideInkCache();
        InkStroke original = Stroke(1);
        cache.Set(7, new[] { original });

        IReadOnlyList<InkStroke> first = cache.Get(7);
        Assert.NotNull(first);
        Assert.Single(first);
        Assert.NotSame(original, first[0]);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Count);

        // Mutating the returned copy must not corrupt the cached entry.
        first[0].AddPoint(99f, 10f);
        IReadOnlyList<InkStroke> second = cache.Get(7);
        Assert.NotEqual(first[0].Points.Count, second[0].Points.Count);
    }

    [Fact]
    public void Cache_LruEvictsLeastRecentlyUsed()
    {
        var cache = new SlideInkCache(2);
        cache.Set(1, new[] { Stroke(1) });
        cache.Set(2, new[] { Stroke(2) });

        Assert.NotNull(cache.Get(1));
        cache.Set(3, new[] { Stroke(3) });

        Assert.NotNull(cache.Get(1));
        Assert.Null(cache.Get(2));
        Assert.NotNull(cache.Get(3));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Cache_Capacity_IsRespected()
    {
        var cache = new SlideInkCache(3);
        for (int i = 1; i <= 10; i++) cache.Set(i, new[] { Stroke(i) });
        Assert.Equal(3, cache.Count);
        Assert.Equal(3, cache.Capacity);
    }

    [Fact]
    public void Cache_ReSetSameSlide_UpdatesWithoutGrowing()
    {
        var cache = new SlideInkCache(2);
        cache.Set(1, new[] { Stroke(1) });
        cache.Set(1, new[] { Stroke(2), Stroke(3) });
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, cache.Get(1).Count);
    }

    [Fact]
    public void Cache_Clear_RemovesEntries()
    {
        var cache = new SlideInkCache();
        cache.Set(1, new[] { Stroke(1) });
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Get(1));
    }

    [Fact]
    public void Cache_ConcurrentWrites_DoNotThrow()
    {
        var cache = new SlideInkCache(4);
        Parallel.For(0, 500, i =>
        {
            cache.Set(i % 12, new[] { Stroke(i) });
            if (i % 3 == 0) cache.Get(i % 12);
        });
        Assert.InRange(cache.Count, 1, 4);
    }

    // ---- PresentationSourceSelector -------------------------------------

    [Fact]
    public void Selector_PrefersComWindowKeys()
    {
        var selector = new PresentationSourceSelector();

        selector.Select(comAvailable: true, windowAvailable: true);
        Assert.Equal(PresentationSourceLevel.Com, selector.Active);

        selector.Select(comAvailable: false, windowAvailable: true);
        Assert.Equal(PresentationSourceLevel.Window, selector.Active);

        selector.Select(comAvailable: false, windowAvailable: false);
        Assert.Equal(PresentationSourceLevel.Keys, selector.Active);
    }

    [Fact]
    public void Selector_ReportFailure_DegradesOneStep()
    {
        var selector = new PresentationSourceSelector();
        selector.Select(true, true);

        selector.ReportFailure(PresentationSourceLevel.Com);
        Assert.Equal(PresentationSourceLevel.Window, selector.Active);
        Assert.False(string.IsNullOrWhiteSpace(selector.LastReason));

        selector.ReportFailure(PresentationSourceLevel.Window);
        Assert.Equal(PresentationSourceLevel.Keys, selector.Active);

        selector.ReportFailure(PresentationSourceLevel.Keys);
        Assert.Equal(PresentationSourceLevel.None, selector.Active);
    }

    [Fact]
    public void Selector_Degraded_DoesNotPromoteBackToCom()
    {
        var selector = new PresentationSourceSelector();
        selector.Select(true, true);
        selector.ReportFailure(PresentationSourceLevel.Com);

        selector.Select(comAvailable: true, windowAvailable: true);
        Assert.Equal(PresentationSourceLevel.Window, selector.Active);
    }

    [Fact]
    public void Selector_AllUnavailable_FallsBackToKeys()
    {
        var selector = new PresentationSourceSelector();
        selector.Select(comAvailable: false, windowAvailable: false);
        Assert.Equal(PresentationSourceLevel.Keys, selector.Active);
    }

    [Fact]
    public void Selector_Reset_RestoresCom()
    {
        var selector = new PresentationSourceSelector();
        selector.ReportFailure(PresentationSourceLevel.Com);
        selector.ReportFailure(PresentationSourceLevel.Window);

        selector.Reset();
        Assert.Equal(PresentationSourceLevel.Com, selector.Active);
        Assert.Null(selector.LastReason);
    }

    [Fact]
    public void Selector_NeverThrows()
    {
        var selector = new PresentationSourceSelector();
        Exception exception = Record.Exception(() =>
        {
            selector.ReportFailure((PresentationSourceLevel)999);
            selector.ReportFailure(PresentationSourceLevel.None);
            selector.Select(false, false);
        });
        Assert.Null(exception);
        Assert.Equal(PresentationSourceLevel.Keys, selector.Active);
    }

    // ---- PresentationKeyMap ---------------------------------------------

    [Theory]
    [InlineData(PresentationKeyMap.VkPageDown)]
    [InlineData(PresentationKeyMap.VkRight)]
    [InlineData(PresentationKeyMap.VkDown)]
    [InlineData(PresentationKeyMap.VkSpace)]
    [InlineData(PresentationKeyMap.VkOemPeriod)]
    public void KeyMap_NextKeys(int virtualKey)
    {
        Assert.Equal(PresentationKeyAction.Next, PresentationKeyMap.Map(virtualKey));
    }

    [Theory]
    [InlineData(PresentationKeyMap.VkPageUp)]
    [InlineData(PresentationKeyMap.VkLeft)]
    [InlineData(PresentationKeyMap.VkUp)]
    [InlineData(PresentationKeyMap.VkBackspace)]
    public void KeyMap_PreviousKeys(int virtualKey)
    {
        Assert.Equal(PresentationKeyAction.Previous, PresentationKeyMap.Map(virtualKey));
    }

    [Fact]
    public void KeyMap_B_TogglesBlackScreen()
    {
        Assert.Equal(PresentationKeyAction.ToggleBlackScreen, PresentationKeyMap.Map(PresentationKeyMap.VkB));
    }

    [Fact]
    public void KeyMap_UnknownKey_IsNone()
    {
        Assert.Equal(PresentationKeyAction.None, PresentationKeyMap.Map(0x41));
        Assert.False(PresentationKeyMap.IsHandled(0x41));
        Assert.True(PresentationKeyMap.IsHandled(PresentationKeyMap.VkPageDown));
    }
}