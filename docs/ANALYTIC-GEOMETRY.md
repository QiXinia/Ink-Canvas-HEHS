# 数学工作区与解析几何模块

`inkcanvas.analytic-geometry` 2.2.0 是独立安装的 WinUI 3 模块。界面使用项目的 `WindowFrame`、`SegmentedSelector`、`PointerSlider` 和墨迹输入控件，图形通过 Win2D 绘制。本地符号计算使用 AngouriMath 2.8，JavaScript 事件使用 Jint 4.18。依赖库和许可证随模块分发。

桌面与白板工具栏的“更多 → 解析几何”打开数学工作区。窗口采用左侧代数对象、对象属性、CAS 页面与右侧二维或三维视图的布局。底部提供指令输入、数学键盘和 Windows 本地手写候选。“指令”按钮提供可直接编辑的分类示例。

## 对象联动与操作

输入 `A=(1,2); B=(4,3); s=Segment(A,B); M=Midpoint(A,B)` 创建依赖构造。修改 A 的定义或拖动 A，会更新线段与中点。相同名称重新赋值会更新已有对象。自由点仅包含常量坐标，`P=(a,a^2)` 这样的依赖点通过其参数 a 驱动。

二维视图支持自由点拖动、空白处平移、滚轮与双指缩放。三维视图支持轨道旋转、正交、透视、倾斜投影、红青和红蓝立体显示。三维自由点可在 XY 平面内移动，在“视图”中开启“3D 拖点沿 Z 轴移动”后沿 Z 轴拖动。选择“工具 → 附着 / 脱离”，点击自由点后再点击目标轮廓或支持的区域内部。点击已附着的点可在当前位置解除约束。属性面板也可按名称指定路径，或附着到圆、椭圆、多边形和平面内部。二维坐标轴保持等比例。

在工作区左侧打开「工具」，可按分类选择作图工具。使用「中点 / 中心」时，可点两个点、线段、圆、椭圆或双曲线。垂直平分线支持点选线段或两个端点，角平分线支持点选两条线或三个点。交点支持依次点选两个对象，也可直接点选交会处。在路径或支持的区域内可直接放置约束点。

工具还包括平行线、垂线、切线、极线与径线、拟合直线、轨迹、函数零点与极值、正多边形、圆规、圆锥曲线、度量及变换。多边形可回点首个顶点或点击「完成」，折线与拟合直线用「完成」提交。定长、定半径、定角、旋转和位似通过数值对话框确认。一次作图及其新建的辅助点可一起撤销。

对象属性支持定义、可见性、固定、颜色、线宽、填充透明度、标签、轨迹跟踪和事件脚本。一次拖动使用一个撤销事务。

数值对象可配置滑动条上下限和步长，勾选“播放时驱动此参数”后使用顶部播放按钮。参数以八秒遍历一个范围的速度播放，计算繁忙时等待最新结果。点轨迹最多保留 2048 个位置。“指令”面板中的构造步骤滑动条可逐步显示对象。

## 已实现的指令

角度使用弧度，也可输入 `45°`。支持 Unicode 运算符、上下标平方和立方，以及 `2x` 一类隐式乘法。命令名不区分大小写，对象名区分大小写。

| 类别 | 指令及示例 |
| --- | --- |
| 点、线与向量 | `Point`、`PointOnObject`、`Midpoint`、`Center`、`Line`、`Segment`、`Ray`、`Polyline`、`Vector`、`UnitVector`、`Dot`、`Cross`、`PerpendicularVector` |
| 尺规构造 | `Parallel(A,l)`、`Perpendicular(A,l)`、`PerpendicularBisector(A,B)`、`AngleBisector(A,B,C)`、`Intersect`、`Tangent(A,c)`、`Polar(A,c)` |
| 圆与多边形 | `Circle(A,3)`、`Circle(A,B)`、`Circumcircle(A,B,C)`、`Radius(c)`、`Compass`、`Semicircle`、`Arc`、`Sector`、`CircumArc`、`CircumSector`、`Polygon`、`RegularPolygon`、`Centroid(p)` |
| 刚体与向量多边形 | `RigidPolygon({(0,0),(3,0),(1,2)},A,B)` 将给定顶点整体放置在锚点 A，并由 B 控制方向，保持原边长。`VectorPolygon(A,{u,v,w})` 由起点和边向量构造并自动闭合 |
| 变换与度量 | `Translate`、`Rotate(g,pi/3,A)`、`Rotate(g,pi/3,A,axisVector)`、`Dilate`、`Reflect`、`Distance`、`Length`、`Area`、`Circumference`、`Perimeter`、`Slope`、`Angle`、`Relation` |
| 圆锥曲线 | `Ellipse(F1,F2,a)`、`Hyperbola(F1,F2,a)`、`Parabola(F,directrix)`、`Conic(A,B,C,D,E)`、`CommonTangents(c,d)`。焦点构造的曲线支持 `Focus`、`Directrix`、`Eccentricity`、`Asymptote`、`Vertex` |
| 函数与方程 | `f(x)=a*x^2`、`y=sin(x)`、`Function(x^2,x,-3,3)`、`If(x<0,-x,x^2)`、`x^3+y^3-3*a*x*y=0` |
| 参数与极坐标 | `Curve(4*cos(t),3*sin(t),t,0,2*pi)`、`Derivative(c,2)`、`CurveDerivative(c,n)`、`PolarCurve(3*cos(5*t),0,2*pi)`、极坐标网格 |
| 不等式 | `x>=0 && y>=0 && x+y<=5`，支持区域填充及边界轮廓 |
| 微积分图形 | `Root(f)`、`Root(f,1)`、`Root(f,0,2)`、`Derivative(f,2)`、`Extremum(f)`、`InflectionPoint(f)`、`Tangent`、`Normal(f,2)`、`Curvature(f,2)`、`OsculatingCircle(f,2)`、`Length(f,0,3)`、`Integral(f,0,pi)`、`IntegralBetween(f,g,0,3)` |
| 常微分方程 | `SlopeField(x-y)`、`SolveODE(x-y,0,1,6)`，数值解采用 RK4 |
| 复数 | `ComplexPoint((2+3*i)^2)`、`Complex(A)`，以及 CAS 的复数计算 |
| 三维基础 | 三维点、线、向量。`Plane(A,B,C)`、`Plane(A,normalVector)`、`ParallelPlane`、`PerpendicularPlane` |
| 立体与截线 | `Cube`、`Tetrahedron`、`Octahedron`、`Dodecahedron`、`Icosahedron`、`Sphere`、`Cylinder`、`Cone`、`Prism(polygon,height)`、`Pyramid(polygon,heightOrApex)`、`IntersectPath`、`Volume`、`SurfaceArea` |
| 空间曲线与曲面 | `Curve(x(t),y(t),z(t),t,min,max)`、`z=sin(x)*cos(y)`、`f(x,y)=sin(x*y)`、`Surface`、`x^2+y^2-z^2=4`、`Revolve(curve,axis,angle)` |
| 动态构造 | `Sequence((k,sin(k)),k,-8,8,0.5)`、`Zip(k^2,k,{1,2,3})`、`Locus(P,a,-3,3)`、`Envelope(l,a,1,3)`、`Net(solid,a)` |
| 列表与统计 | `Element(L,2,3)`、`Length(L)`、`Sum(L,3)`、`Sum(L,weights)`、`Mean(L,weights)`、`CellRange(A1,B5)` |
| 交互控件 | `Checkbox(true)`、`Button("下一步")`、`InputBox(a)`、`Dropdown({1,2,3},2)`。拖动控件边上的把手可重新布置位置，位置随构造保存 |

## CAS、证明与脚本

CAS 页面支持 `Simplify`、`Expand`、`Factor`、`Numeric`、`Solve`、`Substitute`、`Derivative`、`Integral`、`Limit`、`Determinant`、`Inverse`、`Transpose`、`Rank`、`Real`、`Imaginary`。例如 `Solve({x+y=5,x-y=1},{x,y})`、`Integral(x^2,x,0,3)`、`Inverse([[1,2],[3,4]])`。工作区中的函数和精确数值可被 CAS 引用。结果显示精确形式与可用的数值形式，可回送输入栏或复制 LaTeX。

`Prove` 与 `ProveDetails` 使用符号恒等式检查支持的平面构造。例如 `ProveDetails(AreCollinear(A,Midpoint(A,B),B))`。支持平行、垂直、共线、共点、共圆、点重合、线段全等和代数等式。支持空间点共面、共线、线段度量和由两点确定的空间直线关系。圆与直线、圆与圆的平面交点可按构造方程消去辅助变量。自由点以符号坐标处理，固定点保留常量。详情包含非退化条件。符号恒等式成立时返回真，找到满足约束的精确反例时返回假，其余情形显示“未证明”。`Relation` 检查当前坐标下的几何关系。

用 `DefineTool(DiameterCircle,{U,V},Circle(Midpoint(U,V),Distance(U,V)/2))` 保存自定义工具，随后输入 `c=DiameterCircle(A,B)`。工具定义随构造文件和画布元数据保存。

对象属性支持点击、更新、值改变、指针进入与离开、选中与取消选中、拖动开始与结束。可使用构造指令脚本，例如 `SetValue(a,a+1); SetVisible(g,true)`。脚本支持 `SetValue`、`SetCoords`、`SetVisible`、`SetFixed`、`ShowLabel`、`SetColor`、`SetControlPosition`、`SetEvent` 和普通构造指令。脚本整体成功后提交，失败时保持原状态。

事件以 `js:` 开头时使用本地 JavaScript 引擎。例如 `js: ggbApplet.setValue('a',ggbApplet.getValue('a')+1);`。提供 `evalCommand`、`getValue`、`getXcoord`、`getYcoord`、`getZcoord`、`setValue`、`setCoords`、`setVisible`。JavaScript 限制为 10000 条语句、32 层递归、8 MiB 分配量和 500 ms。事件按队列在后台计算，提交前检查构造版本，更新脚本引发的改动会跳过同轮更新回调。

输入 `SetEvent(*,OnAdd,"js: ggbApplet.setValue('a',ggbApplet.getValue('a')+1)")` 注册构造级事件，传入空脚本可解除注册。构造级也支持 `OnRemove`、`OnRename`、`OnClear`、`OnStoreUndo`、`OnViewChanged` 和对象属性中的事件类型。使用 `OnClient` 可接收这些事件，脚本可读取 `eventType`、`eventTarget`、`previousEventTarget` 和 `eventArgument`。撤销、重做、样式修改与导出分别触发 `OnUndo`、`OnRedo`、`OnUpdateStyle` 和 `OnExport`。

打开 `.ggb` 时运行 `ggbOnInit`。支持注册和解除构造级 `Add`、`Remove`、`Update`、`Click`、`Rename`、`Clear`、`StoreUndo`、`Client` 监听器，以及对象级 `ObjectUpdate`、`ObjectClick` 监听器。作图工具切换发送带 GeoGebra 工具编号的 `setMode` 事件，代数与工具面板也发送选中和开合事件。脚本可用 `setCoords` 布置原生控件。回调可使用全局函数名或独立的函数表达式。每次事件使用单独的 JavaScript 上下文，跨事件值可保存在数学构造的对象中。监听器随 `.icgeo` 保存。

## 保存、插入与导出

`.icgeo` 保存对象定义、依赖、自定义工具、事件和视图。也可打开 GeoGebra `.ggb`，导入常用构造、视图、样式、滑动条及对象事件。带名称的函数和组合函数随参数更新，复选框等对象可以通过可见性条件控制图形显示及导出。正多边形的边和生成顶点、棱柱的生成顶点，以及五种正多面体的顶点、面和棱会随原构造更新。内嵌自定义工具可以导入由本地命令构成的输入和输出。动态文本可跟随定位点和引用对象，单元格范围会随对应的代数对象更新。对象脚本通过本地脚本引擎执行。打开文件后会提示静态回退和需要手动迁移的内容。

“实时预览到画布”可在打开时绑定的画布页面显示临时轮廓。插入使用现有主工具栏与形状选择路径，整个构造作为一组矢量轮廓提交，支持移动、缩放、旋转、克隆、层级、擦除和单步撤销。插入内容包含压缩构造定义。选中后重开数学工作区可以继续编辑。旧版单曲线对象可迁移，并按原对象框保留位置与尺寸。

构造定义随 `.icink`、白板存档与 Ink Canvas PDF 批注保存。页面切换会清除临时预览并禁用对旧页面的提交，可从菜单重新绑定当前画布。模块停用后已保存的轮廓仍可显示和选择。

可导出 SVG、PNG、矢量 PDF、PGF/TikZ、PSTricks 和 STL。选择“动态 GIF”可录制已启用动画的数值参数遍历一个周期。SVG、PNG 和 PDF 包含填充面，画布插入目前保存线条、点及区域边界。STL 输出三角网格，开放曲面需在打印前补成封闭实体。

## 当前覆盖范围与后续差距

GeoGebra 兼容由 Ink Canvas 本地引擎实现。当前覆盖上表列出的构造及已测试重载。内嵌工具可调用同一文件中可导入的其他工具，包含多输出工具。电子表格专用界面、部分三维命令输出、浏览器 API 和部分 GeoGebra 客户端事件仍有缺口。部分特殊函数的复数分支、奇点和解析延拓仍待验证。严格证明覆盖具有符号模型的平面和基础空间构造。复杂约束、分支选择和更一般的空间构造仍待支持。公切线求解覆盖同平面的非退化圆锥曲线，数值结果受输入精度约束。Windows 端的 AR 功能已取消，鸿蒙端暂缓评估。

符号引擎无法显式求出的表达式可能保留符号运算或返回未求解集合。轨迹、包络线、不等式填充、微分方程、隐式曲面和曲面交线使用有界数值采样。高频或极小结构可能超出当前采样分辨率，曲面交线使用三角网格近似。两圆交点要求共面。展开图使用面邻接生成展开树，复杂多面体展开后可能存在面重叠。

## 性能与验证

拖点的约束投影和文档更新按渲染帧合并。后台计算复用依赖图和未变化对象的完整采样，仅对受影响对象降低采样密度，松手后恢复完整采样。工作区支持最多 10000 个对象、64 个自定义工具，每个定义最多 4096 字符。单次计算预算为八秒。原生视图缓存 Win2D 路径，重绘时复用未变化的对象，指针命中优先使用已投影的点、路径与包围盒。

```powershell
$env:NUGET_PACKAGES='D:\dev-cache\nuget'
dotnet test tests/InkCanvas.Core.Tests/InkCanvas.Core.Tests.csproj -c Release -p:Platform=x64
.\tools\pack-extension.ps1 -Project src/InkCanvas.Modules.AnalyticGeometry/InkCanvas.Modules.AnalyticGeometry.csproj -Kind Module
.\InkCanvas.exe --geometrycheck=D:\Checks\Geometry
```

`IncludeAnalyticGeometryModule=false` 可构建不携带模块的主体。自动验证覆盖依赖重算、循环、精确计算、几何关系、凹多边形、三维截面、展开图、自定义工具、脚本事务与执行预算、文件恢复、画布元数据、原生键盘、CAS、旧版迁移、导出和窗口生命周期。红外屏接触噪声与端到端延迟还需在目标触摸设备上验收。

界面、构造与事件行为参考 GeoGebra 官方文档。依赖版本以项目文件和模块清单为准。[代数视图](https://geogebra.github.io/docs/manual/en/Algebra_View/) [三维视图](https://geogebra.github.io/docs/manual/en/3D_Graphics_View/) [命令索引](https://geogebra.github.io/docs/manual/en/Commands/) [预定义函数](https://geogebra.github.io/docs/manual/en/Predefined_Functions_and_Operators/) [事件监听器](https://geogebra.github.io/docs/reference/en/GeoGebra_Apps_API/#_event_listeners)

