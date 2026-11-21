using System.Text.Json;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Tools;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.Core.Tests;

public sealed class LotterySimulationTests(ITestOutputHelper output)
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void HundredThousandDrawsCompareFairnessAndRecencyAcrossFourPolicies()
    {
        const int population = 30, draws = 100000, seed = 1729;
        var uniform = Simulate("uniform", LotterySamplingMode.Uniform, false);
        var meenie = Simulate("inverse counts in a 30-day window", null, true);
        var bag = Simulate("non-repeating rounds", LotterySamplingMode.RoundWithoutReplacement, false);
        var cooldown = Simulate("10-day half-life soft cooldown", LotterySamplingMode.SoftCooldown, false);
        var report = new { drawsPerPolicy = draws, population, seed, hoursBetweenDraws = 1,
            defaultPenaltyStrength = 1, halfLifeDays = 10, policies = new[] { uniform, meenie, bag, cooldown } };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(json);
        string destination = Environment.GetEnvironmentVariable("INKCANVAS_SIMULATION_REPORT");
        if (!string.IsNullOrWhiteSpace(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllText(destination, json);
        }
        Assert.True(cooldown.ConsecutiveRepeatRate < uniform.ConsecutiveRepeatRate);
        Assert.True(cooldown.RepeatWithinFiveRate < uniform.RepeatWithinFiveRate);
        Assert.True(cooldown.CountCoefficientOfVariation < uniform.CountCoefficientOfVariation);
        Assert.True(cooldown.MinimumObservedProbability > 0);
        Assert.Equal(0, bag.MinimumObservedProbability);

        Metrics Simulate(string policy, LotterySamplingMode? mode, bool windowCounts)
        {
            var clock = new Clock();
            var service = new LotteryService(clock) { PeopleCount = population, SamplingMode = mode ?? LotterySamplingMode.Uniform };
            service.Reseed(seed);
            var random = new Random(seed);
            var counts = new int[population];
            var recent = new Queue<int>();
            var window = new Queue<int>();
            var memoryCounts = new int[population];
            int consecutive = 0, repeats = 0, previous = -1;
            double minimumProbability = 1;
            for (int draw = 0; draw < draws; draw++)
            {
                int picked;
                if (windowCounts)
                {
                    if (window.Count >= 30 * 24) memoryCounts[window.Dequeue()]--;
                    var weights = memoryCounts.Select(count => 1d / (1 + count)).ToArray();
                    double total = weights.Sum();
                    minimumProbability = Math.Min(minimumProbability, weights.Min() / total);
                    double target = random.NextDouble() * total;
                    picked = 0;
                    while (picked < population - 1 && target >= weights[picked]) target -= weights[picked++];
                    memoryCounts[picked]++; window.Enqueue(picked);
                }
                else
                {
                    if (service.RemainingCount == 0) service.Reset();
                    minimumProbability = Math.Min(minimumProbability, service.GetProbabilities().Min());
                    picked = int.Parse(service.Pick()) - 1;
                }
                if (picked == previous) consecutive++;
                if (recent.Contains(picked)) repeats++;
                counts[picked]++; previous = picked;
                recent.Enqueue(picked); if (recent.Count > 5) recent.Dequeue();
                clock.Now += TimeSpan.FromHours(1);
            }
            double mean = (double)draws / population;
            double deviation = Math.Sqrt(counts.Select(count => Math.Pow(count - mean, 2)).Average());
            return new Metrics(policy, (double)consecutive / (draws - 1), (double)repeats / draws,
                counts.Max() - counts.Min(), deviation / mean, minimumProbability);
        }
    }

    private sealed record Metrics(string Policy, double ConsecutiveRepeatRate, double RepeatWithinFiveRate,
        int CountRange, double CountCoefficientOfVariation, double MinimumObservedProbability);
}
