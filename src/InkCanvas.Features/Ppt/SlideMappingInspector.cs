namespace InkCanvas.Features.Ppt;

/// <summary>How a stored ink archive diverges from the presentation currently open.</summary>
public readonly record struct SlideMappingIssue(
    bool CountChanged, bool PageOrderChanged, IReadOnlyList<int> MissingSlideIds)
{
    public bool NeedsWarning => CountChanged || PageOrderChanged || (MissingSlideIds?.Count ?? 0) > 0;
}

/// <summary>
/// Compares a stored archive with the live slide list. The inspection is advisory only:
/// it never mutates the archive and tolerates missing or null inputs.
/// </summary>
public static class SlideMappingInspector
{
    public static SlideMappingIssue Inspect(PptInkArchive archive, IReadOnlyList<PptSlideInfo> currentSlides)
    {
        if (archive?.Slides is null || archive.Slides.Count == 0)
            return new SlideMappingIssue(false, false, Array.Empty<int>());

        var current = currentSlides ?? Array.Empty<PptSlideInfo>();
        var currentIndexById = new Dictionary<int, int>();
        foreach (var slide in current)
        {
            if (slide is null) continue;
            currentIndexById.TryAdd(slide.SlideId, slide.Index);
        }

        var missing = new List<int>();
        var matched = new List<(int StoredIndex, int CurrentIndex)>();
        bool orderChanged = false;

        foreach (var stored in archive.Slides)
        {
            if (stored is null) continue;
            if (!currentIndexById.TryGetValue(stored.SlideId, out int currentIndex))
            {
                missing.Add(stored.SlideId);
                continue;
            }

            if (currentIndex != stored.SlideIndex) orderChanged = true;
            matched.Add((stored.SlideIndex, currentIndex));
        }

        if (!orderChanged && matched.Count > 1 && IsAscending(matched.Select(m => m.StoredIndex)))
        {
            orderChanged = !IsAscending(matched.Select(m => m.CurrentIndex));
        }

        bool countChanged = archive.Presentation is not null
            && archive.Presentation.SlideCount != current.Count;

        return new SlideMappingIssue(countChanged, orderChanged, missing);
    }

    private static bool IsAscending(IEnumerable<int> values)
    {
        bool any = false;
        int previous = 0;
        foreach (int value in values)
        {
            if (any && value <= previous) return false;
            previous = value;
            any = true;
        }
        return true;
    }
}