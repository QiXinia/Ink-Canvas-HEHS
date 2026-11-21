using System.Buffers.Binary;
using System.Text.Json;

namespace InkCanvas.Extensibility;

/// <summary>Length-bounded, one-request/one-response protocol shared by host and worker.</summary>
public static class PluginWire
{
    public const int MaximumMessageBytes = 1024 * 1024;
    public static async ValueTask WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length > MaximumMessageBytes) throw new InvalidDataException("Plugin message exceeds 1 MiB.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(payload, token); await stream.FlushAsync(token);
    }
    public static async ValueTask<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size <= 0 || size > MaximumMessageBytes) throw new InvalidDataException("Invalid plugin message length.");
        byte[] payload = new byte[size]; await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Empty plugin message.");
    }

    public static void ValidateResult(PluginCommandResult result, IReadOnlyCollection<PluginCapability> capabilities)
    {
        if (result is null) throw new InvalidDataException("Plugin returned no result.");
        if (result.Notification?.Length > 4096 || result.Notification is not null && !capabilities.Contains(PluginCapability.Notifications))
            throw new InvalidDataException("Plugin notification is invalid or was not declared.");
        if (result.Strokes is null || result.Strokes.Length > 256 || result.Strokes.Length > 0 && !capabilities.Contains(PluginCapability.CanvasWrite))
            throw new InvalidDataException("Plugin canvas output is invalid or was not declared.");
        int points = 0;
        foreach (var stroke in result.Strokes)
        {
            if (stroke is null || !float.IsFinite(stroke.Width) || stroke.Width is < .5f or > 120
                || stroke.Points is not { Length: > 0 and <= 4096 } || (points += stroke.Points.Length) > 16384)
                throw new InvalidDataException("Invalid plugin stroke geometry.");
            foreach (var point in stroke.Points)
                if (point is null || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || Math.Abs(point.X) > 100000 || Math.Abs(point.Y) > 100000)
                    throw new InvalidDataException("Invalid plugin coordinates.");
        }
    }
}
