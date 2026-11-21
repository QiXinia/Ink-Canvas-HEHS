using InkCanvas.Features.Classroom;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>随机点名名单与聚光灯几何的纯逻辑测试。</summary>
public sealed class ClassroomToolkitTests
{
    private static string TempFile(string extension)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-roster-{Guid.NewGuid():N}{extension}");

    // ---- ClassroomRoster -------------------------------------------------

    [Fact]
    public void Load_DedupesTrimsAndFiltersEmpty()
    {
        var roster = new ClassroomRoster();

        roster.Load(new[] { " 张三 ", "李四", "张三", "", "   ", null, "王五" });

        Assert.Equal(new[] { "张三", "李四", "王五" }, roster.Names);
        Assert.Empty(roster.History);
    }

    [Fact]
    public void Add_IgnoresBlankAndDuplicates()
    {
        var roster = new ClassroomRoster();
        roster.Load(new[] { "张三" });

        Assert.False(roster.Add("   "));
        Assert.False(roster.Add(null));
        Assert.False(roster.Add(" 张三 "));

        Assert.True(roster.Add("李四"));
        Assert.Equal(new[] { "张三", "李四" }, roster.Names);
    }

    [Fact]
    public void Remove_RemovesNameAndHistory()
    {
        var roster = new ClassroomRoster();
        roster.Load(new[] { "张三", "李四" });
        roster.DrawOne(withoutRepeat: false);

        Assert.True(roster.Remove("张三"));
        Assert.False(roster.Remove("张三"));
        Assert.Equal(new[] { "李四" }, roster.Names);
    }

    [Fact]
    public void ImportCsv_ReadsFirstColumn_AndSkipsBlankLines()
    {
        string path = TempFile(".csv");
        try
        {
            File.WriteAllText(path, "张三,1班\r\n\r\n李四,2班\r\n   \r\n王五,3班\r\n");

            var roster = new ClassroomRoster();
            string error = roster.ImportCsvOrTxt(path);

            Assert.Null(error);
            Assert.Equal(new[] { "张三", "李四", "王五" }, roster.Names);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ImportCsv_HandlesQuotedComma()
    {
        string path = TempFile(".csv");
        try
        {
            File.WriteAllText(path, "\"张,三\",男\r\n\"李\"\"四\",女\r\n");

            var roster = new ClassroomRoster();
            string error = roster.ImportCsvOrTxt(path);

            Assert.Null(error);
            Assert.Equal(new[] { "张,三", "李\"四" }, roster.Names);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ImportTxt_UsesWholeLines()
    {
        string path = TempFile(".txt");
        try
        {
            File.WriteAllText(path, "张三\r\n李四\r\n\r\n王五\r\n");

            var roster = new ClassroomRoster();
            string error = roster.ImportCsvOrTxt(path);

            Assert.Null(error);
            Assert.Equal(new[] { "张三", "李四", "王五" }, roster.Names);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Import_NonexistentFile_ReturnsErrorWithoutThrowing()
    {
        var roster = new ClassroomRoster();

        string error = roster.ImportCsvOrTxt(TempFile(".csv"));

        Assert.NotNull(error);
        Assert.Empty(roster.Names);
    }

    [Fact]
    public void DrawOne_WithoutRepeat_ProducesNoDuplicatesInRound()
    {
        var roster = new ClassroomRoster();
        roster.Load(new[] { "A", "B", "C" });

        var drawn = new List<string>();
        for (int i = 0; i < 3; i++) drawn.Add(roster.DrawOne(withoutRepeat: true));

        Assert.Equal(3, drawn.Distinct().Count());
        Assert.Equal(3, roster.History.Count);
    }

    [Fact]
    public void DrawOne_WithoutRepeat_ReturnsNullWhenExhausted()
    {
        var roster = new ClassroomRoster();
        roster.Load(new[] { "A", "B" });

        Assert.NotNull(roster.DrawOne(withoutRepeat: true));
        Assert.NotNull(roster.DrawOne(withoutRepeat: true));
        Assert.Null(roster.DrawOne(withoutRepeat: true));
    }

    [Fact]
    public void DrawOne_WithRepeat_AlwaysReturnsNameWhileNotEmpty()
    {
        var roster = new ClassroomRoster();
        roster.Load(new[] { "A" });

        for (int i = 0; i < 20; i++)
            Assert.Equal("A", roster.DrawOne(withoutRepeat: false));

        Assert.All(roster.History, n => Assert.Equal("A", n));
        Assert.Equal(20, roster.History.Count);
    }

    [Fact]
    public void DrawOne_EmptyRoster_ReturnsNull()
    {
        var roster = new ClassroomRoster();
        Assert.Null(roster.DrawOne(withoutRepeat: false));
        Assert.Null(roster.DrawOne(withoutRepeat: true));
    }

    [Fact]
    public void ResetRound_ClearsHistory_AndAllowsRedraw()
    {
        var roster = new ClassroomRoster();
        roster.Load(new[] { "A", "B" });
        roster.DrawOne(withoutRepeat: true);
        roster.DrawOne(withoutRepeat: true);
        Assert.Null(roster.DrawOne(withoutRepeat: true));

        roster.ResetRound();

        Assert.Empty(roster.History);
        Assert.NotNull(roster.DrawOne(withoutRepeat: true));
    }

    [Fact]
    public void SaveLoad_RoundTripsNames()
    {
        string path = TempFile(".json");
        try
        {
            var roster = new ClassroomRoster();
            roster.Load(new[] { "张三", "李四", "王五" });
            Assert.True(roster.Save(path));

            var loaded = new ClassroomRoster();
            Assert.True(loaded.Load(path));

            Assert.Equal(new[] { "张三", "李四", "王五" }, loaded.Names);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- SpotlightGeometry ----------------------------------------------

    [Fact]
    public void ClampCenter_KeepsSpotInsideBounds()
    {
        var (x1, y1) = SpotlightGeometry.ClampCenter(-50, 50, 100, 100, 20);
        Assert.Equal(20, x1, 3);
        Assert.Equal(50, y1, 3);

        var (x2, y2) = SpotlightGeometry.ClampCenter(999, 999, 100, 100, 20);
        Assert.Equal(80, x2, 3);
        Assert.Equal(80, y2, 3);

        var (x3, y3) = SpotlightGeometry.ClampCenter(50, 50, 100, 100, 20);
        Assert.Equal(50, x3, 3);
        Assert.Equal(50, y3, 3);
    }

    [Fact]
    public void ClampCenter_RadiusLargerThanHalf_CentersSpot()
    {
        var (x, y) = SpotlightGeometry.ClampCenter(10, 10, 60, 60, 500);
        Assert.Equal(30, x, 3);
        Assert.Equal(30, y, 3);
    }

    [Fact]
    public void NormalizeRect_HandlesReversedCorners()
    {
        var (left, top, width, height) = SpotlightGeometry.NormalizeRect(100, 80, 20, 30);

        Assert.Equal(20, left, 3);
        Assert.Equal(30, top, 3);
        Assert.Equal(80, width, 3);
        Assert.Equal(50, height, 3);
    }

    [Fact]
    public void RadiusFrom_HalvesDiameter()
    {
        Assert.Equal(20, SpotlightGeometry.RadiusFrom(40), 3);
        Assert.Equal(0, SpotlightGeometry.RadiusFrom(-5), 3);
    }

    [Fact]
    public void ClampRadius_ClampsToHalfMinDimension()
    {
        Assert.Equal(30, SpotlightGeometry.ClampRadius(999, 120, 60), 3);
        Assert.Equal(0, SpotlightGeometry.ClampRadius(-10, 120, 60), 3);
        Assert.Equal(10, SpotlightGeometry.ClampRadius(10, 120, 60), 3);
    }
}