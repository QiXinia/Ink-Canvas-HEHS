namespace InkCanvas.Core.Ink;

/// <summary>One causal pressure filter per stroke. Historical widths are never revisited.</summary>
internal sealed class HardwarePressureProfile
{
    private readonly float _minimum, _maximum;
    private readonly bool _softBrush;
    private float _lastPressure, _filteredPressure, _time;
    private bool _initialized;

    public HardwarePressureProfile(TaperKind kind, bool highlighter)
    {
        _softBrush = !highlighter && kind is TaperKind.SoftPen or TaperKind.Calligraphy or TaperKind.InkWash;
        _minimum = highlighter ? .3f : _softBrush ? .04f : .12f;
        _maximum = highlighter ? 1.4f : _softBrush ? 2f : 1.75f;
    }

    public void Apply(Span<InkPoint> points, int index, float baseWidth)
    {
        ref var point = ref points[index];
        if (point.HasPressure) _lastPressure = point.Pressure;
        if (!_initialized)
        {
            _filteredPressure = _lastPressure;
            _initialized = true;
        }
        else
        {
            float elapsed = point.TimeMs - _time;
            // Equal-time packets can still contain a real pressure change. A bounded response
            // keeps them usable without inventing a sample interval.
            float response = .5f;
            if (elapsed > 0 && elapsed < 100)
                response = 1 - MathF.Exp(-elapsed / (4 + 8 * (1 - MathF.Abs(_lastPressure - _filteredPressure))));
            else if (elapsed != 0) response = 1;
            _filteredPressure += response * (_lastPressure - _filteredPressure);
        }
        _time = point.TimeMs;

        float ratio;
        if (_filteredPressure <= .5f)
        {
            float unit = _filteredPressure * 2;
            if (_softBrush) unit *= unit;
            ratio = _minimum + (1 - _minimum) * unit;
        }
        else
        {
            float unit = (_filteredPressure - .5f) * 2;
            ratio = 1 + (_maximum - 1) * unit;
        }
        point.Width = MathF.Max(.05f, baseWidth * ratio);
    }
}
