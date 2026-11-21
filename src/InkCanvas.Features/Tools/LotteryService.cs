using System.Security.Cryptography;
using System.Text;
using InkCanvas.Core.Settings;

namespace InkCanvas.Features.Tools;

/// <summary>Classroom sampling with persistent, exponentially decayed negative reinforcement.</summary>
public sealed class LotteryService
{
    private const double MinimumWeight = 1e-9;
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly HashSet<int> _used = new();
    private readonly List<LotteryDraw> _history = new();
    private readonly Dictionary<string, LotteryCooldownRoster> _memory = new(StringComparer.Ordinal);
    private Random _random = new();
    private int _peopleCount = 60;
    private LotterySamplingMode _samplingMode;
    private double _halfLifeDays = 10, _penaltyStrength = 1;
    private string[] _identityNames, _participantKeys;
    private string _rosterKey;
    private int _identityNumberCount;

    public LotteryService() : this(TimeProvider.System) { }
    public LotteryService(TimeProvider clock) => _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    public List<string> Names { get; } = new();
    public event Action NamesChanged;
    public event Action ConfigurationChanged;
    public event Action CooldownMemoryChanged;

    public int PeopleCount
    {
        get { lock (_gate) return _peopleCount; }
        set
        {
            lock (_gate)
            {
                int count = Math.Max(1, value);
                if (_peopleCount == count) return;
                _peopleCount = count;
                if (Names.Count == 0) _used.Clear();
            }
            ConfigurationChanged?.Invoke();
        }
    }

    public LotterySamplingMode SamplingMode
    {
        get { lock (_gate) return _samplingMode; }
        set
        {
            lock (_gate)
            {
                var mode = Enum.IsDefined(value) ? value : LotterySamplingMode.SoftCooldown;
                if (_samplingMode == mode) return;
                _samplingMode = mode;
                _used.Clear();
            }
            ConfigurationChanged?.Invoke();
        }
    }

    public bool NoRepeat
    {
        get => SamplingMode == LotterySamplingMode.RoundWithoutReplacement;
        set => SamplingMode = value ? LotterySamplingMode.RoundWithoutReplacement
            : SamplingMode == LotterySamplingMode.RoundWithoutReplacement ? LotterySamplingMode.SoftCooldown : SamplingMode;
    }

    public double CooldownHalfLifeDays
    {
        get { lock (_gate) return _halfLifeDays; }
        set
        {
            lock (_gate)
            {
                double days = double.IsFinite(value) ? Math.Clamp(value, .25, 3650) : 10;
                if (_halfLifeDays == days) return;
                var now = _clock.GetUtcNow();
                foreach (var state in _memory.Values) { Decay(state, now); state.HalfLifeDays = days; }
                _halfLifeDays = days;
            }
            CooldownMemoryChanged?.Invoke();
            ConfigurationChanged?.Invoke();
        }
    }

    public double CooldownPenaltyStrength
    {
        get { lock (_gate) return _penaltyStrength; }
        set
        {
            lock (_gate)
            {
                double strength = double.IsFinite(value) ? Math.Clamp(value, .1, 10) : 1;
                if (_penaltyStrength == strength) return;
                _penaltyStrength = strength;
            }
            ConfigurationChanged?.Invoke();
        }
    }

    public IReadOnlyCollection<int> Used { get { lock (_gate) return _used.ToArray(); } }
    public int PoolSize { get { lock (_gate) return CurrentPoolSize; } }
    public int RemainingCount { get { lock (_gate) return NoRepeat ? Enumerable.Range(0, CurrentPoolSize).Count(index => !_used.Contains(index)) : CurrentPoolSize; } }
    public IReadOnlyList<LotteryDraw> History { get { lock (_gate) return _history.ToArray(); } }
    private int CurrentPoolSize => Names.Count > 0 ? Names.Count : _peopleCount;

    public void Reseed(int seed) { lock (_gate) _random = new Random(seed); }
    /// <summary>Starts a new non-repeating round. Time-based cooldown memory is retained.</summary>
    public void Reset() { lock (_gate) _used.Clear(); }
    public string Pick() => Draw(1).FirstOrDefault();

    public IReadOnlyList<string> Draw(int count)
    {
        if (count <= 0) return Array.Empty<string>();
        string[] result;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var state = GetCurrentMemory(now);
            var available = Enumerable.Range(0, CurrentPoolSize).Where(index => !NoRepeat || !_used.Contains(index)).ToArray();
            int take = Math.Min(count, available.Length);
            if (take == 0) return Array.Empty<string>();
            var selected = new int[take];
            if (_samplingMode == LotterySamplingMode.SoftCooldown)
            {
                var sampler = new WeightedSampler(available.Select(index => Weight(state, _participantKeys[index])).ToArray());
                for (int index = 0; index < take; index++) selected[index] = available[sampler.Take(_random)];
            }
            else
            {
                for (int index = 0; index < take; index++)
                {
                    int next = _random.Next(index, available.Length);
                    (available[index], available[next]) = (available[next], available[index]);
                    selected[index] = available[index];
                }
            }
            result = new string[take];
            for (int index = 0; index < take; index++)
            {
                int picked = selected[index];
                if (NoRepeat) _used.Add(picked);
                string key = _participantKeys[picked];
                state.Scores[key] = state.Scores.GetValueOrDefault(key) + 1;
                result[index] = Names.Count > 0 ? Names[picked] : (picked + 1).ToString();
            }
            _history.Add(new LotteryDraw(now, Array.AsReadOnly(result)));
            if (_history.Count > 20) _history.RemoveAt(0);
        }
        CooldownMemoryChanged?.Invoke();
        return Array.AsReadOnly(result);
    }

    /// <summary>Current normalized probabilities, without recording or drawing a participant.</summary>
    public IReadOnlyList<double> GetProbabilities()
    {
        lock (_gate)
        {
            var state = GetCurrentMemory(_clock.GetUtcNow());
            var weights = new double[CurrentPoolSize];
            for (int index = 0; index < weights.Length; index++)
                weights[index] = NoRepeat && _used.Contains(index) ? 0
                    : _samplingMode == LotterySamplingMode.SoftCooldown ? Weight(state, _participantKeys[index]) : 1;
            double total = weights.Sum();
            if (total > 0) for (int index = 0; index < weights.Length; index++) weights[index] /= total;
            return weights;
        }
    }

    public void LoadNames(IEnumerable<string> names)
    {
        var normalized = names.Select(name => name?.Trim()).Where(name => !string.IsNullOrEmpty(name)).ToArray();
        lock (_gate)
        {
            if (Names.SequenceEqual(normalized)) return;
            Names.Clear(); Names.AddRange(normalized);
            _used.Clear(); _history.Clear();
        }
        NamesChanged?.Invoke();
    }

    public void ClearCurrentCooldownMemory()
    {
        lock (_gate) { GetCurrentMemory(_clock.GetUtcNow()).Scores.Clear(); }
        CooldownMemoryChanged?.Invoke();
    }

    private LotteryCooldownRoster GetCurrentMemory(DateTimeOffset now)
    {
        if (_participantKeys is null || _identityNumberCount != _peopleCount && Names.Count == 0
            || _identityNames is null || !Names.SequenceEqual(_identityNames))
        {
            _identityNames = Names.ToArray(); _identityNumberCount = _peopleCount;
            if (Names.Count == 0)
            {
                _participantKeys = Enumerable.Range(1, _peopleCount).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                _rosterKey = Hash("numeric\n" + _peopleCount);
            }
            else
            {
                var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
                _participantKeys = Names.Select(name =>
                {
                    int occurrence = occurrences.GetValueOrDefault(name);
                    occurrences[name] = occurrence + 1;
                    return Hash(name + "\0" + occurrence);
                }).ToArray();
                // Reordering the same roster retains memory. Different rosters and numeric pools are isolated.
                _rosterKey = Hash("names\n" + string.Join('\n', _participantKeys.Order(StringComparer.Ordinal)));
            }
        }
        if (!_memory.TryGetValue(_rosterKey, out var state))
        {
            state = new LotteryCooldownRoster { Key = _rosterKey, UpdatedUtc = now, HalfLifeDays = _halfLifeDays };
            _memory.Add(_rosterKey, state);
        }
        Decay(state, now);
        return state;
    }

    private double Weight(LotteryCooldownRoster state, string participant) =>
        Math.Max(MinimumWeight, 1 / (1 + _penaltyStrength * state.Scores.GetValueOrDefault(participant)));

    private static void Decay(LotteryCooldownRoster state, DateTimeOffset now)
    {
        if (now <= state.UpdatedUtc) return;
        double factor = Math.Exp(-Math.Log(2) * (now - state.UpdatedUtc).TotalDays / state.HalfLifeDays);
        foreach (string key in state.Scores.Keys.ToArray())
        {
            double score = state.Scores[key] * factor;
            if (score < 1e-12) state.Scores.Remove(key);
            else state.Scores[key] = score;
        }
        state.UpdatedUtc = now;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public LotteryCooldownMemory CaptureCooldownMemory()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var state in _memory.Values) Decay(state, now);
            return new LotteryCooldownMemory { Rosters = _memory.Values.Where(state => state.Scores.Count > 0)
                .Select(state => new LotteryCooldownRoster { Key = state.Key, UpdatedUtc = state.UpdatedUtc,
                    HalfLifeDays = state.HalfLifeDays, Scores = new(state.Scores, StringComparer.Ordinal) }).ToList() };
        }
    }

    public void RestoreCooldownMemory(LotteryCooldownMemory memory)
    {
        if (memory?.Version != 1 || memory.Rosters is null) return;
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var saved in memory.Rosters)
            {
                if (saved is null || string.IsNullOrEmpty(saved.Key) || saved.Key.Length > 128 || saved.Scores is null
                    || !double.IsFinite(saved.HalfLifeDays) || saved.HalfLifeDays is < .25 or > 3650) continue;
                var state = new LotteryCooldownRoster { Key = saved.Key,
                    UpdatedUtc = saved.UpdatedUtc > now ? now : saved.UpdatedUtc, HalfLifeDays = saved.HalfLifeDays };
                foreach (var pair in saved.Scores)
                    if (!string.IsNullOrEmpty(pair.Key) && pair.Key.Length <= 128 && double.IsFinite(pair.Value) && pair.Value > 0)
                        state.Scores[pair.Key] = pair.Value;
                Decay(state, now); state.HalfLifeDays = _halfLifeDays;
                _memory[state.Key] = state;
            }
        }
    }

    /// <summary>Fenwick sums provide O(n) construction and O(log n) removal per batch member.</summary>
    private sealed class WeightedSampler
    {
        private readonly double[] _weights, _tree;
        public WeightedSampler(double[] weights)
        {
            _weights = weights; _tree = new double[weights.Length + 1];
            for (int index = 1; index < _tree.Length; index++)
            {
                _tree[index] += weights[index - 1];
                int parent = index + (index & -index);
                if (parent < _tree.Length) _tree[parent] += _tree[index];
            }
        }
        public int Take(Random random)
        {
            double total = 0;
            for (int index = _weights.Length; index > 0; index -= index & -index) total += _tree[index];
            double target = random.NextDouble() * total;
            int selected = 0;
            for (int bit = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)_weights.Length); bit > 0; bit >>= 1)
            {
                int next = selected + bit;
                if (next < _tree.Length && _tree[next] <= target) { selected = next; target -= _tree[next]; }
            }
            // Floating point cancellation can leave a residue after the last weighted removal.
            if (selected >= _weights.Length || _weights[selected] <= 0) selected = Array.FindIndex(_weights, weight => weight > 0);
            double removed = _weights[selected]; _weights[selected] = 0;
            for (int index = selected + 1; index < _tree.Length; index += index & -index) _tree[index] -= removed;
            return selected;
        }
    }
}

public sealed class LotteryCooldownMemory
{
    public int Version { get; set; } = 1;
    public List<LotteryCooldownRoster> Rosters { get; set; } = new();
}

public sealed class LotteryCooldownRoster
{
    public string Key { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public double HalfLifeDays { get; set; } = 10;
    public Dictionary<string, double> Scores { get; set; } = new(StringComparer.Ordinal);
}
