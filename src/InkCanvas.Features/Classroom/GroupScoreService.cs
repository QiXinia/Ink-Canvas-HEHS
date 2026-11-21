using InkCanvas.Core.Classroom;
using System.Text;
using System.Text.Json;

namespace InkCanvas.Features.Classroom;

public enum GroupSplitMode
{
    BySize,
    ByGroupCount,
}

public sealed class ScoreGroup
{
    public int Index { get; set; }

    public string Name { get; set; } = "";

    public List<string> Members { get; set; } = new();

    public int Score { get; set; }

    public int RoundDelta { get; set; }
}

public sealed class GroupScoreState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public int Round { get; set; } = 1;

    public long UpdatedAtMs { get; set; }

    public List<ScoreGroup> Groups { get; set; } = new();

    public int TotalScore => Sum(scores => scores.Score);

    private int Sum(Func<ScoreGroup, int> selector)
    {
        int total = 0;
        foreach (ScoreGroup group in Groups) total += selector(group);
        return total;
    }
}

public static class GroupSplitPolicy
{
    /// <summary>
    /// 随机分组：按人数时最后一组可少人；按组数时不能整除的余数依次分给前面的组。
    /// 名单为空或参数非法时返回空结果。
    /// </summary>
    public static IReadOnlyList<List<string>> Split(
        IReadOnlyList<string> names, int value, GroupSplitMode mode, int seed = 0)
    {
        var result = new List<List<string>>();
        if (names is null || names.Count == 0 || value <= 0) return result;

        var pool = new List<string>();
        foreach (string name in names)
        {
            string trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && !pool.Contains(trimmed)) pool.Add(trimmed);
        }

        if (pool.Count == 0) return result;

        var random = new Random(seed);
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int swap = random.Next(i + 1);
            (pool[i], pool[swap]) = (pool[swap], pool[i]);
        }

        if (mode == GroupSplitMode.BySize)
        {
            int size = Math.Min(value, pool.Count);
            for (int i = 0; i < pool.Count; i += size)
                result.Add(pool.GetRange(i, Math.Min(size, pool.Count - i)));
            return result;
        }

        int groupCount = Math.Min(value, pool.Count);
        int baseSize = pool.Count / groupCount;
        int remainder = pool.Count % groupCount;
        int cursor = 0;
        for (int i = 0; i < groupCount; i++)
        {
            int take = baseSize + (i < remainder ? 1 : 0);
            result.Add(pool.GetRange(cursor, take));
            cursor += take;
        }

        return result;
    }
}

/// <summary>分组积分：与点名/抽奖共用同一份名单，全部数据仅本地保存。</summary>
public sealed class GroupScoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public GroupScoreService(GroupScoreState state = null)
    {
        State = state ?? new GroupScoreState();
        State.Groups ??= new List<ScoreGroup>();
        Normalize();
    }

    public GroupScoreState State { get; }

    public IReadOnlyList<ScoreGroup> Groups => State.Groups;

    public void ResetGroups(IReadOnlyList<string> names, GroupSplitMode mode, int value, int seed = 0)
    {
        IReadOnlyList<List<string>> split = GroupSplitPolicy.Split(names, value, mode, seed);
        State.Groups = new List<ScoreGroup>();
        for (int i = 0; i < split.Count; i++)
        {
            State.Groups.Add(new ScoreGroup
            {
                Index = i + 1,
                Name = "第 " + (i + 1) + " 组",
                Members = new List<string>(split[i]),
            });
        }

        State.Round = 1;
        Normalize();
    }

    public bool MoveMember(string name, int toGroupIndex)
    {
        string trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        ScoreGroup target = FindGroup(toGroupIndex);
        if (target is null) return false;

        foreach (ScoreGroup group in State.Groups)
        {
            if (group.Members.Remove(trimmed) && group.Index == toGroupIndex) return true;
        }

        target.Members.Add(trimmed);
        return true;
    }

    public int AddScore(int groupIndex, int delta)
    {
        ScoreGroup group = FindGroup(groupIndex);
        if (group is null) return 0;

        group.Score += delta;
        group.RoundDelta += delta;
        return group.Score;
    }

    /// <summary>本轮重置：保留成员分组，清空本轮积分。</summary>
    public void ResetRound()
    {
        State.Round++;
        foreach (ScoreGroup group in State.Groups)
        {
            group.Score = 0;
            group.RoundDelta = 0;
        }
    }

    public IReadOnlyList<ScoreGroup> Ranking()
    {
        var copy = new List<ScoreGroup>(State.Groups);
        copy.Sort((left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Index.CompareTo(right.Index);
        });
        return copy;
    }

    public string BuildResultText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("小组积分（第 " + State.Round + " 轮）");
        foreach (ScoreGroup group in Ranking())
        {
            builder.Append(group.Name).Append("：").Append(group.Score).Append(" 分");
            if (group.Members.Count > 0)
                builder.Append("（").Append(string.Join("、", group.Members)).Append("）");
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    public string Serialize() => JsonSerializer.Serialize(State, JsonOptions);

    public static GroupScoreService Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new GroupScoreService();
        try
        {
            GroupScoreState state = JsonSerializer.Deserialize<GroupScoreState>(json, JsonOptions);
            if (state?.Groups is null) return new GroupScoreService();
            state.Version = Math.Max(1, state.Version);
            return new GroupScoreService(state);
        }
        catch (JsonException)
        {
            return new GroupScoreService();
        }
    }

    private void Normalize()
    {
        for (int i = 0; i < State.Groups.Count; i++)
        {
            ScoreGroup group = State.Groups[i];
            group.Index = i + 1;
            group.Members ??= new List<string>();
            if (string.IsNullOrWhiteSpace(group.Name)) group.Name = "第 " + (i + 1) + " 组";
        }
    }

    private ScoreGroup FindGroup(int groupIndex)
    {
        if (groupIndex <= 0 || groupIndex > State.Groups.Count) return null;
        return State.Groups[groupIndex - 1];
    }
}