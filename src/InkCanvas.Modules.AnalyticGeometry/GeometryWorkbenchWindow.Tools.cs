using InkCanvas.AnalyticGeometry.Construction;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed partial class GeometryWorkbenchWindow
{
    private sealed record DrawingTool(string Name, string Command, int Points, string Help);
    private static readonly DrawingTool[] DrawingTools =
    {
        new("移动 / 选择", "", 0, "拖动自由点可更新关联对象。拖动空白处平移，双指捏合缩放。3D 中拖动空白处旋转视图。"),
        new("点", "Point", 1, "点击视图放置自由点。"),
        new("线段", "Segment", 2, "依次选择两个点，或点击视图建立点。"),
        new("直线", "Line", 2, "依次选择两个点。"),
        new("射线", "Ray", 2, "依次选择端点与方向点。"),
        new("向量", "Vector", 2, "依次选择起点与终点。"),
        new("圆", "Circle", 2, "依次选择圆心与圆上一点。"),
        new("三点圆", "Circumcircle", 3, "依次选择圆上的三个点。"),
        new("中点", "Midpoint", 2, "依次选择两个点。"),
        new("垂直平分线", "PerpendicularBisector", 2, "依次选择线段的两个端点。"),
        new("角平分线", "AngleBisector", 3, "依次选择角的起点、顶点与终点。"),
        new("多边形", "Polygon", -1, "依次选择顶点，再点第一个顶点完成多边形。"),
        new("圆弧", "Arc", 3, "依次选择圆心、起点、终点，沿逆时针方向作弧。"),
        new("扇形", "Sector", 3, "依次选择圆心、起点和终点。"),
        new("半圆", "Semicircle", 2, "依次选择直径的两个端点。"),
        new("三点平面", "Plane", 3, "依次选择三个空间点。空间坐标可在输入栏中指定。"),
    };

    private void GraphClicked(GeoVector point, string hit)
    {
        if (_tools.SelectedIndex <= 0)
        {
            if (hit is not null && _rows.TryGetValue(hit, out var row)) _objects.SelectedItem = row.Item;
            if (hit is not null) RunEventScript(Document.State.Objects.First(item => item.Label == hit).OnClick);
            return;
        }
        Guard(() =>
        {
            var tool = DrawingTools[_tools.SelectedIndex];
            Document.BeginGesture();
            string label;
            if (hit is not null && Evaluation?.Values.TryGetValue(hit, out var value) == true && value.Kind == GeometryKind.Point) label = hit;
            else
            {
                label = ConstructionDocument.NextLabel(Document.State.Objects, "P");
                Document.Execute(label + "=" + Coordinates(point));
            }
            if (tool.Command == "Point") { Document.EndGesture(); return; }
            if (tool.Points == -1 && _toolPoints.Count >= 3 && label == _toolPoints[0])
            {
                Document.Execute("Polygon(" + string.Join(',', _toolPoints) + ")"); _toolPoints.Clear();
            }
            else
            {
                if (_toolPoints.Count == 0 || _toolPoints[^1] != label) _toolPoints.Add(label);
                if (_toolPoints.Count == tool.Points)
                {
                    Document.Execute(tool.Command + "(" + string.Join(',', _toolPoints) + ")"); _toolPoints.Clear();
                }
            }
            Document.EndGesture();
            SetStatus(tool.Help + (_toolPoints.Count > 0 ? " · 已选择 " + _toolPoints.Count + " 个点" : ""));
        });
    }

    private static readonly (string Group, string Name, string Input)[] Examples =
    {
        ("点线与构造", "两点直线与中点", "A=(-3,0);B=(3,2);l=Line(A,B);M=Midpoint(A,B)"),
        ("点线与构造", "垂线与平行线", "A=(1,3);l=Line((-3,0),(4,1));p=Perpendicular(A,l);q=Parallel(A,l)"),
        ("点线与构造", "圆与切线", "c=Circle((0,0),3);A=(5,0);g=Tangent(A,c)"),
        ("点线与构造", "两圆交点", "c=Circle((0,0),3);d=Circle((3,0),2);I=Intersect(c,d)"),
        ("点线与构造", "正多边形", "g=RegularPolygon((-2,0),(2,0),6)"),
        ("变换与度量", "旋转与平移", "g=Polygon((0,0),(3,0),(1,2));h=Rotate(g,pi/3,(0,0));k=Translate(h,Vector((3,2)))"),
        ("变换与度量", "圆反演", "c=Circle((0,0),3);A=(4,2);B=Reflect(A,c)"),
        ("变换与度量", "关系检验", "l=Line((0,0),(2,1));m=Parallel((0,3),l);r=Relation(l,m,\"parallel\")"),
        ("函数与圆锥曲线", "参数驱动函数", "a=1;f(x)=a*sin(x);d=Derivative(f,x);R=Root(f);E=Extremum(f)"),
        ("函数与圆锥曲线", "椭圆及焦点", "c=Ellipse((-3,0),(3,0),5);F=Focus(c);d=Directrix(c)"),
        ("函数与圆锥曲线", "双曲线及渐近线", "c=Hyperbola((-4,0),(4,0),2);a=Asymptote(c)"),
        ("函数与圆锥曲线", "隐式曲线", "a=2;g=x^3+y^3-3*a*x*y=0"),
        ("函数与圆锥曲线", "参数曲线与极坐标", "g=Curve(4*cos(t),3*sin(t),t,0,2*pi);h=PolarCurve(3*cos(5*t),0,2*pi)"),
        ("函数与圆锥曲线", "不等式可行域", "g=x>=0 && y>=0 && x+y<=5"),
        ("微积分", "切线与密切圆", "f(x)=x^2/4;T=Tangent((2,0),f);c=OsculatingCircle(f,2);k=Curvature(f,2)"),
        ("微积分", "定积分区域", "f(x)=sin(x);g=IntegralArea(f,0,pi)"),
        ("微积分", "方向场与微分方程", "g=SlopeField(x-y);h=SolveODE(x-y,0,1,6)"),
        ("三维", "立体与截面", "g=Cube((-2,-2,-2),4);p=Plane((0,0,0),Vector((1,1,1)));c=Intersect(p,g)"),
        ("三维", "多边形拉伸", "p=RegularPolygon((-2,-1),(2,-1),5);g=Prism(p,4);h=Pyramid(p,6)"),
        ("三维", "正多面体", "g=Dodecahedron((0,0,0),2);v=Volume(g);a=SurfaceArea(g)"),
        ("三维", "球、圆柱与圆锥", "g=Sphere((-4,0,0),2);h=Cylinder((0,0,0),(0,0,4),2);k=Cone((5,0,0),(5,0,4),2)"),
        ("三维", "函数曲面", "g=Surface(sin(x)*cos(y),-5,5,-5,5)"),
        ("三维", "空间参数曲线", "g=Curve(3*cos(t),3*sin(t),t/2,t,-6,6)"),
        ("三维", "参数曲面", "g=Surface((3+cos(v))*cos(u),(3+cos(v))*sin(u),sin(v),u,0,2*pi,v,0,2*pi)"),
        ("三维", "隐式曲面", "g=x^2+y^2-z^2=4"),
        ("三维", "多面体动态展开", "a=1;g=Cube((-2,-2,-2),4);n=Net(g,a)"),
        ("三维", "旋转曲面", "g=Curve(2+t,0,t,0,4);l=Line((0,0,0),(0,0,1));s=Revolve(g,l)"),
        ("批量构造", "点序列", "g=Sequence((k, sin(k)),k,-8,8,0.5)"),
        ("批量构造", "Zip 列表映射", "g=Zip(k^2,k,{1,2,3,4})"),
        ("批量构造", "参数轨迹", "a=0;P=(a,a^2);g=Locus(P,a,-3,3)"),
        ("批量构造", "符号证明", "A=(-2,1);B=(3,4);M=Midpoint(A,B);p=ProveDetails(AreCollinear(A,M,B))"),
        ("批量构造", "自定义工具", "DefineTool(DiameterCircle,{U,V},Circle(Midpoint(U,V),Distance(U,V)/2));A=(-3,0);B=(3,2);c=DiameterCircle(A,B)"),
        ("批量构造", "交互控件", "a=2;f(x)=a*x^2;show=Checkbox(true);input=InputBox(a);choice=Dropdown({1,2,3},2);next=Button(\"下一步\")"),
        ("CAS", "解方程", "Solve(x^3-2*x-1=0,x)"),
        ("CAS", "解方程组", "Solve({x+y=5,x-y=1},{x,y})"),
        ("CAS", "因式分解", "Factor(x^4-1)"),
        ("CAS", "定积分", "Integral(x^2,x,0,3)"),
        ("CAS", "极限", "Limit(sin(x)/x,x,0)"),
        ("CAS", "矩阵求逆", "Inverse([[1,2],[3,4]])"),
        ("CAS", "复数", "Expand((2+3*i)^3)"),
    };
    internal static IEnumerable<(string Group, string Name, string Input)> ExampleCommands => Examples;

    private void ShowCommandHelp()
    {
        var panel = new StackPanel { Spacing = 8, Width = 460 };
        foreach (var group in Examples.GroupBy(example => example.Group))
        {
            var rows = new StackPanel { Spacing = 4 };
            foreach (var example in group)
            {
                var button = new Button { Content = example.Name, MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
                button.Click += (_, _) =>
                {
                    _input.Text = example.Input;
                    if (example.Group == "三维") Document.SetView(Document.State with { ThreeDimensional = true });
                    SetStatus(example.Group == "CAS" ? "点击“CAS 计算”查看精确结果。" : "点击“创建 / 更新”加入构造。相同名称会更新已有对象。");
                };
                ToolTipService.SetToolTip(button, example.Input); rows.Children.Add(button);
            }
            panel.Children.Add(new Expander { Header = group.Key, Content = rows, HorizontalAlignment = HorizontalAlignment.Stretch });
        }
        var protocol = new Slider { Header = "构造步骤", Minimum = 0, Maximum = Document.State.Objects.Count, Value = Document.State.Objects.Count, StepFrequency = 1 };
        protocol.ValueChanged += (_, _) => { _protocolStep = (int)protocol.Value; RenderView(); };
        panel.Children.Add(protocol);
        var all = new Button { Content = "显示完整构造", MinHeight = 44 };
        all.Click += (_, _) => { _protocolStep = int.MaxValue; RenderView(); }; panel.Children.Add(all);
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 620 }, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        flyout.ShowAt(_input);
    }
}
