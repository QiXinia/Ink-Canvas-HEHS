using InkCanvas.AnalyticGeometry.Algebra;
using Jint;

namespace InkCanvas.AnalyticGeometry.Construction;

public sealed partial class ConstructionDocument
{
    private void RunJavaScript(string source)
    {
        var transaction = new ConstructionDocument(); transaction.Load(State);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var engine = new Engine(options => options.MaxStatements(10000).LimitMemory(8 * 1024 * 1024)
            .LimitRecursion(32).TimeoutInterval(TimeSpan.FromMilliseconds(500)).CancellationToken(cancellation.Token));
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
        engine.SetValue("evalCommand", new Action<string>(command => transaction.Execute(command)));
        engine.SetValue("getValue", new Func<string, double>(label => Read(label).Number));
        engine.SetValue("getXcoord", new Func<string, double>(label => Read(label).Origin.X));
        engine.SetValue("getYcoord", new Func<string, double>(label => Read(label).Origin.Y));
        engine.SetValue("getZcoord", new Func<string, double>(label => Read(label).Origin.Z));
        engine.SetValue("setValue", new Action<string, double>((label, number) =>
            transaction.RunScript("SetValue(" + label + "," + ComputerAlgebra.Number(number) + ")")));
        engine.SetValue("setCoords", new Action<string, double, double>((label, x, y) =>
            transaction.RunScript("SetCoords(" + label + "," + ComputerAlgebra.Number(x) + "," + ComputerAlgebra.Number(y) + ")")));
        engine.SetValue("setVisible", new Action<string, bool>((label, visible) => transaction.RunScript("SetVisible(" + label + "," + (visible ? "true" : "false") + ")")));
        engine.Execute("const ggbApplet = Object.freeze({evalCommand,getValue,getXcoord,getYcoord,getZcoord,setValue,setCoords,setVisible});");
        engine.Execute(source);
        Commit(transaction.State);
    }
}
