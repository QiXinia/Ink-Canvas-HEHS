using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InkCanvas.Features.Classroom;

/// <summary>
/// 随机点名名单（纯逻辑）。负责名单增删、CSV/TXT 导入、本轮不重复抽取与本地 JSON 持久化。
/// 不做任何 UI 或文件对话框相关的工作，窗口层只负责调用这里的 API。
/// </summary>
public sealed class ClassroomRoster
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<string> _names = new();
    private readonly List<string> _history = new();

    /// <summary>当前名单，按加入顺序返回；外部只读。</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>本轮已经抽出的名单（按抽取顺序）。</summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>抽取成功时触发，参数为抽中的姓名；供上层记录课堂时间轴。</summary>
    public event Action<string> Drawn;

    /// <summary>替换整份名单：Trim、去重（保留首次出现）、过滤空项，并清空本轮历史。</summary>
    public void Load(IEnumerable<string> names)
    {
        _names.Clear();
        _history.Clear();
        if (names is null) return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in names)
        {
            string value = raw?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (seen.Add(value)) _names.Add(value);
        }
    }

    /// <summary>添加一个姓名；空项或已存在时忽略。返回是否真的加入。</summary>
    public bool Add(string name)
    {
        string value = name?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        if (_names.Exists(n => string.Equals(n, value, StringComparison.Ordinal))) return false;
        _names.Add(value);
        return true;
    }

    /// <summary>移除一个姓名（同时从本轮历史里移除）。返回是否找到并移除。</summary>
    public bool Remove(string name)
    {
        string value = name?.Trim();
        if (string.IsNullOrEmpty(value)) return false;
        int index = _names.FindIndex(n => string.Equals(n, value, StringComparison.Ordinal));
        if (index < 0) return false;
        _names.RemoveAt(index);
        _history.RemoveAll(n => string.Equals(n, value, StringComparison.Ordinal));
        return true;
    }

    /// <summary>清空名单与本轮历史。</summary>
    public void Clear()
    {
        _names.Clear();
        _history.Clear();
    }

    /// <summary>
    /// 从 CSV/TXT 导入名单。CSV 取第一列（支持简单双引号转义），TXT 每行即一个姓名。
    /// 空行忽略。成功返回 <c>null</c>，失败返回可展示的中文错误信息，绝不抛异常。
    /// </summary>
    public string ImportCsvOrTxt(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "未选择文件。";
            if (!File.Exists(path)) return $"文件不存在：{path}";

            bool isCsv = string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase);
            var parsed = new List<string>();

            foreach (string raw in File.ReadAllLines(path))
            {
                if (raw is null) continue;
                string line = raw.Trim();
                if (line.Length == 0) continue;

                string name = isCsv ? FirstCsvField(line) : line;
                if (!string.IsNullOrWhiteSpace(name)) parsed.Add(name);
            }

            if (parsed.Count == 0) return "文件中没有可用的名单。";

            Load(parsed);
            return null;
        }
        catch (Exception ex)
        {
            return $"导入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 抽取一个姓名。<paramref name="withoutRepeat"/> 为 true 时本轮不重复（已抽出的不再出现）。
    /// 名单为空或本轮已抽完时返回 <c>null</c>。使用 <see cref="RandomNumberGenerator"/> 保证可靠随机。
    /// </summary>
    public string DrawOne(bool withoutRepeat)
    {
        if (_names.Count == 0) return null;

        var pool = new List<string>();
        foreach (string name in _names)
        {
            if (withoutRepeat && _history.Exists(h => string.Equals(h, name, StringComparison.Ordinal)))
                continue;
            pool.Add(name);
        }
        if (pool.Count == 0) return null;

        string picked = pool[RandomNumberGenerator.GetInt32(pool.Count)];
        _history.Add(picked);
        Drawn?.Invoke(picked);
        return picked;
    }

    /// <summary>重置本轮：清空历史，让所有姓名重新进入抽取池。</summary>
    public void ResetRound() => _history.Clear();

    /// <summary>保存名单到本地 JSON。返回是否成功（失败时不抛异常）。</summary>
    public bool Save(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var file = new RosterFile { Names = new List<string>(_names) };
            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>从本地 JSON 载入名单。返回是否成功；失败时保持原名单不变。</summary>
    public bool Load(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            var file = JsonSerializer.Deserialize<RosterFile>(File.ReadAllText(path), JsonOptions);
            Load(file?.Names ?? new List<string>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析 CSV 一行并返回第一列；支持 <c>"a,b"</c> 与 <c>""</c> 转义。</summary>
    private static string FirstCsvField(string line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        if (line[0] == '"')
        {
            var builder = new StringBuilder();
            int i = 1;
            while (i < line.Length)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i += 2;
                        continue;
                    }
                    i++;
                    break;
                }
                builder.Append(c);
                i++;
            }
            return builder.ToString().Trim();
        }

        int comma = line.IndexOf(',');
        string field = comma >= 0 ? line.Substring(0, comma) : line;
        return field.Trim();
    }

    private sealed class RosterFile
    {
        public List<string> Names { get; set; } = new();
    }
}