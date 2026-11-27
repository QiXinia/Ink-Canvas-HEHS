using InkCanvas.AnalyticGeometry.Construction;
using InkCanvas.Controls.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using static InkCanvas.Modules.AnalyticGeometry.GeometryToolInput;

namespace InkCanvas.Modules.AnalyticGeometry;

public sealed partial class GeometryWorkbenchWindow
{
    private const string AttachDetachTool = "AttachDetachPoint";
    private sealed record NumberInput(string Name, double Default, double Minimum, double Maximum, bool Integer = false);
    private sealed record ToolRecipe(GeometryToolInput[] Picks, string Format = null, NumberInput Number = null);
    private sealed record DrawingTool(string Category, string Name, string Command, string Help, string Icon, int GeoGebraMode,
        ToolRecipe[] Recipes, int MinimumPoints = 0, bool CloseAtFirst = false);

    private static readonly NumberInput PositiveLength = new("长度", 3, 1e-8, 1e6);
    private static readonly NumberInput Radius = new("半径", 3, 1e-8, 1e6);
    private static readonly NumberInput Degrees = new("角度（度，正值为逆时针）", 45, -36000, 36000);

    private static ToolRecipe Pick(params GeometryToolInput[] picks) => new(picks);
    private static DrawingTool PointsTool(string category, string name, string command, string help, string icon, int mode, int count)
        => new(category, name, command, help, icon, mode, [Pick(Enumerable.Repeat(Point, count).ToArray())]);

    private static readonly DrawingTool[] DrawingTools =
    [
        new("点与基本绘图", "移动 / 选择", "", "拖动自由点可更新关联对象。拖动空白处平移，双指捏合缩放。3D 中拖动空白处旋转视图。", "move", 0, []),
        PointsTool("点与基本绘图", "点", "Point", "点击画布放置自由点。", "point", 1, 1),
        new("点与基本绘图", "对象上的点", "PointOnObject", "点击路径或区域内部放置约束点。", "attachpoint", 501, [Pick(ConstraintPath | Region)]),
        new("点与基本绘图", "中点 / 中心", "Midpoint", "选择两个点、一条线段、圆、椭圆或双曲线。", "midpoint", 19,
            [Pick(Segment | CenteredConic), Pick(Point, Point)]),
        new("点与基本绘图", "交点", "Intersect", "依次选择两个对象以求交点，或直接点击交会处。", "intersect", 5,
            [Pick(Linear, Linear | Conic | Function), Pick(Conic, Linear | Conic), Pick(Function, Function | Linear)]),
        new("点与基本绘图", "附着 / 脱离", AttachDetachTool, "选择自由点以附着，或选择已附着的点以脱离。", "attachpoint", 67, [Pick(Point)]),
        PointsTool("点与基本绘图", "线段", "Segment", "依次选择两个端点。", "segment", 15, 2),
        PointsTool("点与基本绘图", "直线", "Line", "依次选择两个点。", "line", 2, 2),
        PointsTool("点与基本绘图", "射线", "Ray", "依次选择端点与方向点。", "ray", 18, 2),
        PointsTool("点与基本绘图", "向量", "Vector", "依次选择起点与终点。", "vector", 7, 2),
        new("点与基本绘图", "定长线段", "Segment", "选择起点后输入长度。", "segment", 45, [new([Point], Number: PositiveLength)]),
        new("点与基本绘图", "相等向量", "VectorFromPoint", "选择起点，再选择要复制的向量。", "vector", 37, [Pick(Point, Vector)]),

        new("直线与函数构造", "垂直平分线", "PerpendicularBisector", "选择一条线段或两个端点。", "perpbisector", 8,
            [Pick(Segment), Pick(Point, Point)]),
        new("直线与函数构造", "角平分线", "AngleBisector", "选择两条直线，或依次选择起点、顶点和终点。", "anglebisector", 9,
            [Pick(Linear, Linear), Pick(Point, Point, Point)]),
        new("直线与函数构造", "平行线", "Parallel", "选择经过点与基准线，顺序不限。", "parallel", 3,
            [Pick(Point, Linear), new([Linear, Point], "Parallel({1},{0})")]),
        new("直线与函数构造", "垂线", "Perpendicular", "选择经过点与基准线，顺序不限。", "perpbisector", 4,
            [Pick(Point, Linear), new([Linear, Point], "Perpendicular({1},{0})")]),
        new("直线与函数构造", "切线", "Tangent", "选择点和函数或圆锥曲线、方向线和圆锥曲线，或两条圆锥曲线。", "tangent", 13,
            [Pick(Point, Function | Conic), Pick(Linear, Conic), Pick(Conic, Conic),
                new([Conic, Point | Linear], "Tangent({1},{0})"), new([Function, Point], "Tangent({1},{0})")]),
        new("直线与函数构造", "极线 / 径线", "Polar", "选择点、直线或向量，再选择圆锥曲线。", "tangent", 44,
            [Pick(Point, Conic), new([Linear | Vector, Conic], "Diameter({0},{1})")]),
        new("直线与函数构造", "拟合直线", "FitLine", "依次选择至少两个点，再点击完成。", "fitline", 58, [], MinimumPoints: 2),
        new("直线与函数构造", "轨迹", "Locus", "依次选择依赖点和驱动它的路径点。", "curve", 47, [Pick(Point, PathPoint)]),
        new("直线与函数构造", "零点", "Root", "选择函数图像，标出当前视图区间内的实零点。", "intersect", 76, [Pick(Function)]),
        new("直线与函数构造", "极值点", "Extremum", "选择函数图像，标出当前视图区间内的极值点。", "curve", 75, [Pick(Function)]),

        new("多边形", "多边形", "Polygon", "依次选择顶点，回点首个顶点或点击完成。", "polygon", 16, [], 3, true),
        new("多边形", "折线", "PolyLine", "依次选择顶点，再点击完成。", "polyline", 65, [], 2),
        new("多边形", "正多边形", "RegularPolygon", "选择一条边的两个端点，再输入边数。", "polygon", 51,
            [new([Point, Point], Number: new("边数", 5, 3, 256, true))]),
        new("多边形", "刚体多边形", "RigidPolygon", "依次选择顶点并完成。移动前两个顶点可平移、转动整个图形。", "polygon", 64, [], 3, true),
        new("多边形", "向量多边形", "VectorPolygon", "依次选择顶点并完成。移动首点可平移整个图形。", "polygon", 70, [], 3, true),

        PointsTool("圆与圆锥曲线", "圆", "Circle", "依次选择圆心与圆上一点。", "circle", 10, 2),
        new("圆与圆锥曲线", "定半径圆", "Circle", "选择圆心后输入半径。", "circle", 34, [new([Point], Number: Radius)]),
        PointsTool("圆与圆锥曲线", "三点圆", "Circumcircle", "依次选择圆上的三个点。", "circumcircle", 11, 3),
        new("圆与圆锥曲线", "圆规", "Compass", "选择线段、已有圆或两个点确定半径，再选择圆心。", "circle", 53,
            [Pick(Segment | Circle, Point), Pick(Point, Point, Point)]),
        PointsTool("圆与圆锥曲线", "圆弧", "Arc", "依次选择圆心、起点、终点，沿逆时针方向作弧。", "arc", 20, 3),
        PointsTool("圆与圆锥曲线", "扇形", "Sector", "依次选择圆心、起点和终点。", "sector", 21, 3),
        PointsTool("圆与圆锥曲线", "外接圆弧", "Circumarc", "依次选择弧的起点、经过点和终点。", "arc", 22, 3),
        PointsTool("圆与圆锥曲线", "外接扇形", "Circumsector", "依次选择弧的起点、经过点和终点。", "sector", 23, 3),
        PointsTool("圆与圆锥曲线", "半圆", "Semicircle", "依次选择直径的两个端点。", "semicircle", 24, 2),
        PointsTool("圆与圆锥曲线", "椭圆", "Ellipse", "依次选择两个焦点及曲线上的一点。", "ellipse", 55, 3),
        PointsTool("圆与圆锥曲线", "双曲线", "Hyperbola", "依次选择两个焦点及曲线上的一点。", "hyperbola", 56, 3),
        new("圆与圆锥曲线", "抛物线", "Parabola", "选择焦点与准线。", "curve", 57,
            [Pick(Point, Linear), new([Linear, Point], "Parabola({1},{0})")]),
        PointsTool("圆与圆锥曲线", "五点圆锥曲线", "Conic", "依次选择曲线上的五个点。", "ellipse", 12, 5),

        new("度量", "角度", "Angle", "选择起点、顶点和终点、两条线，或选择多边形标注内角。", "anglebisector", 36,
            [Pick(Point, Point, Point), Pick(Linear | Vector, Linear | Vector), Pick(PolygonRegion)]),
        new("度量", "定角", "Angle", "选择角的起点和顶点，再输入角度。", "anglebisector", 46,
            [new([Point, Point], Number: Degrees)]),
        new("度量", "距离 / 长度", "Distance", "选择两个点、点与直线，或单选线段、向量、圆、椭圆和多边形。", "measure", 38,
            [new([FiniteLength], "Length({0})"), new([PolygonRegion | Circle | GeometryToolInput.Ellipse], "Perimeter({0})"),
                Pick(Point, Point | Linear), new([InfiniteLine, Point], "Distance({1},{0})")]),
        new("度量", "面积", "Area", "选择多边形、圆、椭圆或有面积的区域。", "polygon", 49, [Pick(AreaRegion)]),
        new("度量", "斜率", "Slope", "选择一条直线、射线或线段。", "fitline", 50, [Pick(Linear)]),

        new("变换", "轴对称", "Reflect", "选择要变换的对象，再选择对称轴。", "reflect", 30, [Pick(Transformable, Linear)]),
        new("变换", "中心对称", "Reflect", "选择要变换的对象，再选择对称中心。", "reflect", 29, [Pick(Transformable, Point)]),
        new("变换", "圆反演", "Reflect", "选择要反演的对象，再选择反演圆。", "circle", 54, [Pick(Transformable, Circle)]),
        new("变换", "按向量平移", "Translate", "选择要平移的对象，再选择向量。", "vector", 31, [Pick(Transformable, Vector)]),
        new("变换", "定角旋转", "Rotate", "选择对象与旋转中心，再输入角度。", "rotate", 32,
            [new([Transformable, Point], "Rotate({0},{2}*pi/180,{1})", Degrees)]),
        new("变换", "位似", "Dilate", "选择对象与位似中心，再输入比例。", "polygon", 33,
            [new([Transformable, Point], "Dilate({0},{2},{1})", new("比例（非零）", 2, -1e6, 1e6))]),

        PointsTool("空间构造", "三点平面", "Plane", "依次选择三个空间点。空间坐标可在输入栏中指定。", "plane", 510, 3),
    ];

    private readonly List<TouchIconButton> _toolButtons = new();
    private int _activeToolIndex;

    private FrameworkElement BuildToolsPanel()
    {
        var panel = new StackPanel { Spacing = 14, Padding = new Thickness(12, 8, 12, 16) };
        _toolButtons.Clear();
        for (int i = 0; i < DrawingTools.Length; i++) _toolButtons.Add(null);

        foreach (var group in DrawingTools.Select((tool, index) => (tool, index)).GroupBy(item => item.tool.Category))
        {
            panel.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Opacity = 0.72,
                Margin = new Thickness(2, 2, 0, 0),
            });

            var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
            const int columns = 3;
            for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var items = group.ToArray();
            int rows = (items.Length + columns - 1) / columns;
            for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int k = 0; k < items.Length; k++)
            {
                var (tool, toolIndex) = items[k];
                var button = new TouchIconButton
                {
                    Label = tool.Name,
                    LabelFontSize = 11,
                    IconAreaHeight = 30,
                    MinHeight = 62,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(4, 6, 4, 6),
                    CornerRadius = new CornerRadius(10),
                };
                button.SetContent(CreateToolGlyph(tool.Icon));
                AutomationProperties.SetName(button, "几何工具 " + tool.Name);
                ToolTipService.SetToolTip(button, tool.Help);
                int captured = toolIndex;
                button.Click += (_, _) => SelectTool(captured);
                Grid.SetColumn(button, k % columns);
                Grid.SetRow(button, k / columns);
                grid.Children.Add(button);
                _toolButtons[toolIndex] = button;
            }
            panel.Children.Add(grid);
        }

        SelectTool(0);
        return new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private void SelectTool(int index)
    {
        int previous = _activeToolIndex;
        _activeToolIndex = Math.Clamp(index, 0, DrawingTools.Length - 1);
        _view.ConstructionToolActive = _activeToolIndex > 0;
        _view.AcceptToolObject = _view.ConstructionToolActive ? AcceptToolObject : null;
        ClearToolSelection();
        for (int i = 0; i < _toolButtons.Count; i++)
            if (_toolButtons[i] is not null) _toolButtons[i].IsSelected = i == _activeToolIndex;

        var tool = DrawingTools[_activeToolIndex];
        if (_toolBanner is not null)
            _toolBanner.Visibility = _activeToolIndex > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (previous != _activeToolIndex)
            DispatchEvent("", "OnSetMode", tool.GeoGebraMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static UIElement CreateToolGlyph(string kind)
    {
        var canvas = new Canvas { Width = 28, Height = 28 };
        var accent = new SolidColorBrush(Color.FromArgb(255, 34, 105, 222));
        var line = new SolidColorBrush(Color.FromArgb(255, 72, 86, 106));
        void Dot(double x, double y, Brush fill = null)
        {
            var e = new Ellipse { Width = 6.5, Height = 6.5, Fill = fill ?? accent, Stroke = new SolidColorBrush(Microsoft.UI.Colors.White), StrokeThickness = 1 };
            Canvas.SetLeft(e, x - 3.25); Canvas.SetTop(e, y - 3.25); canvas.Children.Add(e);
        }
        void Seg(double x1, double y1, double x2, double y2, Brush stroke = null)
            => canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke ?? line, StrokeThickness = 1.8 });

        switch (kind)
        {
            case "move":
                canvas.Children.Add(new Polygon
                {
                    Points = new PointCollection { new(7, 4), new(7, 22), new(12, 17), new(16, 23), new(19, 21), new(15, 15), new(22, 15) },
                    Fill = line,
                });
                break;
            case "point":
                Dot(14, 14);
                break;
            case "attachpoint":
                Seg(4, 20, 24, 20); Seg(14, 8, 14, 20, accent); Dot(14, 8); Dot(14, 20, line);
                break;
            case "segment":
                Seg(6, 22, 22, 6); Dot(6, 22); Dot(22, 6);
                break;
            case "line":
                Seg(3, 25, 25, 3); Dot(9, 19); Dot(19, 9);
                break;
            case "ray":
                Seg(6, 22, 25, 3); Dot(6, 22); Dot(17, 11);
                break;
            case "vector":
                Seg(6, 22, 22, 6, accent); Seg(22, 6, 15, 7, accent); Seg(22, 6, 21, 13, accent); Dot(6, 22);
                break;
            case "circle":
            case "circumcircle":
                var c = new Ellipse { Width = 20, Height = 20, Stroke = line, StrokeThickness = 1.8 };
                Canvas.SetLeft(c, 4); Canvas.SetTop(c, 4); canvas.Children.Add(c);
                if (kind == "circle") { Dot(14, 14); Dot(24, 14); }
                else { Dot(14, 4); Dot(5, 18); Dot(23, 18); }
                break;
            case "midpoint":
                Seg(5, 22, 23, 6); Dot(5, 22, line); Dot(23, 6, line); Dot(14, 14, accent);
                break;
            case "perpbisector":
                Seg(5, 20, 23, 20); Seg(14, 4, 14, 24, accent); Dot(5, 20); Dot(23, 20);
                break;
            case "anglebisector":
                Seg(6, 22, 24, 22); Seg(6, 22, 18, 5); Seg(6, 22, 23, 12, accent); Dot(6, 22);
                break;
            case "parallel":
                Seg(4, 17, 18, 3); Seg(10, 24, 24, 10, accent); Dot(17, 17);
                break;
            case "intersect":
                Seg(4, 22, 24, 6); Seg(4, 6, 24, 22); Dot(14, 14);
                break;
            case "polyline":
            case "curve":
                Seg(4, 20, 10, 7); Seg(10, 7, 17, 18); Seg(17, 18, 24, 9);
                Dot(4, 20); Dot(24, 9);
                break;
            case "fitline":
                Seg(4, 22, 24, 6, accent); Dot(6, 19, line); Dot(13, 17, line); Dot(20, 7, line);
                break;
            case "measure":
                Seg(4, 16, 24, 16, accent); Seg(4, 10, 4, 22); Seg(24, 10, 24, 22); Seg(14, 12, 14, 16);
                break;
            case "reflect":
                Seg(14, 3, 14, 25); Seg(4, 20, 10, 8); Seg(24, 20, 18, 8, accent);
                break;
            case "polygon":
                canvas.Children.Add(new Polygon
                {
                    Points = new PointCollection { new(14, 5), new(23, 22), new(5, 22) },
                    Fill = new SolidColorBrush(Color.FromArgb(46, 34, 105, 222)),
                    Stroke = line,
                    StrokeThickness = 1.6,
                });
                Dot(14, 5); Dot(23, 22); Dot(5, 22);
                break;
            case "plane":
                canvas.Children.Add(new Polygon
                {
                    Points = new PointCollection { new(8, 7), new(24, 7), new(20, 21), new(4, 21) },
                    Fill = new SolidColorBrush(Color.FromArgb(46, 34, 105, 222)),
                    Stroke = accent,
                    StrokeThickness = 1.6,
                });
                Dot(8, 7); Dot(24, 7); Dot(4, 21);
                break;
            default:
                var arc = new Ellipse { Width = 18, Height = 18, Stroke = accent, StrokeThickness = 1.8 };
                Canvas.SetLeft(arc, 5); Canvas.SetTop(arc, 5); canvas.Children.Add(arc);
                Dot(14, 14);
                break;
        }
        return canvas;
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

    private void ShowCommandHelp(FrameworkElement anchor = null)
    {
        var panel = new StackPanel { Spacing = 8, Width = 460 };
        foreach (var group in Examples.GroupBy(example => example.Group))
        {
            var rows = new StackPanel { Spacing = 4 };
            foreach (var example in group)
            {
                var button = new Button { Content = example.Name, MinHeight = 42, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
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
        var protocol = new Slider { Header = "构造步骤回放", Minimum = 0, Maximum = Document.State.Objects.Count, Value = Document.State.Objects.Count, StepFrequency = 1 };
        protocol.ValueChanged += (_, _) => { _protocolStep = (int)protocol.Value; RenderView(); };
        panel.Children.Add(protocol);
        var all = new Button { Content = "显示完整构造", MinHeight = 40 };
        all.Click += (_, _) => { _protocolStep = int.MaxValue; RenderView(); }; panel.Children.Add(all);
        var flyout = new Flyout { Content = new ScrollViewer { Content = panel, MaxHeight = 620 }, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom };
        flyout.ShowAt(anchor ?? _input);
    }
}
