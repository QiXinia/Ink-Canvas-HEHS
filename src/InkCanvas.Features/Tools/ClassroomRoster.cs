using System.Text;

namespace InkCanvas.Features.Tools;

public static class ClassroomRoster
{
    public static IReadOnlyList<string> Load(string directory)
    {
        string path = Path.Combine(directory, "Names.txt");
        if (!File.Exists(path)) return Array.Empty<string>();
        var names = Decode(File.ReadAllBytes(path)).ReplaceLineEndings("\n").Split('\n').Select(name => name.Trim());
        string rulesPath = Path.Combine(directory, "Replace.txt");
        var rules = File.Exists(rulesPath) ? File.ReadAllLines(rulesPath) : Array.Empty<string>();
        return names.Select(original =>
        {
            string name = original;
            foreach (string rule in rules)
            {
                int split = rule.IndexOf("-->", StringComparison.Ordinal);
                if (split >= 0 && name == rule[..split]) name = rule[(split + 3)..];
            }
            return name;
        }).Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    public static string Decode(byte[] bytes)
    {
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes, writable: false), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    /// <summary>Accepts one name per line or a CSV/TSV table with a name column.</summary>
    public static IReadOnlyList<string> Parse(string text)
    {
        string[] lines = (text ?? "").TrimStart('\uFEFF').Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        var rows = lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
        if (rows.Length == 0) return Array.Empty<string>();
        char separator = rows[0].Contains('\t') ? '\t' : ',';
        var header = Fields(rows[0], separator);
        int nameColumn = Array.FindIndex(header, field => field.Trim().ToLowerInvariant() is "姓名" or "名字" or "name" or "student name");
        int column = Math.Max(0, nameColumn);
        return rows.Skip(nameColumn >= 0 ? 1 : 0).Select(row => Fields(row, separator))
            .Where(fields => fields.Length > column).Select(fields => fields[column].Trim())
            .Where(name => name.Length > 0).ToArray();
    }

    private static string[] Fields(string row, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int index = 0; index < row.Length; index++)
        {
            char character = row[index];
            if (character == '"')
            {
                if (quoted && index + 1 < row.Length && row[index + 1] == '"') { field.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (character == separator && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(character);
        }
        fields.Add(field.ToString());
        return fields.ToArray();
    }
}
