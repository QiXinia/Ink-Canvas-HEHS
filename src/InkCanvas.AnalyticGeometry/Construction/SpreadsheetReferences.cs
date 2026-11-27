using System.Globalization;
using System.Text.RegularExpressions;

namespace InkCanvas.AnalyticGeometry.Construction;

internal static partial class SpreadsheetReferences
{
    private const string Cell = @"\$?[A-Z]{1,6}\$?[1-9][0-9]{0,8}";
    private const string CellBoundary = @"[\p{L}\d_$]";
    private const string Quoted = @"""(?:\\.|[^""\\])*""";

    [GeneratedRegex(Quoted
        + @"|(?<!" + CellBoundary + @")(?i:CellRange)\s*[([]\s*(?<first>" + Cell + @")\s*,\s*(?<last>" + Cell
            + @")\s*(?:,\s*" + Quoted + @"\s*)?[)\]]"
        + @"|(?<!" + CellBoundary + @")(?<first>" + Cell + @")\s*:\s*(?<last>" + Cell + @")(?!" + CellBoundary + ")"
        + @"|(?<!" + CellBoundary + @")(?<absolute>\$[A-Z]{1,6}\$?[1-9][0-9]{0,8}|[A-Z]{1,6}\$[1-9][0-9]{0,8})(?!" + CellBoundary + ")",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    public static string Expand(string expression, IEnumerable<string> labels)
    {
        if (!expression.Contains(':') && !expression.Contains('$') && !expression.Contains("CellRange", StringComparison.OrdinalIgnoreCase))
            return expression;
        (string Label, int Column, int Row)[] cells = null;
        return ReferencePattern().Replace(expression, match =>
        {
            if (match.Groups["absolute"] is { Success: true } absolute)
                return absolute.Value.Replace("$", "", StringComparison.Ordinal);
            if (!match.Groups["first"].Success) return match.Value;

            TryAddress(match.Groups["first"].Value.Replace("$", "", StringComparison.Ordinal), out var first);
            TryAddress(match.Groups["last"].Value.Replace("$", "", StringComparison.Ordinal), out var last);
            cells ??= labels.Select(label => (Label: label, IsCell: TryAddress(label, out var location), Location: location))
                .Where(item => item.IsCell).OrderBy(item => item.Location.Column).ThenBy(item => item.Location.Row)
                .Select(item => (item.Label, item.Location.Column, item.Location.Row)).ToArray();

            int minColumn = Math.Min(first.Column, last.Column), maxColumn = Math.Max(first.Column, last.Column);
            int minRow = Math.Min(first.Row, last.Row), maxRow = Math.Max(first.Row, last.Row);
            var included = cells.Where(cell => cell.Column >= minColumn && cell.Column <= maxColumn
                && cell.Row >= minRow && cell.Row <= maxRow);
            return "{" + string.Join(',', included.Select(cell => cell.Label)) + "}";
        });
    }

    private static bool TryAddress(string label, out (int Column, int Row) location)
    {
        location = default;
        int column = 0, separator = 0;
        for (; separator < label.Length && label[separator] is >= 'A' and <= 'Z'; separator++)
        {
            if (separator == 6) return false;
            column = column * 26 + label[separator] - 'A' + 1;
        }
        if (separator == 0 || separator == label.Length || label[separator] == '0'
            || !int.TryParse(label.AsSpan(separator), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row <= 0)
            return false;
        location = (column, row);
        return true;
    }
}
