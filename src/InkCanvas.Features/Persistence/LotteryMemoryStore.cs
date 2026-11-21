using System.Text.Json;
using InkCanvas.Features.Tools;

namespace InkCanvas.Features.Persistence;

/// <summary>Coalesces cooldown updates and atomically writes them off the drawing thread.</summary>
public sealed class LotteryMemoryStore : IDisposable
{
    private readonly LotteryService _service;
    private readonly string _path;
    private readonly System.Threading.Timer _timer;
    private readonly object _writeGate = new();
    private long _generation, _savedGeneration;
    private volatile bool _disposed;
    public string LastError { get; private set; }

    public LotteryMemoryStore(LotteryService service, string directory)
    {
        _service = service;
        _path = Path.Combine(directory, "LotteryCooldown.json");
        try
        {
            if (File.Exists(_path)) _service.RestoreCooldownMemory(JsonSerializer.Deserialize<LotteryCooldownMemory>(File.ReadAllText(_path)));
        }
        catch (Exception exception) { LastError = exception.Message; }
        _timer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _service.CooldownMemoryChanged += Schedule;
    }

    private void Schedule()
    {
        if (_disposed) return;
        Interlocked.Increment(ref _generation);
        try { _timer.Change(200, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public void Flush()
    {
        lock (_writeGate)
        {
            long generation = Volatile.Read(ref _generation);
            if (generation == _savedGeneration) return;
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(temporary, JsonSerializer.Serialize(_service.CaptureCooldownMemory()));
                File.Move(temporary, _path, overwrite: true);
                _savedGeneration = generation; LastError = null;
            }
            catch (Exception exception) { LastError = exception.Message; }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }
    }

    public void Dispose()
    {
        lock (_writeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _service.CooldownMemoryChanged -= Schedule;
            _timer.Dispose();
            Flush();
        }
    }
}
