using System.Text;
using InkCanvas.Features.Tools;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ClassroomToolsTests
{
    [Fact]
    public void RosterChangesNotifyOnceAndKeepUnchangedRounds()
    {
        var service = new LotteryService { NoRepeat = true };
        int changes = 0;
        service.NamesChanged += () => changes++;
        service.LoadNames(new[] { "甲", "乙" });
        service.Draw(1);
        service.LoadNames(new[] { "甲", "乙" });
        Assert.Equal(1, changes);
        Assert.Equal(1, service.RemainingCount);
        service.LoadNames(new[] { "丙", "丁" });
        Assert.Equal(2, changes);
        Assert.Equal(2, service.RemainingCount);
        Assert.Empty(service.History);
    }

    [Fact]
    public void SavedRosterKeepsNamesContainingCommasAndAppliesLegacyReplacements()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Names.txt"), "Smith, John\n旧名\n同名\n同名\n\n");
            File.WriteAllText(Path.Combine(directory, "Replace.txt"), "旧名-->新名");
            Assert.Equal(new[] { "Smith, John", "新名", "同名", "同名" }, ClassroomRoster.Load(directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void WholeRoundDrawsEveryParticipantExactlyOnce()
    {
        for (int seed = 0; seed < 100; seed++)
        {
            var service = new LotteryService { PeopleCount = 60, NoRepeat = true };
            service.Reseed(seed);
            var result = Enumerable.Range(0, 12).SelectMany(_ => service.Draw(5)).ToArray();
            Assert.Equal(60, result.Distinct().Count());
            Assert.Equal(0, service.RemainingCount);
            Assert.Empty(service.Draw(10));
            Assert.Null(service.Pick());
            service.Reset();
            Assert.Equal(60, service.RemainingCount);
        }
    }

    [Fact]
    public void EveryBatchIsUniqueWhenRepetitionBetweenBatchesIsAllowed()
    {
        var service = new LotteryService { PeopleCount = 20 };
        for (int index = 0; index < 30; index++)
        {
            var draw = service.Draw(30);
            Assert.Equal(20, draw.Count);
            Assert.Equal(20, draw.Distinct().Count());
        }
        Assert.Equal(20, service.History.Count);
        Assert.Equal(20, service.RemainingCount);
    }

    [Fact]
    public void ReopeningTheSameRosterKeepsTheRoundAndResults()
    {
        var service = new LotteryService { NoRepeat = true };
        service.LoadNames(new[] { "甲", "乙", "丙" });
        var draw = service.Draw(2).ToArray();
        service.LoadNames(new[] { " 甲 ", "乙", "丙", "" });
        Assert.Equal(1, service.RemainingCount);
        Assert.Single(service.History);
        Assert.Equal(draw, service.History[0].Names);
        service.LoadNames(new[] { "甲", "丁" });
        Assert.Equal(2, service.RemainingCount);
        Assert.Empty(service.History);
    }

    [Fact]
    public void PartialFinalBatchDoesNotResetOrRepeatTheRound()
    {
        var service = new LotteryService { PeopleCount = 7, NoRepeat = true };
        var first = service.Draw(5);
        var last = service.Draw(5);
        Assert.Equal(2, last.Count);
        Assert.Equal(7, first.Concat(last).Distinct().Count());
        Assert.Equal(0, service.RemainingCount);
    }

    [Fact]
    public void EqualNamesRemainSeparateRosterEntries()
    {
        var service = new LotteryService { NoRepeat = true };
        service.LoadNames(new[] { "同名学生", "同名学生" });
        Assert.Equal(2, service.Draw(2).Count);
        Assert.Equal(0, service.RemainingCount);
    }

    [Fact]
    public void NumericPoolChangesResetTheRoundAndNamedPoolIgnoresNumericSize()
    {
        var service = new LotteryService { PeopleCount = 5, NoRepeat = true };
        service.Draw(3); service.PeopleCount = 6;
        Assert.Equal(6, service.RemainingCount);
        service.LoadNames(new[] { "甲", "乙" }); service.Draw(1); service.PeopleCount = 20;
        Assert.Equal(1, service.RemainingCount);
        Assert.Equal(2, service.PoolSize);
    }

    [Theory]
    [InlineData(" 甲 \r\n\r\n乙\n", "甲|乙")]
    [InlineData("学号,姓名,班级\n1,张三,一班\n2,李四,一班", "张三|李四")]
    [InlineData("学号\t姓名\t班级\n1\t张三\t一班", "张三")]
    [InlineData("Name,Class\n\"Smith, John\",A\n\"A \"\"B\"\"\",A", "Smith, John|A \"B\"")]
    [InlineData("\uFEFF姓名\n甲\n乙", "甲|乙")]
    [InlineData("甲,一班\n乙,二班", "甲|乙")]
    public void RosterSupportsPastedListsAndTableNameColumns(string input, string expected)
        => Assert.Equal(expected, string.Join('|', ClassroomRoster.Parse(input)));

    [Fact]
    public void RosterReadsUtf8Utf16AndChineseWindowsCsv()
    {
        const string source = "姓名\r\n张三\r\n李四";
        Assert.Equal(source, ClassroomRoster.Decode(Encoding.UTF8.GetBytes(source)));
        Assert.Equal(source, ClassroomRoster.Decode(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(source)).ToArray()));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(source, ClassroomRoster.Decode(Encoding.GetEncoding(936).GetBytes(source)));
    }
}
