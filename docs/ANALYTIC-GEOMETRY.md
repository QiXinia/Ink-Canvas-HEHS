# 数学工作区与解析几何模块

`inkcanvas.analytic-geometry` 2.0 是独立安装的 WinUI 3 模块。界面使用项目的 `WindowFrame`、`SegmentedSelector`、`PointerSlider` 和墨迹输入控件，图形通过 Win2D 绘制。本地符号计算使用 AngouriMath 2.8，JavaScript 事件使用 Jint 4.18。依赖库和许可证随模块分发。

桌面与白板工具栏的“更多 → 解析几何”打开数学工作区。窗口采用左侧代数对象、对象属性、CAS 页面与右侧二维或三维视图的布局。底部提供指令输入、数学键盘和 Windows 本地手写候选。“指令”按钮提供可直接编辑的分类示例。

## 对象联动与操作

输入 `A=(1,2); B=(4,3); s=Segment(A,B); M=Midpoint(A,B)` 创建依赖构造。修改 A 的定义或拖动 A，会更新线段与中点。相同名称重新赋值会更新已有对象。自由点仅包含常量坐标，`P=(a,a^2)` 这样的依赖点通过其参数 a 驱动。

二维视图支持自由点拖动、空白处平移、滚轮与双指缩放。三维视图支持轨道旋转、正交、透视与倾斜投影。三维自由点可在 XY 平面内移动，在“视图”中开启“3D 拖点沿 Z 轴移动”后沿 Z 轴拖动。二维坐标轴保持等比例。

作图工具可直接创建点、线段、直线、射线、向量、圆、三点圆、中点、垂直平分线、角平分线、多边形、圆弧、扇形、半圆和三点平面。多边形再次点击起点完成。对象属性支持定义、可见性、固定、颜色、线宽、填充透明度、标签、轨迹跟踪和事件脚本。一次拖动使用一个撤销事务。

数值对象可配置滑动条上下限和步长，勾选“播放时驱动此参数”后使用顶部播放按钮。参数以八秒遍历一个范围的速度播放，计算繁忙时等待最新结果。点轨迹最多保留 2048 个位置。“指令”面板中的构造步骤滑动条可逐步显示对象。

## 已实现的指令

角度使用弧度，也可输入 `45°`。支持 Unicode 运算符、上下标平方和立方，以及 `2x` 一类隐式乘法。命令名不区分大小写，对象名区分大小写。

| 类别 | 指令及示例 |
| --- | --- |
| 点、线与向量 | `Point`、`PointOnObject`、`Midpoint`、`Center`、`Line`、`Segment`、`Ray`、`Polyline`、`Vector`、`UnitVector`、`Dot`、`Cross`、`PerpendicularVector` |
| 尺规构造 | `Parallel(A,l)`、`Perpendicular(A,l)`、`PerpendicularBisector(A,B)`、`AngleBisector(A,B,C)`、`Intersect`、`Tangent(A,c)`、`Polar(A,c)` |
| 圆与多边形 | `Circle(A,3)`、`Circle(A,B)`、`Circumcircle(A,B,C)`、`Compass`、`Semicircle`、`Arc`、`Sector`、`CircumArc`、`CircumSector`、`Polygon`、`RegularPolygon` |
| 刚体与向量多边形 | `RigidPolygon({(0,0),(3,0),(1,2)},A,B)` 将给定顶点整体放置在锚点 A，并由 B 控制方向，保持原边长。`VectorPolygon(A,{u,v,w})` 由起点和边向量构造并自动闭合 |
| 变换与度量 | `Translate`、`Rotate(g,pi/3,A)`、`Rotate(g,pi/3,A,axisVector)`、`Dilate`、`Reflect`、`Distance`、`Length`、`Area`、`Slope`、`Angle`、`Relation` |
| 圆锥曲线 | `Ellipse(F1,F2,a)`、`Hyperbola(F1,F2,a)`、`Parabola(F,directrix)`、`Conic(A,B,C,D,E)`。焦点构造的曲线支持 `Focus`、`Directrix`、`Eccentricity`、`Asymptote`、`Vertex` |
| 函数与方程 | `f(x)=a*x^2`、`y=sin(x)`、`Function(x^2,x,-3,3)`、`If(x<0,-x,x^2)`、`x^3+y^3-3*a*x*y=0` |
| 参数与极坐标 | `Curve(4*cos(t),3*sin(t),t,0,2*pi)`、`PolarCurve(3*cos(5*t),0,2*pi)`、极坐标网格 |
| 不等式 | `x>=0 && y>=0 && x+y<=5`，支持区域填充及边界轮廓 |
| 微积分图形 | `Root(f)`、`Extremum(f)`、`InflectionPoint(f)`、`Tangent`、`Normal(f,2)`、`Curvature(f,2)`、`OsculatingCircle(f,2)`、`Length(f,0,3)`、`IntegralArea(f,0,pi)`、`IntegralBetween(f,g,0,3)` |
| 常微分方程 | `SlopeField(x-y)`、`SolveODE(x-y,0,1,6)`，数值解采用 RK4 |
| 复数 | `ComplexPoint((2+3*i)^2)`、`Complex(A)`，以及 CAS 的复数计算 |
| 三维基础 | 三维点、线、向量。`Plane(A,B,C)`、`Plane(A,normalVector)`、`ParallelPlane`、`PerpendicularPlane` |
| 立体与截线 | `Cube`、`Tetrahedron`、`Octahedron`、`Dodecahedron`、`Icosahedron`、`Sphere`、`Cylinder`、`Cone`、`Prism(polygon,height)`、`Pyramid(polygon,heightOrApex)`、`IntersectPath`、`Volume`、`SurfaceArea` |
| 空间曲线与曲面 | `Curve(x(t),y(t),z(t),t,min,max)`、`z=sin(x)*cos(y)`、`f(x,y)=sin(x*y)`、`Surface`、`x^2+y^2-z^2=4`、`Revolve(curve,axis,angle)` |
| 动态构造 | `Sequence((k,sin(k)),k,-8,8,0.5)`、`Zip(k^2,k,{1,2,3})`、`Locus(P,a,-3,3)`、`Envelope(l,a,1,3)`、`Net(solid,a)` |
| 交互控件 | `Checkbox(true)`、`Button("下一步")`、`InputBox(a)`、`Dropdown({1,2,3},2)`。控件位于图形视图左上方，按钮事件在对象属性中编辑 |

## CAS、证明与脚本

CAS 页面支持 `Simplify`、`Expand`、`Factor`、`Numeric`、`Solve`、`Substitute`、`Derivative`、`Integral`、`Limit`、`Determinant`、`Inverse`、`Transpose`、`Rank`、`Real`、`Imaginary`。例如 `Solve({x+y=5,x-y=1},{x,y})`、`Integral(x^2,x,0,3)`、`Inverse([[1,2],[3,4]])`。工作区中的函数和精确数值可被 CAS 引用。结果显示精确形式与可用的数值形式，可回送输入栏或复制 LaTeX。

`Prove` 与 `ProveDetails` 使用符号恒等式检查支持的平面构造。例如 `ProveDetails(AreCollinear(A,Midpoint(A,B),B))`。支持平行、垂直、三点共线、四点共圆和点重合命题。自由点以符号坐标处理，固定点保留常量。支持点、中点、直线、平行线、垂线、垂直平分线与直线交点构造，详情包含使用到的非退化条件。未能将余式化为零时显示“未证明”。`Relation` 提供独立的数值关系检查。

用 `DefineTool(DiameterCircle,{U,V},Circle(Midpoint(U,V),Distance(U,V)/2))` 保存自定义工具，随后输入 `c=DiameterCircle(A,B)`。工具定义随构造文件和画布元数据保存。

点击与更新事件可使用构造指令脚本，例如 `SetValue(a,a+1); SetVisible(g,true)`。支持 `SetValue`、`SetCoords`、`SetVisible`、`SetFixed`、`ShowLabel`、`SetColor` 和普通构造指令。脚本整体成功后提交，失败时保持原状态。

事件以 `js:` 开头时使用本地 JavaScript 引擎。例如 `js: ggbApplet.setValue('a',ggbApplet.getValue('a')+1);`。提供 `evalCommand`、`getValue`、`getXcoord`、`getYcoord`、`getZcoord`、`setValue`、`setCoords`、`setVisible`。JavaScript 限制为 10000 条语句、32 层递归、8 MiB 分配量和 500 ms。事件在后台计算，结果仅在构造版本仍匹配时提交，脚本引发的更新不会再次触发相同一轮更新事件。

## 保存、插入与导出

`.icgeo` 保存对象定义、依赖、自定义工具、事件和视图。保存先写临时文件，再原子替换目标文件。

“实时预览到画布”可在打开时绑定的画布页面显示临时轮廓。插入使用现有主工具栏与形状选择路径，整个构造作为一组矢量轮廓提交，支持移动、缩放、旋转、克隆、层级、擦除和单步撤销。插入内容包含压缩构造定义。选中后重开数学工作区可以继续编辑。旧版单曲线对象可迁移，并按原对象框保留位置与尺寸。

构造定义随 `.icink`、白板存档与 Ink Canvas PDF 批注保存。页面切换会清除临时预览并禁用对旧页面的提交，可从菜单重新绑定当前画布。模块停用后已保存的轮廓仍可显示和选择。

可导出 SVG、PNG、矢量 PDF、PGF/TikZ 和 STL。SVG、PNG 和 PDF 包含填充面，画布插入目前保存线条、点及区域边界。STL 输出三角网格，开放曲面需在打印前补成封闭实体。

## 当前覆盖范围与后续差距

此版本已提供原生联动工作区和上述指令。与 GeoGebra 全功能的差距仍包括完整命令兼容、任意构造的通用自动证明、全部特殊函数、全部圆锥曲线特征提取与公切线、附着和脱离点的完整交互、自由布置控件、红蓝立体投影、AR、动态 GIF 和 PSTricks 导出。JavaScript 接口支持上表列出的工作区操作，GeoGebra 全部事件注册 API 尚未覆盖。

符号引擎无法显式求出的表达式可能保留符号运算或返回未求解集合。轨迹、包络线、不等式填充、微分方程、隐式曲面和曲面交线使用有界数值采样。高频或极小结构可能超出当前采样分辨率，曲面交线使用三角网格近似。两圆交点要求共面。展开图使用面邻接生成展开树，复杂多面体展开后可能存在面重叠。

## 性能与验证

输入与拖动更新合并约 28 ms 后在后台计算，依赖签名缓存使未受影响对象复用结果。单个工作区最多 512 个对象、64 个自定义工具，每个定义最多 4096 字符。计算有取消标记和八秒预算，过期计算结果被丢弃。数值采样与输出顶点均有上限，三维隐式曲面复用网格顶点采样。原生视图缓存 Win2D 路径，重绘时复用未变化的场景。

```powershell
$env:NUGET_PACKAGES='D:\dev-cache\nuget'
dotnet test tests/InkCanvas.Core.Tests/InkCanvas.Core.Tests.csproj -c Release -p:Platform=x64
.\tools\pack-extension.ps1 -Project src/InkCanvas.Modules.AnalyticGeometry/InkCanvas.Modules.AnalyticGeometry.csproj -Kind Module
.\InkCanvas.exe --geometrycheck=D:\Checks\Geometry
```

`IncludeAnalyticGeometryModule=false` 可构建不携带模块的主体。自动验证覆盖依赖重算、循环、精确计算、几何关系、凹多边形、三维截面、展开图、自定义工具、脚本事务与执行预算、文件恢复、画布元数据、原生键盘、CAS、旧版迁移、导出和窗口生命周期。红外屏接触噪声与端到端延迟还需在目标触摸设备上验收。

界面与交互参考 GeoGebra 官方代数视图与三维视图说明。依赖版本以项目文件和模块清单为准。[代数视图](https://geogebra.github.io/docs/manual/en/Algebra_View/) [三维视图](https://geogebra.github.io/docs/manual/en/3D_Graphics_View/)

