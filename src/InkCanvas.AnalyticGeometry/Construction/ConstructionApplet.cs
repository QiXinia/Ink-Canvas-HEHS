using System.Text.Json;

namespace InkCanvas.AnalyticGeometry.Construction;

public sealed partial class ConstructionDocument
{
    internal const int MaximumAppletScriptLength = 65536;
    internal const int MaximumAppletListeners = 256;

    private static readonly HashSet<string> AppletEventNames = new(StringComparer.Ordinal)
    {
        "OnAdd", "OnRemove", "OnUpdate", "OnClick", "OnRename", "OnClear", "OnStoreUndo", "OnClient",
    };

    private static bool ValidAppletScripts(ConstructionSnapshot state)
        => state.AppletScript is not null && state.AppletScript.Length <= MaximumAppletScriptLength
            && state.AppletListeners is not null && state.AppletListeners.Count <= MaximumAppletListeners
            && state.AppletListeners.All(listener => listener is not null && listener.EventName is not null
                && AppletEventNames.Contains(listener.EventName) && !string.IsNullOrWhiteSpace(listener.Callback)
                && listener.Callback.Length <= 4096
                && (listener.ObjectId is null || listener.EventName is "OnClick" or "OnUpdate"));

    public void InitializeApplet()
    {
        if (State.AppletScript.Length == 0) return;
        RunJavaScript("if (typeof ggbOnInit === 'function') ggbOnInit('ggbApplet', ggbApplet);", "OnOpen", "", "", initializeApplet: true);
    }

    public void RunAppletEvent(string eventName, string target, string previousTarget, string argument = null,
        IReadOnlyDictionary<string, double> fields = null)
    {
        Guid? id = State.Objects.FirstOrDefault(item => item.Label == target)?.Id;
        var callbacks = State.AppletListeners.Where(listener => listener.EventName == "OnClient"
            || listener.EventName == eventName && (listener.ObjectId is null || listener.ObjectId == id)).ToArray();
        if (callbacks.Length == 0) return;

        string clientType = eventName switch
        {
            "OnViewChanged" => State.ThreeDimensional ? "viewChanged3D" : "viewChanged2D",
            _ => eventName.Length > 2 ? char.ToLowerInvariant(eventName[2]) + eventName[3..] : eventName,
        };
        var payload = new Dictionary<string, object>
        {
            ["type"] = clientType,
            ["target"] = target,
            ["argument"] = argument ?? previousTarget,
        };
        if (fields is not null)
            foreach (var field in fields)
                if (double.IsFinite(field.Value)) payload.TryAdd(field.Key, field.Value);
        string clientEvent = JsonSerializer.Serialize(payload);
        string arguments = eventName switch
        {
            "OnRename" => JsonSerializer.Serialize(previousTarget) + "," + JsonSerializer.Serialize(target),
            "OnClear" or "OnStoreUndo" => "",
            _ => JsonSerializer.Serialize(target),
        };
        string GetInvocation(ConstructionListener listener)
        {
            return "(" + listener.Callback + ")(" + (listener.EventName == "OnClient" ? clientEvent : arguments) + ");";
        }
        RunJavaScript(string.Join('\n', callbacks.Select(GetInvocation)), eventName, target, previousTarget, argument: argument);
    }

    private void ChangeAppletListener(string eventName, string objectLabel, string callback, bool register)
    {
        Guid? id = null;
        if (objectLabel is not null)
        {
            id = State.Objects.FirstOrDefault(item => item.Label == objectLabel)?.Id;
            if (id is null) return;
        }

        var listeners = State.AppletListeners.Where(listener => listener.EventName != eventName
            || listener.ObjectId != id || id is null && listener.Callback != callback).ToList();
        if (register) listeners.Add(new(eventName, id, callback));
        if (!listeners.SequenceEqual(State.AppletListeners)) Commit(State with { AppletListeners = listeners });
    }
}
