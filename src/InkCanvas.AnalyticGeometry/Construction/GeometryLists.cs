namespace InkCanvas.AnalyticGeometry.Construction;

internal sealed partial class GeometryEvaluator
{
    private GeometryValue ListCommand(string command, string[] args)
    {
        var input = Evaluate(args[0]);
        if (command is "first" or "last" or "take" or "reverse")
            return Slice(command, input, args);
        if (command == "append" && args.Length == 2)
        {
            var second = Evaluate(args[1]);
            if (input.Kind == GeometryKind.List) return Collection(input.Items.Append(second));
            return Collection(RequireList(second).Prepend(input));
        }
        var items = RequireList(input);
        if (command == "join")
            return Collection((args.Length == 1 ? items : args.Select(Evaluate)).SelectMany(RequireList));
        if (command == "flatten" && args.Length == 1)
        {
            var output = new List<GeometryValue>();
            var pending = new Stack<GeometryValue>(items.Reverse());
            while (pending.TryPop(out var item))
            {
                Sample();
                if (item.Kind == GeometryKind.List)
                    foreach (var nested in item.Items.Reverse()) pending.Push(nested);
                else output.Add(item);
            }
            return Collection(output);
        }
        if (command == "sort" && args.Length is 1 or 2)
        {
            var keys = args.Length == 2 ? RequireList(Evaluate(args[1])) : items;
            if (keys.Count != items.Count || keys.Count > 0
                && (keys[0].Kind is not (GeometryKind.Scalar or GeometryKind.Text or GeometryKind.Point)
                    || keys.Any(key => key.Kind != keys[0].Kind)))
                throw new FormatException("排序键须是等长、同类型的数值、文本或点列表。");
            return Collection(items.Zip(keys).OrderBy(pair => pair.Second, Comparer<GeometryValue>.Create(CompareListValues))
                .Select(pair => pair.First));
        }
        if (command == "unique" && args.Length == 1)
        {
            if (items.Count == 0) return Collection([]);
            if (items.Any(item => item.Kind != items[0].Kind || item.Kind is not (GeometryKind.Scalar or GeometryKind.Text)))
                throw new FormatException("Unique 需要数值或文本列表。");
            return Collection(items.DistinctBy(item => (item.Kind, item.Number, item.Kind == GeometryKind.Text ? item.Display : ""))
                .Order(Comparer<GeometryValue>.Create(CompareListValues)));
        }
        throw new FormatException("列表指令参数数量无效。");
    }

    private GeometryValue Slice(string command, GeometryValue source, string[] args)
    {
        bool isText = source.Kind == GeometryKind.Text;
        var items = isText ? null : RequireList(source);
        string[] characters = isText ? TextElements(source.Display) : null;
        int length = characters?.Length ?? items.Count;
        if (args.Length > 3 || command != "take" && args.Length > 2 || command == "take" && args.Length == 1
            || command == "reverse" && args.Length != 1)
            throw new FormatException("截取指令参数数量无效。");

        int start = 0, count = length;
        if (command == "take")
        {
            start = ListIndex(N(args[1]), 1, length) - 1;
            int end = args.Length == 3 ? ListIndex(N(args[2]), 0, length) : length;
            count = Math.Max(0, end - start);
        }
        else if (command != "reverse")
        {
            count = args.Length == 2 ? ListIndex(N(args[1]), 0, length) : Math.Min(1, length);
            if (command == "last") start = length - count;
        }

        if (isText)
            return source with { Display = string.Concat(command == "reverse" ? characters.Reverse() : characters.Skip(start).Take(count)) };
        return Collection(command == "reverse" ? items.Reverse() : items.Skip(start).Take(count));
    }

    private GeometryValue ListStatistic(string command, string[] args)
    {
        var numeric = command == "product" && args.Length == 4 ? Sequence(args) : Evaluate(args[0]);
        if (args.Length > 2 && !(command == "product" && args.Length == 4)) throw new FormatException("统计指令参数数量无效。");
        var items = RequireList(numeric);
        var option = args.Length == 2 ? Evaluate(args[1]) : null;
        int count = command == "product" && option?.Kind == GeometryKind.Scalar ? ListIndex(option.Number, 0, items.Count) : items.Count;
        var frequencies = option?.Kind == GeometryKind.List ? RequireList(option) : null;
        if (option is not null && frequencies is null && !(command == "product" && option.Kind == GeometryKind.Scalar)
            || frequencies is not null && (frequencies.Count != count || frequencies.Any(item => item.Kind != GeometryKind.Scalar
                || !double.IsFinite(item.Number) || item.Number < 0)))
            throw new FormatException("需要有效的频数列表。");
        double Weight(int index) => frequencies?[index].Number ?? 1;
        if (items.Take(count).Any(item => item.Kind != GeometryKind.Scalar || !double.IsFinite(item.Number)))
            throw new FormatException("统计指令需要有限实数。");
        if (command == "product")
            return GeometryValue.Scalar(Enumerable.Range(0, count).Aggregate(1d,
                (product, index) => product * Math.Pow(items[index].Number, Weight(index))));

        double weight = Enumerable.Range(0, count).Sum(Weight);
        if (weight <= 0 || !double.IsFinite(weight)) throw new FormatException("统计指令需要正的总频数。");
        if (command == "median")
        {
            if (Enumerable.Range(0, count).Any(index => Weight(index) != Math.Truncate(Weight(index))))
                throw new FormatException("中位数需要整数频数。");
            double lowerRank = Math.Floor((weight + 1) / 2), upperRank = Math.Ceiling((weight + 1) / 2);
            double cumulative = 0;
            double? lower = null;
            foreach (int index in Enumerable.Range(0, count).OrderBy(index => items[index].Number))
            {
                cumulative += Weight(index);
                if (cumulative >= lowerRank) lower ??= items[index].Number;
                if (cumulative >= upperRank) return GeometryValue.Scalar(lower.Value / 2 + items[index].Number / 2);
            }
            throw new FormatException("中位数的频数超出精度范围。");
        }

        double mean = Enumerable.Range(0, count).Sum(index => items[index].Number * (Weight(index) / weight));
        double degrees = command.StartsWith("sample", StringComparison.Ordinal) ? weight - 1 : weight;
        if (degrees <= 0) throw new FormatException("样本方差需要至少两个样本。");
        double variance = Enumerable.Range(0, count).Sum(index =>
        {
            double difference = items[index].Number - mean;
            return difference * difference * (Weight(index) / degrees);
        });
        return GeometryValue.Scalar(command.EndsWith("sd", StringComparison.Ordinal) ? Math.Sqrt(variance) : variance);
    }

    private static string[] TextElements(string text)
    {
        var elements = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        var result = new List<string>();
        while (elements.MoveNext()) result.Add(elements.GetTextElement());
        return result.ToArray();
    }

    private static IReadOnlyList<GeometryValue> RequireList(GeometryValue value)
        => value.Kind == GeometryKind.List ? value.Items : throw new FormatException("此指令需要列表。");

    private static int ListIndex(double value, int minimum, int maximum)
        => double.IsFinite(value) && value == Math.Truncate(value) && value >= minimum && value <= maximum
            ? (int)value : throw new FormatException("序号超出范围。");

    private static int CompareListValues(GeometryValue a, GeometryValue b)
    {
        if (a.Kind == GeometryKind.Text) return StringComparer.Ordinal.Compare(a.Display, b.Display);
        if (a.Kind == GeometryKind.Scalar) return a.Number.CompareTo(b.Number);
        int x = a.Origin.X.CompareTo(b.Origin.X);
        if (x != 0) return x;
        int y = a.Origin.Y.CompareTo(b.Origin.Y);
        return y != 0 ? y : a.Origin.Z.CompareTo(b.Origin.Z);
    }
}
