using InkCanvas.AnalyticGeometry.Algebra;
using Jint;

namespace InkCanvas.AnalyticGeometry.Construction;

public sealed partial class ConstructionDocument
{
    private void RunJavaScript(string source, string eventName, string target, string previousTarget, bool initializeApplet = false, string argument = null)
    {
        var transaction = new ConstructionDocument(); transaction.Load(State);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var engine = new Engine(options => options.MaxStatements(10000).LimitMemory(8 * 1024 * 1024)
            .LimitRecursion(32).TimeoutInterval(TimeSpan.FromMilliseconds(500)).CancellationToken(cancellation.Token)
            .RetainFunctionSourceText(true));
        var kernel = new ConstructionKernel();
        ConstructionEvaluation evaluation = null;
        int revision = -1;
        GeometryValue Read(string label)
        {
            if (revision != transaction.Revision)
            { evaluation = kernel.Evaluate(transaction.State, cancellation.Token); revision = transaction.Revision; }
            if (!evaluation.Values.TryGetValue(label, out var value) || !value.Defined) throw new FormatException("脚本对象不存在或未定义 · " + label);
            return value;
        }
        bool replayingApplet = !initializeApplet;
        engine.SetValue("evalCommand", new Func<string, bool>(command =>
        {
            if (!replayingApplet) transaction.Execute(command);
            return true;
        }));
        engine.SetValue("getValue", new Func<string, double>(label => Read(label).Number));
        engine.SetValue("getXcoord", new Func<string, double>(label => Read(label).Origin.X));
        engine.SetValue("getYcoord", new Func<string, double>(label => Read(label).Origin.Y));
        engine.SetValue("getZcoord", new Func<string, double>(label => Read(label).Origin.Z));
        engine.SetValue("setValue", new Action<string, double>((label, number) =>
        {
            if (replayingApplet) return;
            transaction.Update(label, item => item with
            {
                Definition = MathSyntax.Call(item.Definition, out string command, out _)
                    && command.Equals("Checkbox", StringComparison.OrdinalIgnoreCase)
                    ? (number == 1 ? "Checkbox(true)" : "Checkbox(false)") : ComputerAlgebra.Number(number),
            });
        }));
        engine.SetValue("setCoords", new Action<string, double, double, double?>((label, x, y, z) =>
        {
            if (replayingApplet) return;
            if (!double.IsFinite(x) || !double.IsFinite(y) || z is double depthValue && !double.IsFinite(depthValue))
                throw new FormatException("对象坐标必须为有限实数。");
            var value = Read(label);
            if (value.ControlKind.Length > 0 || value.Kind == GeometryKind.Scalar
                && transaction.State.Objects.Any(item => item.Label == label && item.ShowSlider))
            {
                transaction.Update(label, item => item with { ControlPositionSet = true, ControlX = x, ControlY = y });
                return;
            }
            string coordinates = ComputerAlgebra.Number(x) + "," + ComputerAlgebra.Number(y);
            if (z is double depth) coordinates += "," + ComputerAlgebra.Number(depth);
            transaction.Update(label, item => item with
            {
                Definition = value.Kind == GeometryKind.Vector ? "Vector((" + coordinates + "))" : "(" + coordinates + ")",
            });
        }));
        engine.SetValue("setVisible", new Action<string, bool>((label, visible) =>
        {
            if (!replayingApplet) transaction.Update(label, item => item with { Visible = visible });
        }));
        engine.SetValue("eventType", eventName);
        engine.SetValue("eventTarget", target);
        engine.SetValue("previousEventTarget", previousTarget);
        engine.SetValue("eventArgument", argument ?? previousTarget);
        engine.SetValue("setEvent", new Action<string, string, string>((label, name, script) =>
        {
            if (!replayingApplet) transaction.RegisterEvent(label, name, script);
        }));
        engine.SetValue("changeAppletListener", new Action<string, string, string, bool>((name, label, callback, register) =>
        {
            if (!replayingApplet) transaction.ChangeAppletListener(name, label, callback, register);
        }));
        engine.Execute("""
            const ggbApplet = (() => {
                const api = {evalCommand,getValue,getXcoord,getYcoord,getZcoord,setValue,setCoords,setVisible,setEvent};
                const callbackSource = callback => {
                    if (typeof callback === "string") return callback;
                    if (callback.name && globalThis[callback.name] === callback) return callback.name;
                    const source = Function.prototype.toString.call(callback);
                    if (source.includes("[native code]")) throw new TypeError("Expected a JavaScript listener");
                    return source;
                };
                for (const event of ["Add","Remove","Update","Click","Rename","Clear","StoreUndo","Client"]) {
                    api["register" + event + "Listener"] = callback => changeAppletListener("On" + event, null, callbackSource(callback), true);
                    api["unregister" + event + "Listener"] = callback => changeAppletListener("On" + event, null, callbackSource(callback), false);
                }
                for (const event of ["Update","Click"]) {
                    api["registerObject" + event + "Listener"] = (label, callback) => changeAppletListener("On" + event, label, callbackSource(callback), true);
                    api["unregisterObject" + event + "Listener"] = label => changeAppletListener("On" + event, label, "", false);
                }
                return Object.freeze(api);
            })();
            """);
        if (transaction.State.AppletScript.Length != 0) engine.Execute(transaction.State.AppletScript);
        replayingApplet = false;
        engine.Execute(source);
        if (!ReferenceEquals(State, transaction.State)) Commit(transaction.State);
    }
}
