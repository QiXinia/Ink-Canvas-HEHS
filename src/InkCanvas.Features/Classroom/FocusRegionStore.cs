using System.Text.Json;

namespace InkCanvas.Features.Classroom;

/// <summary>保存下来的聚焦区域：屏幕像素矩形 + 来源页，按讲解顺序切换。</summary>
public sealed class FocusRegionItem
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Order { get; set; }

    public int SlideId { get; set; }

    public int SlideIndex { get; set; }

    public int AnimationStep { get; set; }

    public int BoardPageIndex { get; set; } = -1;

    public long SavedAtMs { get; set; }

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public FocusRegionItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        X = X,
        Y = Y,
        Width = Width,
        Height = Height,
        Order = Order,
        SlideId = SlideId,
        SlideIndex = SlideIndex,
        AnimationStep = AnimationStep,
        BoardPageIndex = BoardPageIndex,
        SavedAtMs = SavedAtMs,
    };
}

public sealed class FocusRegionStore
{
    public const int CurrentVersion = 1;

    public const int MaximumRegions = 24;

    public int Version { get; set; } = CurrentVersion;

    public long UpdatedAtMs { get; set; }

    public List<FocusRegionItem> Regions { get; set; } = new();

    public int Count => Regions.Count;

    public FocusRegionItem Add(FocusRegionItem region, long nowMs = 0)
    {
        if (region is null || region.IsEmpty || Regions.Count >= MaximumRegions) return null;

        int nextOrder = 1;
        foreach (FocusRegionItem item in Regions) nextOrder = Math.Max(nextOrder, item.Order + 1);

        region.Id = string.IsNullOrWhiteSpace(region.Id) ? Guid.NewGuid().ToString("N") : region.Id;
        region.Order = nextOrder;
        if (string.IsNullOrWhiteSpace(region.Name)) region.Name = "区域 " + nextOrder;
        region.SavedAtMs = nowMs;
        Regions.Add(region);
        UpdatedAtMs = nowMs;
        return region;
    }

    public bool Remove(string id, long nowMs = 0)
    {
        int index = Regions.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;
        Regions.RemoveAt(index);
        Reindex();
        UpdatedAtMs = nowMs;
        return true;
    }

    public bool Move(string id, int delta, long nowMs = 0)
    {
        if (delta == 0) return false;
        int index = Regions.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
        if (index < 0) return false;

        int target = index + delta;
        if (target < 0 || target >= Regions.Count) return false;

        FocusRegionItem item = Regions[index];
        Regions.RemoveAt(index);
        Regions.Insert(target, item);
        Reindex();
        UpdatedAtMs = nowMs;
        return true;
    }

    /// <summary>按讲解顺序取下一个/上一个区域；current 不在列表时从第一个开始。</summary>
    public FocusRegionItem Next(FocusRegionItem current, int delta)
    {
        if (Regions.Count == 0) return null;
        if (delta == 0) delta = 1;

        int index = current is null
            ? -1
            : Regions.FindIndex(item => string.Equals(item.Id, current.Id, StringComparison.Ordinal));
        if (index < 0) index = delta > 0 ? -1 : 0;

        int target = index + delta;
        if (target < 0) target = Regions.Count - 1;
        if (target >= Regions.Count) target = 0;
        return Regions[target];
    }

    public FocusRegionItem FindMatching(int x, int y, int width, int height)
    {
        foreach (FocusRegionItem item in Regions)
        {
            if (Math.Abs(item.X - x) <= 2 && Math.Abs(item.Y - y) <= 2
                && Math.Abs(item.Width - width) <= 2 && Math.Abs(item.Height - height) <= 2)
            {
                return item;
            }
        }

        return null;
    }

    private void Reindex()
    {
        for (int i = 0; i < Regions.Count; i++) Regions[i].Order = i + 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static FocusRegionStore Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new FocusRegionStore();
        try
        {
            FocusRegionStore store = JsonSerializer.Deserialize<FocusRegionStore>(json, JsonOptions);
            if (store?.Regions is null) return new FocusRegionStore();
            store.Version = Math.Max(1, store.Version);
            return store;
        }
        catch (JsonException)
        {
            return new FocusRegionStore();
        }
    }
}