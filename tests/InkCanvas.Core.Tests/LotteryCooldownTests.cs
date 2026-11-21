using System.Text.Json;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Persistence;
using InkCanvas.Features.Tools;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class LotteryCooldownTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void SoftCooldownDefaultsAndLegacyModesSurviveSettingsRoundTrips()
    {
        var defaults = new AppSettings();
        Assert.Equal(LotterySamplingMode.SoftCooldown, defaults.Rand.SamplingMode);
        Assert.Equal(10, defaults.Rand.CooldownHalfLifeDays);
        Assert.Equal(1, defaults.Rand.CooldownPenaltyStrength);
        var legacy = JsonSerializer.Deserialize<AppSettings>("{\"randSettings\":{\"peopleCount\":45,\"isNotRepeatName\":true}}");
        Assert.Equal(LotterySamplingMode.RoundWithoutReplacement, legacy.Rand.SamplingMode);
        Assert.Equal(45, legacy.Rand.PeopleCount);
        foreach (var mode in Enum.GetValues<LotterySamplingMode>())
        {
            defaults.Rand.SamplingMode = mode; defaults.Rand.CooldownHalfLifeDays = 17;
            var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(defaults));
            Assert.Equal(mode, restored.Rand.SamplingMode);
            Assert.Equal(17, restored.Rand.CooldownHalfLifeDays);
        }
        defaults.ResetToRecommendation();
        Assert.Equal(LotterySamplingMode.SoftCooldown, defaults.Rand.SamplingMode);
        Assert.Equal(10, defaults.Rand.CooldownHalfLifeDays);
    }

    [Fact]
    public void FirstPickLowersProbabilityAndActualTimeRestoresItWithATenDayHalfLife()
    {
        var clock = new Clock();
        var service = new LotteryService(clock) { PeopleCount = 2 };
        int selected = int.Parse(service.Pick()) - 1;
        Assert.Equal(1d / 3, service.GetProbabilities()[selected], 10);
        clock.Now += TimeSpan.FromDays(10);
        Assert.Equal(.4, service.GetProbabilities()[selected], 10);
        clock.Now += TimeSpan.FromDays(10);
        Assert.Equal(4d / 9, service.GetProbabilities()[selected], 10);
        clock.Now += TimeSpan.FromDays(10000);
        Assert.Equal(.5, service.GetProbabilities()[selected], 10);
        Assert.Single(service.History);
    }

    [Fact]
    public void CountsAccumulateAndChangingHalfLifePreservesDecayAlreadyElapsed()
    {
        var clock = new Clock();
        var service = new LotteryService(clock) { PeopleCount = 1 };
        service.Draw(1); service.Draw(1);
        Assert.Equal(2, service.CaptureCooldownMemory().Rosters.Single().Scores.Values.Single());
        clock.Now += TimeSpan.FromDays(5);
        service.CooldownHalfLifeDays = 2;
        clock.Now += TimeSpan.FromDays(2);
        Assert.Equal(Math.Pow(.5, .5), service.CaptureCooldownMemory().Rosters.Single().Scores.Values.Single(), 10);
    }

    [Fact]
    public void BatchIsUniqueButLaterDrawsCanRepeatAndTheProbabilityNeverVanishes()
    {
        var service = new LotteryService(new Clock()) { PeopleCount = 30 };
        service.Reseed(17);
        for (int round = 0; round < 1000; round++)
        {
            var result = service.Draw(7);
            Assert.Equal(7, result.Count);
            Assert.Equal(7, result.Distinct().Count());
        }
        Assert.All(service.GetProbabilities(), probability => Assert.InRange(probability, double.Epsilon, 1));
        Assert.Equal(30, service.Draw(100).Distinct().Count());
        Assert.Equal(30, service.RemainingCount);
        var singleton = new LotteryService(new Clock()) { PeopleCount = 1 };
        Assert.Equal("1", singleton.Pick()); Assert.Equal("1", singleton.Pick());
    }

    [Fact]
    public void ReorderingNamesKeepsCooldownAndDuplicateNamesHaveIndependentMemory()
    {
        var service = new LotteryService(new Clock());
        service.LoadNames(new[] { "甲", "乙", "丙" });
        string picked = service.Pick();
        var probabilities = service.Names.Zip(service.GetProbabilities()).ToDictionary(pair => pair.First, pair => pair.Second);
        service.LoadNames(new[] { "丙", "甲", "乙" });
        for (int index = 0; index < service.Names.Count; index++) Assert.Equal(probabilities[service.Names[index]], service.GetProbabilities()[index], 10);
        service.LoadNames(new[] { "同名", "同名" });
        service.Pick();
        Assert.NotEqual(service.GetProbabilities()[0], service.GetProbabilities()[1]);
        service.LoadNames(new[] { "甲", "乙", "丙" });
        Assert.Equal(probabilities[picked], service.GetProbabilities()[service.Names.IndexOf(picked)], 10);
    }

    [Fact]
    public void NumericPoolsAndDifferentRostersDoNotSharePenaltyRecords()
    {
        var service = new LotteryService(new Clock()) { PeopleCount = 2 };
        service.Pick();
        service.LoadNames(new[] { "1", "2" });
        Assert.All(service.GetProbabilities(), probability => Assert.Equal(.5, probability));
        service.Pick(); service.LoadNames(new[] { "1", "3" });
        Assert.All(service.GetProbabilities(), probability => Assert.Equal(.5, probability));
        service.LoadNames(Array.Empty<string>());
        Assert.NotEqual(service.GetProbabilities()[0], service.GetProbabilities()[1]);
    }

    [Fact]
    public void ResettingNonRepeatingRoundsDoesNotEraseCooldownAndExplicitClearDoes()
    {
        var service = new LotteryService(new Clock()) { PeopleCount = 3 };
        service.Pick(); var before = service.GetProbabilities().ToArray();
        service.Reset(); Assert.Equal(before, service.GetProbabilities());
        service.NoRepeat = true;
        Assert.Equal(3, service.Draw(8).Distinct().Count()); Assert.Empty(service.Draw(1));
        service.Reset(); Assert.Equal(3, service.RemainingCount);
        service.NoRepeat = false;
        Assert.True(service.GetProbabilities().Min() < service.GetProbabilities().Max());
        service.ClearCurrentCooldownMemory();
        Assert.All(service.GetProbabilities(), probability => Assert.Equal(1d / 3, probability, 10));
    }

    [Fact]
    public void ClockRollbackAndInvalidSettingsDoNotCreateInvalidProbabilities()
    {
        var clock = new Clock(); var service = new LotteryService(clock) { PeopleCount = 2 };
        service.Pick(); var before = service.GetProbabilities().ToArray();
        clock.Now -= TimeSpan.FromDays(2);
        Assert.Equal(before, service.GetProbabilities());
        service.CooldownHalfLifeDays = double.NaN; service.CooldownPenaltyStrength = double.PositiveInfinity;
        Assert.Equal(10, service.CooldownHalfLifeDays); Assert.Equal(1, service.CooldownPenaltyStrength);
        var snapshot = service.CaptureCooldownMemory();
        foreach (var state in snapshot.Rosters) foreach (string key in state.Scores.Keys.ToArray()) state.Scores[key] = double.MaxValue;
        service.RestoreCooldownMemory(snapshot);
        Assert.All(service.GetProbabilities(), probability => Assert.True(double.IsFinite(probability) && probability > 0));
    }

    [Fact]
    public void PersistenceRestoresOfflineDecayAndFlushesTheLastPendingPick()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-cooldown-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock();
        try
        {
            var service = new LotteryService(clock) { PeopleCount = 2 };
            int picked;
            using (var store = new LotteryMemoryStore(service, directory)) picked = int.Parse(service.Pick()) - 1;
            clock.Now += TimeSpan.FromDays(10);
            var restored = new LotteryService(clock) { PeopleCount = 2 };
            using (var store = new LotteryMemoryStore(restored, directory))
            {
                Assert.Null(store.LastError);
                Assert.Equal(.4, restored.GetProbabilities()[picked], 10);
                restored.ClearCurrentCooldownMemory(); store.Flush();
            }
            var cleared = new LotteryService(clock) { PeopleCount = 2 };
            using var finalStore = new LotteryMemoryStore(cleared, directory);
            Assert.All(cleared.GetProbabilities(), probability => Assert.Equal(.5, probability));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConcurrentDrawsRemainUniqueAndPersistAllUpdates()
    {
        var service = new LotteryService(new Clock()) { PeopleCount = 10 };
        Parallel.For(0, 100, _ => Assert.Equal(5, service.Draw(5).Distinct().Count()));
        Assert.Equal(500, service.CaptureCooldownMemory().Rosters.Single().Scores.Values.Sum());
    }

    [Fact]
    public void CorruptOrUnwritableMemoryDoesNotPreventDrawing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-cooldown-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "LotteryCooldown.json"), "{invalid");
            var service = new LotteryService(new Clock()) { PeopleCount = 2 };
            using (var store = new LotteryMemoryStore(service, directory))
            {
                Assert.NotNull(store.LastError);
                Assert.NotNull(service.Pick()); store.Flush(); Assert.Null(store.LastError);
            }
            string blocked = Path.Combine(directory, "file-instead-of-directory");
            File.WriteAllText(blocked, "fixture");
            using var unwritable = new LotteryMemoryStore(service, blocked);
            Assert.NotNull(service.Pick()); unwritable.Flush(); Assert.NotNull(unwritable.LastError);
            Assert.All(service.GetProbabilities(), probability => Assert.True(probability > 0));
        }
        finally { Directory.Delete(directory, true); }
    }
}
