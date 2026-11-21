using System.Globalization;

namespace InkCanvas.Pdf;

public static class PdfPageRange
{
    /// <summary>Parse user-facing, one-based page ranges without unbounded expansion.</summary>
    public static IReadOnlyList<int> Parse(string value, int pageCount, int maximum)
    {
        var pages = new SortedSet<int>();
        foreach (string entry in (value ?? "").Replace('，', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int start)
                || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out int end)
                || start < 1 || end < start || end > pageCount) throw new FormatException("页码范围无效，请输入 1-5,8 这样的范围。");
            if ((long)end - start + 1 > maximum) throw new FormatException("所选页数超过画板剩余容量。");
            for (int p = start; p <= end; p++)
            { pages.Add(p - 1); if (pages.Count > maximum) throw new FormatException("所选页数超过画板剩余容量。"); }
        }
        if (pages.Count == 0) throw new FormatException("请至少选择一页。");
        return pages.ToArray();
    }
}
