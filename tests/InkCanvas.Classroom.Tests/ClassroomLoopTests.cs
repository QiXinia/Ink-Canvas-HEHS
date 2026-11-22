using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

public sealed class FocusRegionTests
{
    private static FocusRegionItem Region(int index)
        => new()
        {
            X = index * 10,
            Y = index * 10,
            Width = 100,
            Height = 80,
            Name = "区域 " + index,
        };

    [Fact]
    public void Add_AssignsOrderAndSerializes()
    {
        var store = new FocusRegionStore();

        FocusRegionItem first = store.Add(Region(1), nowMs: 10);
        FocusRegionItem second = store.Add(Region(2), nowMs: 20);

        Assert.Equal(1, first.Order);
        Assert.Equal(2, second.Order);
        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.Equal(20, store.UpdatedAtMs);

        FocusRegionStore restored = FocusRegionStore.Deserialize(store.Serialize());
        Assert.Equal(2, restored.Count);
        Assert.Equal(second.Id, restored.Regions[1].Id);
    }

    [Fact]
    public void Add_RejectsEmptyAndCapsMaximum()
    {
        var store = new FocusRegionStore();
        Assert.Null(store.Add(new FocusRegionItem { Width = 0, Height = 0 }));

        for (int i = 0; i < FocusRegionStore.MaximumRegions; i++)
            Assert.NotNull(store.Add(Region(i)));

        Assert.Null(store.Add(Region(99)));
    }

    [Fact]
    public void Next_CyclesForwardAndBackward()
    {
        var store = new FocusRegionStore();
        FocusRegionItem first = store.Add(Region(1));
        FocusRegionItem second = store.Add(Region(2));

        Assert.Equal(second.Id, store.Next(first, 1).Id);
        Assert.Equal(first.Id, store.Next(second, 1).Id);
        Assert.Equal(second.Id, store.Next(first, -1).Id);
    }

    [Fact]
    public void Move_ReordersAndRemovesReindexes()
    {
        var store = new FocusRegionStore();
        FocusRegionItem first = store.Add(Region(1));
        FocusRegionItem second = store.Add(Region(2));

        Assert.True(store.Move(second.Id, -1));
        Assert.Equal(second.Id, store.Regions[0].Id);
        Assert.Equal(1, second.Order);

        Assert.True(store.Remove(first.Id));
        Assert.Single(store.Regions);
        Assert.Equal(1, store.Regions[0].Order);
    }

    [Fact]
    public void FindMatching_MatchesScreenRect()
    {
        var store = new FocusRegionStore();
        FocusRegionItem item = store.Add(new FocusRegionItem
        {
            X = 100, Y = 200, Width = 300, Height = 200,
        });

        Assert.Equal(item.Id, store.FindMatching(101, 199, 299, 201).Id);
        Assert.Null(store.FindMatching(0, 0, 10, 10));
    }
}
