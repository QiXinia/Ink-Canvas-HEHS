import React from "react";

const assets = "./assets/";

function DataTable({ headings, rows, className = "" }) {
  return (
    <table className={`report-table ${className}`}>
      <thead>
        <tr>
          {headings.map((text) => (
            <th key={text}>{text}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, index) => (
          <tr key={index}>
            {row.map((cell, i) => (
              <td key={i}>{cell}</td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function Evidence({ children }) {
  return <div className="evidence-source">{children}</div>;
}

function Demo({ name, title, poster, children, duration }) {
  return (
    <figure className="demo-card">
      <div className="demo-screen">
        <img src={assets + poster} alt={title + "演示画面"} />
        <video
          controls
          preload="none"
          poster={assets + poster}
          aria-label={title + "本地演示"}
        >
          <source src={`${assets}media/${name}.mp4`} type="video/mp4" />
        </video>
      </div>
      <figcaption>
        <b>{title}</b>
        <span>{duration}</span>
        <p>{children}</p>
      </figcaption>
    </figure>
  );
}

export function createExpandedPages({
  Page,
  Heading,
  Figure,
  Feature,
  Note,
  Label,
  Icon,
}) {
  function Origins() {
    return (
      <Page label="项目来源与重构贡献" className="expanded-page origins-page">
        <Heading
          eyebrow="PROJECT / 项目来源与演进"
          title={
            <>
              在开源基础上
              <br />
              重构现代课堂体验
            </>
          }
        >
          本项目以 Ink Canvas 与 Ink Canvas Artistry
          的开源工作为基础，开展架构迁移、代码重构与功能扩展，重建桌面界面、墨迹管线及输入交互，并持续改进
          UI、UX 与课堂使用流程。
        </Heading>
        <div className="origin-statement">
          <Label>项目贡献的具体落点</Label>
          <p>
            从 WPF 桌面实现演进到 .NET 10、WinUI 3 与
            Win2D，并以明确的模块契约支撑 PDF、解析几何、AI
            和文稿管理。鸿蒙版本采用 ArkTS／ArkUI 单独适配平台能力。
          </p>
        </div>
        <DataTable
          headings={["重构维度", "本项目实施内容", "带来的使用变化"]}
          rows={[
            [
              "技术与结构",
              "划分 Core、Platform、Ink、Controls、Features、App，建立宿主契约与扩展运行时",
              "核心书写与可选模块分包维护",
            ],
            [
              "数字墨水",
              "自建可变笔宽模型，分层与脏区渲染，接入真实压力、笔速笔锋和倾斜数据",
              "支持多种笔刷、增量绘制与可编辑存储",
            ],
            [
              "UI 与 UX",
              "重做 WinUI 3 标题栏、设置导航、PDF 壳层、助手侧栏、文稿库与课堂窗口",
              "统一深浅主题、图标、触摸热区与响应式布局",
            ],
            [
              "数据与协同",
              "异步 ZIP 存档、页面级纸型、PDF 矢量写回、模块工具注册",
              "保存后可以继续编辑和跨功能复用",
            ],
            [
              "平台适配",
              "Windows 多屏与焦点行为、混合输入，以及鸿蒙原生控件与应用内画布",
              "围绕不同系统的能力调整交互入口",
            ],
          ]}
        />
        <div className="attribution-box">
          <h3>保留上游贡献与许可</h3>
          <p>
            沿用 GPL-3.0，保留 Ink Canvas 与 Ink Canvas Artistry
            的来源和署名。报告按实际代码、界面截图和验证记录说明本项目改造，具体依赖及素材许可随项目提供。
          </p>
          <div className="source-links">
            <a href="https://github.com/WXRIW/Ink-Canvas">Ink Canvas 上游 ↗</a>
            <a href="https://github.com/InkCanvas/Ink-Canvas-Artistry">
              Ink Canvas Artistry 上游 ↗
            </a>
          </div>
        </div>
        <Evidence>
          重构依据 · docs/EXTENSIONS.md · docs/PORTING-NOTES.md ·
          docs/PERFORMANCE.md · CHANGELOG.md
        </Evidence>
      </Page>
    );
  }

  function Engineering() {
    const layers = [
      ["App", "组合根与窗口入口", "组装服务、打开工作区、协调模式和生命周期"],
      ["Features", "课堂业务", "白板页面、对象、保存恢复与课件服务"],
      ["Controls", "共用交互组件", "工具栏、指针滑动条、窗口框架与主题资源"],
      [
        "Ink + Platform",
        "书写与系统能力",
        "输入、几何缓存、覆盖层、DPI、焦点和显示器",
      ],
      ["Core", "领域模型", "笔画、几何、撤销、设置与纯逻辑算法"],
    ];
    return (
      <Page label="架构重构 / 依赖与数据" className="expanded-page">
        <Heading
          eyebrow="ENGINEERING / 架构重构"
          title={
            <>
              把职责分清
              <br />
              让功能可以独立演进
            </>
          }
        >
          领域模型、平台实现和界面组件各有边界。可选功能经宿主接口访问画布，文件与工具能力可以扩展，降低界面与业务相互牵连的成本。
        </Heading>
        <div className="dependency-diagram">
          <div className="layer-stack">
            {layers.map(([name, title, detail]) => (
              <div className="layer-item" key={name}>
                <b>{name}</b>
                <div>
                  <h3>{title}</h3>
                  <p>{detail}</p>
                </div>
              </div>
            ))}
          </div>
          <div className="contract-stack">
            <span className="diagram-tag">可选能力</span>
            <b>
              PDF · 数学 · AI
              <br />
              文稿库 · 云同步
            </b>
            <span>↓</span>
            <strong>
              Host.Contracts
              <br />
              Extensibility
            </strong>
            <p>
              明确的 DTO、生命周期
              <br />
              命令和工具注册
            </p>
            <span>↓</span>
            <b>Extensions</b>
            <p>
              发现 · 依赖 · 状态
              <br />
              进程管理与兼容检查
            </p>
          </div>
        </div>
        <div className="section-intro">
          <h2>从单体媒体 JSON 到流式文稿容器</h2>
          <p>
            当前白板保存通过异步流式读写组织 ZIP
            条目，图片和视频作为媒体条目保存，减少大文件文本膨胀及同步 I/O
            对交互的影响。
          </p>
        </div>
        <div className="storage-flow">
          <div>
            <b>页面与对象</b>
            <small>笔迹 · 纸型 · 图片 · 视频</small>
          </div>
          <span>→</span>
          <div>
            <b>.icink ZIP 容器</b>
            <small>清单 · 分页数据 · 媒体 · 预览</small>
          </div>
          <span>→</span>
          <div>
            <b>按需读取</b>
            <small>打开 · 缩略图 · 再编辑</small>
          </div>
        </div>
        <Evidence>
          代码入口 · InkCanvas.Host.Contracts/DocumentWorkspace.cs ·
          InkCanvas.Extensions/ModuleRuntime.cs · BoardService.Persistence.cs
        </Evidence>
      </Page>
    );
  }

  function Performance() {
    return (
      <Page
        label="墨迹管线 / 性能依据"
        className="expanded-page performance-page"
      >
        <Heading
          eyebrow="RENDERING / 墨迹管线"
          title={
            <>
              持续书写时
              <br />
              把计算留给变化的部分
            </>
          }
        >
          实时笔尖、稳定笔画和历史画面分层处理。补采输入保留时间序列，历史笔迹复用缓存，平移和捏合期间优先保持连续反馈。
        </Heading>
        <div className="render-pipeline">
          {[
            ["输入", "坐标、时间、压力与倾斜", "补采合并输入，按笔画维护状态"],
            ["笔触", "因果笔锋与可变宽度", "稳定前缀保持，仅更新活动末端"],
            ["分层", "历史缓存 + 活动墨迹", "空间分块索引与脏区包围盒"],
            ["呈现", "Win2D 与覆盖层", "增量合成、局部读回、抬笔提交"],
          ].map(([name, title, detail]) => (
            <div key={name}>
              <span>{name}</span>
              <b>{title}</b>
              <p>{detail}</p>
            </div>
          ))}
        </div>
        <div className="section-intro">
          <h2>千笔画面的本机离屏测量</h2>
          <p>
            Release x64，1280 × 720，1,200 条历史笔画，每条 100
            点。书写、平移、捏合各测量 120 帧，包含强制 GPU 读回。
          </p>
        </div>
        <DataTable
          headings={[
            "笔刷",
            "书写 中位 / P95",
            "平移 中位 / P95",
            "捏合 中位 / P95",
          ]}
          rows={[
            ["自然笔锋", "0.92 / 1.48 ms", "0.95 / 1.45 ms", "1.23 / 3.39 ms"],
            ["书法笔锋", "0.82 / 1.25 ms", "0.77 / 1.12 ms", "0.87 / 1.27 ms"],
            ["水墨笔锋", "1.31 / 2.14 ms", "0.82 / 1.10 ms", "0.82 / 1.14 ms"],
          ]}
        />
        <div className="metric-pair">
          <div>
            <strong>49 MiB</strong>
            <b>历史分块栅格预算</b>
            <p>低倍率按显示需求采样，缓存有界。</p>
          </div>
          <div>
            <strong>95.26–98.84%</strong>
            <b>活动墨迹读回字节减少</b>
            <p>八组普通笔与荧光笔合成检查的结果。</p>
          </div>
        </div>
        <div className="feature-grid tight">
          <Feature icon="pen" title="滑动调节保持轻量">
            粗细与透明度按帧合并更新，预览后台计算轮廓，松手合并保存配置。
          </Feature>
          <Feature icon="pages" title="窗口和媒体按需加载">
            隐藏窗口回收图形缓存，PDF 优先渲染可见页，媒体预览复用封面。
          </Feature>
        </div>
        <Note>
          数据来自 2026-11-20
          的单机合成基准，描述渲染阶段耗时。实体红外屏、压感笔和高刷设备的端到端延迟、帧率与功耗仍需在目标硬件测量。
        </Note>
        <Evidence>
          验证记录 · docs/PERFORMANCE.md · ink-performance-final/writing.json ·
          InkSceneRenderer.cs · OverlayInkPresenter.cs
        </Evidence>
      </Page>
    );
  }

  function Touch() {
    return (
      <Page label="WinUI 3 / 触摸与体验" className="expanded-page touch-page">
        <Heading
          eyebrow="NATIVE UX / 原生桌面与触摸"
          title={
            <>
              适应讲台、教室大屏
              <br />
              和二合一设备
            </>
          }
        >
          采用 WinUI 3
          原生控件与共用设计资源，重做窗口层级、工具反馈及触摸布局，让同一套书写功能适应鼠标、笔与手指。
        </Heading>
        <div className="theme-pair">
          <Figure
            src="library.png"
            alt="文稿库浅色主题截图"
            caption="浅色 · Mica 窗口与分区导航"
          />
          <Figure
            src="library-dark.png"
            alt="文稿库深色主题截图"
            caption="深色 · 语义画刷与清晰对比"
          />
        </div>
        <div className="ux-grid">
          {[
            [
              "pen",
              "区分笔、手指与手掌",
              "真实压力驱动笔宽，普通触摸使用模拟笔锋；按设置启用笔优先和大面积接触板擦。",
            ],
            [
              "board",
              "手势围绕对象工作",
              "单指书写、多指平移缩放；选中对象后捏合与自由旋转，移动中持续呈现位置。",
            ],
            [
              "setting",
              "工具选项按需要出现",
              "先选工具，再点开选项。面板可拖离并固定，未固定面板随模式切换收起。",
            ],
            [
              "pages",
              "窗口适应不同尺寸",
              "课堂工具、PDF 和文稿库使用响应式布局，导航可折叠，保持核心操作可达。",
            ],
            [
              "play",
              "融入桌面焦点规则",
              "批注覆盖层支持无焦点激活，减少对 PPT 视频和输入法候选的干扰。",
            ],
            [
              "shapes",
              "多屏与系统偏好",
              "物理像素与 DIP 分别处理，工具窗口限制在工作区，动效遵循系统动画设置。",
            ],
          ].map(([icon, title, text]) => (
            <Feature key={title} icon={icon} title={title}>
              {text}
            </Feature>
          ))}
        </div>
        <div className="bottom-band">
          <strong>交互改造</strong>
          <p>
            Regular 字体图标、系统圆角与标题栏、Mica／Acrylic
            材质、深浅主题和透明效果回退统一纳入窗口框架。
          </p>
        </div>
        <div className="brand-theme-examples">
          <div>
            <img src={assets + "logo.png"} alt="浅色模式的新白板与笔画标志" />
            <span>浅色 · 深色轮廓与蓝色笔画</span>
          </div>
          <div>
            <img
              src={assets + "logo-dark.png"}
              alt="深色模式的新白板与笔画标志"
            />
            <span>深色 · 浅色轮廓与亮蓝笔画</span>
          </div>
        </div>
        <Note>
          真实混合 DPI
          多屏触摸、热插拔与不同驱动的手掌分类，仍需在教室设备上进行组合验收。
        </Note>
      </Page>
    );
  }

  function ObjectWorkflow() {
    return (
      <Page label="对象编辑 / 媒体与回放" className="expanded-page">
        <Heading
          eyebrow="OBJECTS / 对象编辑"
          title={
            <>
              从一笔画出的图形
              <br />
              到可调整的课堂内容
            </>
          }
        >
          选择、变换、克隆与图层操作共用对象模型。形状保留构造信息，图片与墨迹可以一起整理，讲解结束后继续修改。
        </Heading>
        <div className="sequence-pair">
          <Figure
            src="cases/shape-catalog-final.png"
            alt="线条、坐标、平面、曲线与立体共47种图形的原生全览"
            caption="图形全览 · 47 种轮廓按真实范围独立排布"
          />
          <Figure
            src="cases/geometry3d-final.png"
            alt="立方体与切割平面的关联对象"
            caption="空间构造 · 对象依赖、截线与视角调整"
          />
        </div>
        <DataTable
          headings={["课堂动作", "操作方式与结果"]}
          rows={[
            [
              "圈定内容",
              "矩形或套索选择，支持全选与反选；混合选择墨迹、形状及图片。",
            ],
            [
              "调整对象",
              "平移、缩放、旋转，改变颜色和笔宽；支持的形状可改顶点、填充与线型，并关联连接线。",
            ],
            [
              "克隆与排列",
              "克隆模式或 Ctrl 拖动复制，选择工具条可以折叠；多选时整体上移、下移或置于顶／底层。",
            ],
            [
              "插入媒体",
              "批注与白板可插入图片，支持多种位图与矢量图；白板还可插入视频并保留媒体属性。",
            ],
            [
              "回看推导",
              "墨迹在当前画布中回放，使用独立浮动控制条播放、暂停或跳转，按时间呈现笔画。",
            ],
            [
              "自动规整",
              "停笔识别直线、圆、椭圆、多边形、立方体与坐标轴，等待时长和方向吸附可设置。",
            ],
          ]}
        />
        <div className="teaching-example">
          <Label>课堂演示建议</Label>
          <h3>把椭圆题中的图形与推导分层组织</h3>
          <p>
            圈选坐标与椭圆，单独整理直线、交点和面积辅助线。讲解时调整位置与线型，保留代数推导区域，再用撤销与回放回看步骤。
          </p>
        </div>
        <Evidence>
          演示素材 · assets/media/shape-catalog.mp4 · geometry3d.mp4 ·
          原生图形插入与空间构造
        </Evidence>
      </Page>
    );
  }

  function Desktop() {
    return (
      <Page label="桌面协同 / PPT 与内容跟随" className="expanded-page">
        <Heading
          eyebrow="DESKTOP / 桌面协同"
          title={
            <>
              课件在前
              <br />
              批注跟上讲授节奏
            </>
          }
        >
          Windows 版本接入桌面覆盖层、Office 状态与显示器信息。在 PPT
          放映、阅读及普通桌面批注之间，保留教师熟悉的浮动工具入口。
        </Heading>
        <Figure
          src="ppt-annotation.png"
          alt="PPT 放映中使用浮动批注工具栏"
          caption="用户提供的 PPT 批注场景 · 原课件、墨迹与工具栏同屏呈现"
          className="desktop-hero"
        />
        <div className="feature-grid">
          <Feature icon="play" title="放映与阅读模式联动">
            工具栏可自动停靠到底部居中，退出放映后恢复位置。左右侧与底角的翻页工具方便在大屏上操作。
          </Feature>
          <Feature icon="pages" title="幻灯片与批注对应">
            按幻灯片切换墨迹，翻页尽量复用已有数据。关闭标注后的墨迹显示由 PPT
            子设置单独控制。
          </Feature>
          <Feature icon="pen" title="维持演示焦点">
            书写工具采用无焦点激活支持，适配底层视频播放；桌面快捷键根据当前模式门控。
          </Feature>
          <Feature icon="setting" title="多显示器定位">
            覆盖虚拟桌面，按目标显示器 DPI
            布置窗口，截图与辅助工具定位跟随工作区。
          </Feature>
        </div>
        <div className="experimental-box">
          <Label>实验性 · 默认关闭</Label>
          <h3>Word、PowerPoint 与 Edge 内容跟随</h3>
          <p>
            根据目标应用提供的正文、滚动和缩放信息更新墨迹视图，识别固定区域遮罩，并在本次运行中记忆页面。写字和选择期间延后跟随，避免操作中跳动。
          </p>
          <p className="small">
            复杂重排、自绘页面或受保护内容可能无法持续定位。实验性页面记忆在退出时结束，需要保留的内容应另行保存。
          </p>
        </div>
        <Evidence>
          实现说明 · docs/PORTING-NOTES.md · docs/SCREEN-TRACKING.md · Office
          联动依赖 PowerPoint／兼容 WPS 桌面版
        </Evidence>
      </Page>
    );
  }

  function MathCapabilities() {
    return (
      <Page
        label="解析几何 / 数学能力"
        className="expanded-page math-capabilities"
      >
        <Heading
          eyebrow="MATHEMATICS / 数形联动"
          title={
            <>
              输入、计算、构造
              <br />
              在一个工作区中衔接
            </>
          }
        >
          原生数学模块将代数列表、二维／三维视图、CAS
          与公式键盘组合起来。改变自由点或参数后，依赖对象随构造关系重新计算。
        </Heading>
        <Figure
          src="math-keyboard.png"
          alt="原生数学键盘及公式输入界面"
          caption="本机实际截图 · 数学符号键盘、公式输入与二维视图"
          className="math-keyboard-shot"
        />
        <DataTable
          headings={["能力", "当前支持范围举例"]}
          rows={[
            [
              "平面构造",
              "点、线、向量、多边形、圆弧、圆锥曲线、几何变换与度量。",
            ],
            [
              "函数与微积分",
              "显式／隐式曲线、参数／极坐标曲线、不等式区域、导数、积分、斜率场与数值 ODE。",
            ],
            [
              "空间图形",
              "棱柱、棱锥、球、圆柱／圆锥与正多面体，参数曲面、隐式曲面及采样交线。",
            ],
            [
              "符号计算",
              "化简、展开、因式分解、方程求解、代入、极限、导数、积分及矩阵运算。",
            ],
            [
              "动态构造",
              "滑动参数、序列、轨迹、包络、展开图、自定义工具和受限脚本。",
            ],
            [
              "结果复用",
              "插入白板，保存构造，导出 SVG、PNG、PDF、PGF/TikZ 和 STL。",
            ],
          ]}
        />
        <Note>
          完整命令兼容和任意构造的自动证明仍有差距。轨迹、隐式曲面与空间交线采用有界数值采样，复杂展开图可能重叠。单工作区最多
          512 个对象，后台计算可取消，过期结果丢弃。
        </Note>
        <Evidence>
          功能与边界 · docs/ANALYTIC-GEOMETRY.md · AngouriMath 符号计算 · Win2D
          原生视图
        </Evidence>
      </Page>
    );
  }

  function Extensions() {
    return (
      <Page label="模块与插件 / 设置与更新" className="expanded-page">
        <Heading
          eyebrow="EXTENSIONS / 模块化扩展"
          title={
            <>
              主体专注书写
              <br />
              扩展能力按需安装
            </>
          }
        >
          模块注册自己的命令、工具与设置页。安装状态决定入口显示，用户可以从程序内下载仓库插件，并复用更新管线管理新版本。
        </Heading>
        <Figure
          src="06-modules-50.jpg"
          alt="WinUI 3 设置中的模块与插件管理"
          caption="真实 WinUI 3 界面 · 模块开关、版本与操作入口归入同一卡片"
          className="extension-hero"
        />
        <div className="feature-grid">
          <Feature icon="setting" title="插件提供自己的设置">
            设置侧栏由已加载插件注册相关条目，核心设置保留分类与搜索，避免未安装能力的入口干扰。
          </Feature>
          <Feature icon="download" title="下载与一站式更新">
            软件更新和插件下载共用源管理，官方源与镜像源分批并发检测，每批三个，选择响应较快的可用源。
          </Feature>
          <Feature icon="ai" title="工具注册与 AI 协同">
            AI 插件通过注册表访问画布和已安装模块。可选 MCP
            接口扩展外部工具，调用受取消与会话状态约束。
          </Feature>
          <Feature icon="pages" title="分包与故障边界">
            原生模块使用明确宿主接口，外部插件运行在独立工作进程，扩展运行时管理依赖、状态与生命周期。
          </Feature>
        </div>
        <div className="module-list">
          <span>PDF</span>
          <span>解析几何</span>
          <span>AI 助教</span>
          <span>文稿库</span>
          <span>云同步</span>
          <span>手写笔／数位板</span>
          <span>屏幕跟随</span>
          <span>MCP</span>
        </div>
        <Evidence>
          边界与开发入口 · docs/EXTENSIONS.md · InkCanvas.Extensibility ·
          InkCanvas.Host.Contracts · InkCanvas.PluginHost
        </Evidence>
      </Page>
    );
  }

  function Harmony() {
    return (
      <Page label="HarmonyOS / 原生适配" className="expanded-page harmony-page">
        <Heading
          eyebrow="HARMONYOS / 鸿蒙原生适配"
          title={
            <>
              沿用书写领域逻辑
              <br />
              重建平台交互与呈现
            </>
          }
        >
          鸿蒙分支使用 ArkTS／ArkUI
          实现界面与应用内画布，围绕输入、窗口、文件与系统服务重新接线。通过 C#
          黄金向量核对可迁移算法与数据契约。
        </Heading>
        <div className="platform-architecture">
          <div className="platform-head">
            <div>
              <small>WINDOWS</small>
              <b>C# · .NET 10</b>
              <span>WinUI 3 / Win2D / Win32</span>
            </div>
            <div>
              <small>HARMONYOS</small>
              <b>ArkTS · ArkUI</b>
              <span>Canvas / 系统 Kit / 应用内交互</span>
            </div>
          </div>
          <div className="contract-band">
            一致性依据 · 笔迹与几何算法 · 设置与文件契约 · 撤销和手势语义
          </div>
          <div className="harmony-layers">
            {[
              ["entry", "应用页面与交互编排"],
              ["controls", "浮动工具条、笔色板与图形面板"],
              ["features", "分页白板、媒体和课堂工具"],
              ["platform", "触控、文件、Canvas 与系统服务"],
              ["ink / core", "笔画、笔锋、选择、擦除与纯逻辑"],
            ].map(([name, text]) => (
              <div key={name}>
                <b>{name}</b>
                <span>{text}</span>
              </div>
            ))}
          </div>
          <p className="diagram-caption">
            架构示意 · 依据 harmony 目录中的代码组织绘制
          </p>
        </div>
        <div className="feature-grid">
          <Feature icon="pen" title="应用内手写与手势">
            实现笔迹采集、因果笔锋、预测、双层
            Canvas、选择、擦除、缩放与回放逻辑。
          </Feature>
          <Feature icon="ai" title="适合鸿蒙的交互入口">
            魔法套索串起选区、截图、流式回复与贴回；未配置端点时使用本地演示流。
          </Feature>
          <Feature icon="time" title="调用原生系统能力">
            为计时画中画、文件选择、媒体和应用市场路径准备平台适配，交互效果待设备验证。
          </Feature>
          <Feature icon="pages" title="按系统能力调整功能">
            跨应用全屏批注改为应用内画布，PPT 路径转向课件／PDF
            场景，保留应用内快捷操作。
          </Feature>
        </div>
        <Evidence>
          代码依据 · harmony/entry · harmony/controls · CanvasInkRenderer.ets ·
          InkCanvasSurface.ets · StylusBinding.ets
        </Evidence>
      </Page>
    );
  }

  function HarmonyValidation() {
    return (
      <Page
        label="HarmonyOS / 运行素材与验证范围"
        className="expanded-page harmony-record-page"
      >
        <Heading
          eyebrow="HARMONYOS / 设备运行材料"
          title="在鸿蒙画布中书写与讲解"
        >
          2026-11-24
          补入用户提供的三段鸿蒙操作录屏和码道开发记录。录屏显示书写、工具栏折叠、PDF
          批注及白板流程，配套记录报告 MatePad／HarmonyOS 7.0.0.111 安装与测试。
        </Heading>
        <div className="demo-grid harmony-demos">
          <Demo
            name="harmony-writing"
            title="书写与折叠"
            poster="harmony/harmony-writing.jpg"
            duration="21 秒"
          >
            选择笔色与笔锋，连续书写，收起和展开常用工具。
          </Demo>
          <Demo
            name="harmony-pdf"
            title="讲义阅读与批注"
            poster="harmony/harmony-pdf.jpg"
            duration="58 秒"
          >
            打开生物传感器讲义，翻页、圈记并添加手写补充。
          </Demo>
          <Demo
            name="harmony-tools"
            title="化学绘制与白板"
            poster="harmony/harmony-tools.jpg"
            duration="84 秒"
          >
            绘制环与键线，切换白板背景，继续进行墨迹书写。
          </Demo>
          <div className="harmony-record-summary">
            <span className="status-chip">新增运行依据</span>
            <h3>由原始素材回溯</h3>
            <p>
              HM-01 至 HM-03
              随网页保存原录屏。预览帧按时间截取，文件名、哈希与码道截图编号记录在独立证明文件夹。
            </p>
            <p>网页可播放，PDF 显示对应静态画面。</p>
          </div>
        </div>
        <DataTable
          headings={["项目", "当前说明"]}
          rows={[
            [
              "书写与工具",
              "录屏可观察书写、擦除、颜色与工具切换。真实压感、倾斜精度、延迟和高刷表现仍需专项测量。",
            ],
            [
              "原生窗口与面板",
              "对话记录提供折叠、跟随、滚动、边界钳制和安装反馈，多窗／分屏与跨应用权限保留平台限制。",
            ],
            [
              "PDF 与课件",
              "已补入讲义打开、翻页与批注录屏，PDF 写回保真、重复保存和跨阅读器兼容仍需专项验证。",
            ],
            [
              "公式与图形",
              "码道记录包含候选公式、质量提示与图形交互迭代。手写 OCR 仍有空结果，完整几何内核及化学持久化待完善。",
            ],
          ]}
        />
        <Note>
          本页依据用户提供的录屏与后续开发记录整理，本次未重新连接设备复测。旧移植文档反映早期阶段，新补材料中的可见操作与剩余边界以本页说明为准。
        </Note>
        <Evidence>
          材料来源 · HM-01／HM-02／HM-03 · CA-02／CA-03／CA-06／CA-08 ·
          华为云码道使用证明材料
        </Evidence>
      </Page>
    );
  }

  function FeatureIndex() {
    return (
      <Page
        label="功能索引 / 完整能力概览"
        className="expanded-page feature-index"
      >
        <Heading
          eyebrow="CAPABILITIES / 功能索引"
          title={
            <>
              从现场书写
              <br />
              到课程内容的长期复用
            </>
          }
        >
          按使用任务索引主体与扩展能力。功能入口随模块安装和设置变化，图文页及网页演示提供对应操作场景。
        </Heading>
        <DataTable
          headings={["使用任务", "功能覆盖", "详见"]}
          rows={[
            [
              "书写与配色",
              "八种笔迹风格、硬件压感、笔速笔锋、笔尖形状／倾斜、水墨设置、自定义色板、粗细与透明度预览",
              "08",
            ],
            [
              "擦除与识别",
              "面积擦及圆形范围、墨迹擦、套索擦，停笔识形与方向吸附，单击描点与轻抖容错",
              "06–08",
            ],
            [
              "选择与对象",
              "矩形／套索选择、全选／反选、缩放旋转、拖动克隆、颜色／线型／填充、混合对象层级",
              "09–10",
            ],
            [
              "分页白板",
              "逐页纸型、页面导航与缩略图、插入／删除／排序、多页存储、图片／矢量图／视频、清屏与退出保存",
              "09、17",
            ],
            [
              "桌面与演示",
              "无焦点激活、多屏覆盖与定位、PPT 放映／阅读联动、四角侧边翻页工具、幻灯片墨迹切换",
              "07、11",
            ],
            [
              "内容跟随",
              "实验性 Word／PPT／Edge 滚动缩放、固定区域裁切、本会话页面记忆与重新绑定",
              "11",
            ],
            [
              "PDF",
              "打开方式、连续／双页滚动、旋转、缩略图／大纲／搜索、跨页批注、矢量保存、页边出血、导入白板、阅读续接",
              "12",
            ],
            [
              "数学工作区",
              "2D／3D 构造、函数与曲面、参数联动、CAS、公式键盘／手写候选、受限脚本与多格式导出",
              "13–14",
            ],
            [
              "AI 与工具",
              "多供应商与模型列表、四类 API 协议、附件、流式会话、画布工具／Agent、模块适配、可选 MCP",
              "15、18",
            ],
            [
              "课堂活动",
              "倒计时、预设／循环／提示音、秒表计次、多人点名、名单管理、10 天半衰期软冷却",
              "16",
            ],
            [
              "保存与查找",
              "流式文稿、截屏底图存档、自动／退出保存、白板续接、文稿库预览与收藏、AI 命名建议、资源管理器预览",
              "05、17",
            ],
            [
              "部署与同步",
              "MSI／便携包、运行时脚本、按需插件下载和更新、搜索设置、云端备份／个人配置同步、登录与批量部署接口",
              "18、24",
            ],
            [
              "鸿蒙适配",
              "ArkTS／ArkUI 画布、原生控件、基础算法互认、设备操作录屏与专项验证边界",
              "19–20",
            ],
          ]}
        />
        <Evidence>
          范围依据 · 当前源码与 CHANGELOG · 模块专题文档 ·
          具体平台边界见对应页面
        </Evidence>
      </Page>
    );
  }

  function AnalyticCase() {
    return (
      <Page
        label="课堂案例 / 椭圆与直线"
        className="expanded-page analytic-case-page"
      >
        <Heading
          eyebrow="CASE STUDY / 解析几何课堂"
          title="从焦点条件，讲到面积关系"
        >
          案例取自用户提供的全国一卷 PDF 第 4 页第 18 题，展示第 1 问与第 2
          问（i）的推导，串联原题批注、手写作图、代数计算与 AI 讲解。
        </Heading>
        <div className="case-question">
          <Label>读取条件 · 焦点、离心率与面积比</Label>
          <p>
            椭圆左焦点 F(−1, 0)，离心率为 1/2。过 F 作正斜率直线，与椭圆交于
            P、Q，Q 在第三象限。R 为 P 关于原点的对称点，S(PQR) = 3S(PFO)。
          </p>
        </div>
        <Figure
          src="cases/ellipse-board-detail.png"
          alt="椭圆、交点、对称点及联立、韦达和面积比的完整手写推导"
          caption="425 条编排路径经实际墨迹控制器逐笔写入，保存为可继续编辑的白板文稿"
          className="case-board"
        />
        <div className="case-steps">
          <div>
            <span>原题批注</span>
            <h3>先确定椭圆</h3>
            <p>由 c = 1、e = c/a = 1/2，得 a = 2、b² = 3。</p>
            <strong>x²/4 + y²/3 = 1</strong>
          </div>
          <div>
            <span>白板推导</span>
            <h3>联立与韦达</h3>
            <p>设直线 y = k(x + 1)，将交点横坐标记为 x₁、x₂。</p>
            <strong>
              (3 + 4k²)x² + 8k²x
              <br />+ 4k² − 12 = 0
            </strong>
          </div>
          <div>
            <span>几何关系</span>
            <h3>由面积比求斜率</h3>
            <p>结合 R = −P 与面积条件，得到 x₁ + 2x₂ + 3 = 0。</p>
            <strong>
              k = √5/2
              <br />y = √5(x + 1)/2
            </strong>
          </div>
        </div>
        <div className="case-followup">
          <div>
            <Icon name="ai" />
            <p>
              <b>AI 衔接</b>真实 DeepSeek
              返回同题推导，并通过工具新建白板页，供教师继续组织讲解。
            </p>
          </div>
          <div>
            <Icon name="save" />
            <p>
              <b>课后复用</b>可编辑文稿与带批注 PDF
              随网页附带，供打开、擦改与再次授课。
            </p>
          </div>
        </div>
        <div className="case-files">
          <a href={assets + "cases/高考18题-解析几何板书.icink"} download>
            下载可编辑板书 ↗
          </a>
          <a href={assets + "cases/高考数学全国一卷.pdf"} download>
            下载批注 PDF ↗
          </a>
          <span className="print-case-hint">
            配套可编辑白板与批注 PDF，可在网页说明书本页下载。
          </span>
        </div>
        <Note>
          题目与年份按用户提供文件记载。第 2
          问（ii）继续保留在题面，本案例覆盖上述两部分。笔迹为原生输入回放，实体笔手感与端到端延迟需另测。
        </Note>
      </Page>
    );
  }

  function DemoGallery() {
    return (
      <Page label="操作演示 / 课堂任务" className="expanded-page demo-page">
        <Heading
          eyebrow="DEMONSTRATION / 操作演示"
          title="解析几何、空间探究与课堂协作"
        >
          网页版可播放六段新版演示，PDF
          保留对应画面。椭圆题贯穿讲义与板书，再拓展到空间截面、图形库和 AI
          课堂协作。
        </Heading>
        <div className="demo-grid">
          <Demo
            name="gaokao-pdf"
            title="真题定位与批注"
            poster="cases/gaokao-pdf-final.png"
            duration="6 秒"
          >
            定位第 18 题，标记焦点与面积条件，翻页后恢复批注。
          </Demo>
          <Demo
            name="advanced-writing"
            title="椭圆题手写推导"
            poster="cases/advanced-writing-final.png"
            duration="17 秒"
          >
            逐笔绘制坐标、椭圆与交点，用韦达和面积关系求解。
          </Demo>
          <Demo
            name="shape-catalog"
            title="47 种图形全览"
            poster="cases/shape-catalog-final.png"
            duration="3 秒"
          >
            插入线条、坐标、圆锥曲线和立体图形，保留可编辑构造。
          </Demo>
          <Demo
            name="geometry3d"
            title="立方体截面探究"
            poster="cases/geometry3d-final.png"
            duration="9 秒"
          >
            旋转空间视角，观察平面、立方体及其交线的关系。
          </Demo>
          <Demo
            name="assistant-real"
            title="真实 AI 与画布工具"
            poster="cases/assistant-real-final.png"
            duration="6 秒"
          >
            展示 DeepSeek 同题回复及新建页面结果，教师核验后使用。
          </Demo>
          <Demo
            name="classroom"
            title="限时讨论与多人点名"
            poster="cases/classroom-final.png"
            duration="8 秒"
          >
            用倒计时组织讨论，抽取三位同学参与讲解与交流。
          </Demo>
        </div>
        <div className="demo-reading">
          <Icon name="play" />
          <div>
            <h3>配套完整演示</h3>
            <p>
              新版 A／B／C 各 2 分 54
              秒，分别强调手写流程、空间探究和工具协同。新补的鸿蒙录屏可在第 20
              页播放。
            </p>
          </div>
        </div>
        <Note>
          原生业务采集经逐帧合成。AI
          片段为真实回复原样回放取景。硬件延迟与实时生成速度需另测，网页资源可离线播放。
        </Note>
      </Page>
    );
  }

  return {
    Origins,
    Engineering,
    Performance,
    Touch,
    ObjectWorkflow,
    Desktop,
    MathCapabilities,
    Extensions,
    Harmony,
    HarmonyValidation,
    FeatureIndex,
    AnalyticCase,
    DemoGallery,
  };
}
