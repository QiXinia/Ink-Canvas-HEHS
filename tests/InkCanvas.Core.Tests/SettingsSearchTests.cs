using InkCanvas.Core.Settings;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class SettingsSearchTests
{
    private static readonly SettingsSearchEntry[] Entries =
    {
        new("外观选项", "颜色主题", "跟随系统 浅色 深色"),
        new("画板选项", "荧光笔透明度", ""),
        new("画板选项", "荧光笔粗细", ""),
        new("PPT 选项", "Microsoft PowerPoint 支持", ""),
        new("实验性设置", "批注跟随正文滚动", "屏幕内容跟随"),
    };

    [Fact]
    public void SearchMatchesChineseLabelsCategoryAndChoiceText()
    {
        Assert.Equal(2, SettingsSearch.Find(Entries, " 荧光笔 ").Count);
        Assert.Equal(Entries[1], Assert.Single(SettingsSearch.Find(Entries, "画板 透明")));
        Assert.Equal(Entries[0], Assert.Single(SettingsSearch.Find(Entries, "深色")));
        Assert.Equal(Entries[3], Assert.Single(SettingsSearch.Find(Entries, "powerpoint")));
        Assert.Equal(Entries[4], Assert.Single(SettingsSearch.Find(Entries, "屏幕内容")));
    }

    [Fact]
    public void EmptyAndMissingQueriesHaveNoResults()
    {
        Assert.Empty(SettingsSearch.Find(Entries, null));
        Assert.Empty(SettingsSearch.Find(Entries, "  "));
        Assert.Empty(SettingsSearch.Find(Entries, "未安装功能"));
        Assert.Empty(SettingsSearch.Find(Entries, "荧光笔 不存在"));
    }

    [Fact]
    public void ExactSettingAppearsBeforeCategoryAndKeywordMatches()
    {
        var exact = new SettingsSearchEntry("外观", "主题", "");
        var prefix = new SettingsSearchEntry("外观", "主题颜色", "");
        var keyword = new SettingsSearchEntry("外观", "颜色", "主题");
        Assert.Equal(new[] { exact, prefix, keyword }, SettingsSearch.Find(new[] { keyword, prefix, exact }, "主题"));
    }
}
