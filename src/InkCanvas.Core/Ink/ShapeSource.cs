using System.Text.Json;
using System.Text.Json.Serialization;
using InkCanvas.Core.Shapes;

namespace InkCanvas.Core.Ink;

/// <summary>Provider-owned formula data shared by every contour of a selectable shape.</summary>
public sealed record ShapeSource(Guid GroupId, string Provider, string Definition)
{
    private string _definition = Definition;
    private ShapeDefinition _geometry;
    private volatile bool _parsed;
    // Keep the provider payload in the existing persistence contract, including PDF ink data.
    public string Definition
    {
        get => _definition ??= _geometry is null ? null : JsonSerializer.Serialize(_geometry);
        init { _definition = value; _geometry = null; _parsed = false; }
    }

    [JsonIgnore]
    public ShapeDefinition Geometry
    {
        get
        {
            if (_parsed || Provider != ShapeDefinition.Provider) return _geometry;
            try
            {
                var geometry = JsonSerializer.Deserialize<ShapeDefinition>(_definition ?? "null");
                if (geometry?.IsValid == true) _geometry = geometry;
            }
            catch (JsonException) { }
            _parsed = true;
            return _geometry;
        }
    }

    public static ShapeSource FromGeometry(Guid id, ShapeDefinition geometry) =>
        new(id, ShapeDefinition.Provider, null) { _geometry = geometry, _parsed = true };

    /// <summary>Share one provider payload per shape part type when opening a multi-contour object.</summary>
    public static void ShareDefinitions(IReadOnlyList<InkStroke> strokes)
    {
        var sources = new Dictionary<ShapeSource, ShapeSource>();
        foreach (var stroke in strokes)
        {
            if (stroke.ShapeSource is not { } source) continue;
            if (sources.TryGetValue(source, out var shared)) stroke.ShapeSource = shared;
            else sources.Add(source, source);
        }
    }

    public bool Equals(ShapeSource other) => other is not null && GroupId == other.GroupId
        && Provider == other.Provider && Definition == other.Definition;
    public override int GetHashCode() => HashCode.Combine(GroupId, Provider, Definition);
}
