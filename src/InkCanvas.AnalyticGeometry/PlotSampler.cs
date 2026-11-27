namespace InkCanvas.AnalyticGeometry;

/// <summary>Screen-space adaptive curves and stitched marching squares, with bounded work.</summary>
public static class PlotSampler
{
    public const double Width = 640, Height = 480;
    public const int MaximumPoints = 32000;
    public const int MaximumEvaluations = 220000;
    public static PlotMesh Sample(CompiledPlot compiled, PlotDefinition definition, CancellationToken token = default, bool interactive = false)
    {
        definition.Validate();
        return new Sampler(compiled, definition, token, interactive).Run();
    }
    private sealed class BudgetExceededException : Exception;
    private readonly record struct Segment(PlotPoint First, PlotPoint Last);
    private sealed class Sampler(CompiledPlot compiled, PlotDefinition definition, CancellationToken token, bool interactive)
    {
        private readonly List<PlotPoint[]> _paths = new();
        private readonly List<PlotPoint> _current = new();
        private readonly List<Segment> _segments = new();
        private int _evaluations, _points;
        private bool _limited;

        public PlotMesh Run()
        {
            try
            {
                if (compiled.Kind == CompiledPlotKind.Implicit) Implicit();
                else
                {
                    double first = compiled.Kind switch { CompiledPlotKind.FunctionY => definition.XMin, CompiledPlotKind.FunctionX => definition.YMin, _ => definition.TMin };
                    double last = compiled.Kind switch { CompiledPlotKind.FunctionY => definition.XMax, CompiledPlotKind.FunctionX => definition.YMax, _ => definition.TMax };
                    const int intervals = 128;
                    var start = Curve(first);
                    for (int i = 1; i <= intervals; i++)
                    {
                        double next = first + (last - first) * i / intervals;
                        var end = Curve(next);
                        Adaptive(first + (last - first) * (i - 1) / intervals, start, next, end, 0); start = end;
                    }
                    Flush();
                }
            }
            catch (BudgetExceededException) { _limited = true; Flush(); }
            token.ThrowIfCancellationRequested();
            return new PlotMesh(_paths, _evaluations, _limited);
        }
        private double Evaluate(MathExpression expression, double x, double y, double t)
        {
            if (_evaluations >= MaximumEvaluations) throw new BudgetExceededException();
            if ((++_evaluations & 63) == 0) token.ThrowIfCancellationRequested();
            return expression.Evaluate(x, y, t, definition.A, definition.B, definition.C);
        }
        private PlotPoint Screen(double x, double y) => new((x - definition.XMin) * Width / (definition.XMax - definition.XMin),
            (definition.YMax - y) * Height / (definition.YMax - definition.YMin));
        private double Field(PlotPoint screen) => Evaluate(compiled.First,
            definition.XMin + screen.X * (definition.XMax - definition.XMin) / Width,
            definition.YMax - screen.Y * (definition.YMax - definition.YMin) / Height, 0);
        private PlotPoint Curve(double parameter) => compiled.Kind switch
        {
            CompiledPlotKind.FunctionY => Screen(parameter, Evaluate(compiled.First, parameter, 0, 0)),
            CompiledPlotKind.FunctionX => Screen(Evaluate(compiled.First, 0, parameter, 0), parameter),
            CompiledPlotKind.Parametric => Screen(Evaluate(compiled.First, 0, 0, parameter), Evaluate(compiled.Second, 0, 0, parameter)),
            _ => Polar(parameter),
        };
        private PlotPoint Polar(double parameter)
        {
            double radius = Evaluate(compiled.First, 0, 0, parameter);
            return Screen(radius * Math.Cos(parameter), radius * Math.Sin(parameter));
        }
        private static bool Finite(PlotPoint point) => double.IsFinite(point.X) && double.IsFinite(point.Y);
        private static double DistanceSquared(PlotPoint a, PlotPoint b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
        private static double Error(PlotPoint actual, PlotPoint first, PlotPoint last, double fraction)
            => Math.Sqrt(DistanceSquared(actual, new(first.X + (last.X - first.X) * fraction, first.Y + (last.Y - first.Y) * fraction)));
        private static bool OutsideSameSide(PlotPoint a, PlotPoint b, PlotPoint c)
            => a.X < -2 && b.X < -2 && c.X < -2 || a.X > Width + 2 && b.X > Width + 2 && c.X > Width + 2
                || a.Y < -2 && b.Y < -2 && c.Y < -2 || a.Y > Height + 2 && b.Y > Height + 2 && c.Y > Height + 2;
        private void Adaptive(double start, PlotPoint first, double end, PlotPoint last, int depth)
        {
            double middle = (start + end) / 2;
            var center = Curve(middle);
            bool finite = Finite(first) && Finite(center) && Finite(last);
            if (finite && OutsideSameSide(first, center, last)) { Flush(); return; }
            double error = finite ? Error(center, first, last, .5) : double.PositiveInfinity;
            if (finite && error <= .55 && depth == 0)
            {
                var check = Curve(start + (end - start) * .38196601125);
                error = Finite(check) ? Math.Max(error, Error(check, first, last, .38196601125)) : double.PositiveInfinity;
            }
            if (!finite || error > .55)
            {
                if (depth < 10)
                { Adaptive(start, first, middle, center, depth + 1); Adaptive(middle, center, end, last, depth + 1); }
                else { if (finite) _limited = true; Flush(); }
                return;
            }
            AddClipped(first, last);
        }
        private void AddClipped(PlotPoint first, PlotPoint last)
        {
            double dx = last.X - first.X, dy = last.Y - first.Y, enter = 0, exit = 1;
            if (!Clip(-dx, first.X, ref enter, ref exit) || !Clip(dx, Width - first.X, ref enter, ref exit)
                || !Clip(-dy, first.Y, ref enter, ref exit) || !Clip(dy, Height - first.Y, ref enter, ref exit)) { Flush(); return; }
            var a = new PlotPoint(first.X + dx * enter, first.Y + dy * enter);
            var b = new PlotPoint(first.X + dx * exit, first.Y + dy * exit);
            if (_current.Count == 0 || DistanceSquared(_current[^1], a) > .001) { Flush(); Append(a); }
            if (DistanceSquared(_current[^1], b) > 1e-10) Append(b);
            if (exit < 1) Flush();
        }
        private static bool Clip(double direction, double offset, ref double enter, ref double exit)
        {
            if (Math.Abs(direction) < 1e-15) return offset >= 0;
            double ratio = offset / direction;
            if (direction < 0) { if (ratio > exit) return false; enter = Math.Max(enter, ratio); }
            else { if (ratio < enter) return false; exit = Math.Min(exit, ratio); }
            return true;
        }
        private void Append(PlotPoint point)
        {
            if (_points >= MaximumPoints) throw new BudgetExceededException();
            _points++;
            _current.Add(point);
        }
        private void Flush()
        {
            if (_current.Count >= 2) _paths.Add(_current.ToArray());
            _current.Clear();
        }

        private void Implicit()
        {
            int columns = interactive ? 128 : 256, rows = interactive ? 96 : 192;
            var previous = new double[columns + 1]; var next = new double[columns + 1];
            Span<PlotPoint> corners = stackalloc PlotPoint[4];
            Span<double> values = stackalloc double[4];
            Span<PlotPoint> crossings = stackalloc PlotPoint[4];
            for (int x = 0; x <= columns; x++) previous[x] = Field(new(x * Width / columns, 0));
            for (int row = 0; row < rows; row++)
            {
                double top = row * Height / rows, bottom = (row + 1) * Height / rows;
                for (int x = 0; x <= columns; x++) next[x] = Field(new(x * Width / columns, bottom));
                for (int column = 0; column < columns; column++)
                {
                    corners[0] = new(column * Width / columns, top); corners[1] = new((column + 1) * Width / columns, top);
                    corners[2] = new((column + 1) * Width / columns, bottom); corners[3] = new(column * Width / columns, bottom);
                    values[0] = previous[column]; values[1] = previous[column + 1]; values[2] = next[column + 1]; values[3] = next[column];
                    int count = 0;
                    for (int edge = 0; edge < 4; edge++)
                    {
                        int other = (edge + 1) % 4;
                        if (!double.IsFinite(values[edge]) || !double.IsFinite(values[other]) || (values[edge] >= 0) == (values[other] >= 0)) continue;
                        if (Root(corners[edge], values[edge], corners[other], values[other]) is { } crossing) crossings[count++] = crossing;
                    }
                    if (count == 2) AddSegment(crossings[0], crossings[1]);
                    else if (count == 4)
                    {
                        double center = Field(new((corners[0].X + corners[2].X) / 2, (top + bottom) / 2));
                        if ((center >= 0) == (values[0] >= 0)) { AddSegment(crossings[0], crossings[1]); AddSegment(crossings[2], crossings[3]); }
                        else { AddSegment(crossings[0], crossings[3]); AddSegment(crossings[1], crossings[2]); }
                    }
                }
                (previous, next) = (next, previous);
            }
            Stitch();
        }
        private PlotPoint? Root(PlotPoint first, double a, PlotPoint last, double b)
        {
            double tolerance = Math.Max(1e-10, Math.Min(Math.Abs(a), Math.Abs(b)) * 1e-5);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                double fraction = a / (a - b);
                fraction = attempt > 2 ? Math.Clamp(fraction, .05, .95) : Math.Clamp(fraction, 0, 1);
                var point = new PlotPoint(first.X + (last.X - first.X) * fraction, first.Y + (last.Y - first.Y) * fraction);
                double value = Field(point);
                if (!double.IsFinite(value)) return null;
                if (Math.Abs(value) <= tolerance) return point;
                if ((value >= 0) == (a >= 0)) { first = point; a = value; } else { last = point; b = value; }
            }
            return null;
        }
        private void AddSegment(PlotPoint a, PlotPoint b)
        {
            if (_segments.Count >= MaximumPoints / 2) throw new BudgetExceededException();
            if (DistanceSquared(a, b) > 1e-12) _segments.Add(new(a, b));
        }
        private static (long X, long Y) Key(PlotPoint point) => ((long)Math.Round(point.X * 1024), (long)Math.Round(point.Y * 1024));
        private void Stitch()
        {
            var edges = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < _segments.Count; i++)
            {
                Add(Key(_segments[i].First), i); Add(Key(_segments[i].Last), i);
            }
            var used = new bool[_segments.Count];
            foreach (var pair in edges.Where(pair => pair.Value.Count != 2))
                foreach (int index in pair.Value) if (!used[index]) Trace(index, pair.Key);
            for (int i = 0; i < _segments.Count; i++) if (!used[i]) Trace(i, Key(_segments[i].First));
            void Add((long, long) key, int index)
            { if (!edges.TryGetValue(key, out var values)) edges[key] = values = new List<int>(2); values.Add(index); }
            void Trace(int index, (long, long) start)
            {
                var key = start;
                while (index >= 0 && !used[index])
                {
                    used[index] = true; var segment = _segments[index];
                    var a = Key(segment.First) == key ? segment.First : segment.Last;
                    var b = Key(segment.First) == key ? segment.Last : segment.First;
                    if (_current.Count == 0) Append(a);
                    Append(b); key = Key(b);
                    if (key == start) break;
                    int nextIndex = edges[key].FindIndex(candidate => !used[candidate]);
                    index = nextIndex < 0 ? -1 : edges[key][nextIndex];
                }
                Flush();
            }
        }
    }
}
