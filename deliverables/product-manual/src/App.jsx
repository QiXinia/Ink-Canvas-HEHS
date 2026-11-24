import React, { createContext, useContext, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { createExpandedPages } from "./ExpandedPages";
import { createEvidencePages } from "./EvidencePages";
import "./manual.css";

const assetPath = "./assets/";
const glyphs = {
  pen: "\uF3DD",
  eraser: "\uE5F2",
  board: "\uF8C3",
  shapes: "\uF6A1",
  pdf: "\uF3AD",
  pages: "\uE52D",
  save: "\uF68D",
  time: "\uF840",
  people: "\uF5B3",
  ai: "\uEB3D",
  setting: "\uF6B3",
  play: "\uF610",
  download: "\uF151",
  search: "\uF69A",
  help: "\uF648",
  undo: "\uF19A",
};
const PageNumber = createContext(1);
const expanded = createExpandedPages({
  Page,
  Heading,
  Figure,
  Feature,
  Note,
  Label,
  Icon,
});
const evidencePages = createEvidencePages({
  Page,
  Heading,
  Figure,
  Feature,
  Note,
});
const pages = [
  ["封面", Cover],
  ["项目来源与重构贡献", expanded.Origins],
  ["场景背景", Scenarios],
  ["功能架构", Architecture],
  ["代码分层与存档重构", expanded.Engineering],
  ["墨迹管线与性能", expanded.Performance],
  ["WinUI 3 与触摸体验", expanded.Touch],
  ["自然书写", Writing],
  ["白板与对象", Whiteboard],
  ["对象、媒体与回放", expanded.ObjectWorkflow],
  ["桌面与 PPT 协同", expanded.Desktop],
  ["PDF 阅读与批注", Documents],
  ["解析几何", Geometry],
  ["数学能力与公式输入", expanded.MathCapabilities],
  ["AI 助手与 Agent", Assistant],
  ["课堂工具", Classroom],
  ["文稿、续接与同步", Archive],
  ["插件、设置与更新", expanded.Extensions],
  ["鸿蒙原生适配", expanded.Harmony],
  ["鸿蒙验证状态", expanded.HarmonyValidation],
  ["完整功能索引", expanded.FeatureIndex],
  ["椭圆题课堂案例", expanded.AnalyticCase],
  ["操作演示", expanded.DemoGallery],
  ["部署与开源组件", GetStarted],
  ["码道交互与 Prompt", evidencePages.CodeArtsOverview],
  ["码道代码生成与重构", evidencePages.CodeArtsGeneration],
  ["码道排错与验证", evidencePages.CodeArtsDebug],
  ["码道契约与网关协作", evidencePages.CodeArtsCollaboration],
];

function Icon({ name, className = "" }) {
  return (
    <span className={`icon ${className}`} aria-hidden="true">
      {glyphs[name] || glyphs.board}
    </span>
  );
}

function Page({ label, className = "", children }) {
  const number = useContext(PageNumber);
  return (
    <section
      className={`page ${className}`}
      id={`page-${number}`}
      aria-label={`第 ${number} 页 ${label}`}
    >
      <header className="page-top">
        <span>
          <img
            className="page-brand-logo"
            src={assetPath + "logo.png"}
            alt=""
          />
          INK CANVAS
        </span>
        <span>{label}</span>
      </header>
      <div className="page-body">{children}</div>
      <footer className="page-footer">
        <span>Ink Canvas · 架构重构与产品说明</span>
        <span>
          {String(number).padStart(2, "0")}
          <i />
          {pages.length}
        </span>
      </footer>
    </section>
  );
}

function Heading({ eyebrow, title, children }) {
  return (
    <div className="heading">
      <div className="eyebrow">{eyebrow}</div>
      <h1>{title}</h1>
      {children && <p className="lead">{children}</p>}
    </div>
  );
}

function Figure({ src, alt, caption, className = "", imageClass = "" }) {
  return (
    <figure className={`figure ${className}`}>
      <div className="image-frame">
        <img className={imageClass} src={assetPath + src} alt={alt} />
      </div>
      {caption && <figcaption>{caption}</figcaption>}
    </figure>
  );
}

function Feature({ icon, title, children, className = "" }) {
  return (
    <div className={`feature ${className}`}>
      <Icon name={icon} />
      <div>
        <h3>{title}</h3>
        <p>{children}</p>
      </div>
    </div>
  );
}

function Note({ children }) {
  return <p className="note">{children}</p>;
}
function Label({ children }) {
  return <span className="label">{children}</span>;
}

function Cover() {
  return (
    <Page label="产品说明书" className="cover">
      <div className="cover-brand">
        <img src={assetPath + "logo.png"} alt="Ink Canvas 标志" />
        <div>
          <strong>Ink Canvas</strong>
          <span>WINUI 3 EDITION</span>
        </div>
      </div>
      <div className="cover-heading">
        <div className="eyebrow">现代桌面与鸿蒙原生适配</div>
        <h1>
          屏幕批注与
          <br />
          <em>课堂白板</em>
        </h1>
        <p>
          架构重构 · WinUI 3 · 触摸交互
          <br />
          从现场书写，到可以持续复用的课堂内容。
        </p>
      </div>
      <div className="cover-image">
        <img
          src={assetPath + "cases/ellipse-board-detail.png"}
          alt="椭圆与直线相交问题的手写坐标图和代数推导"
        />
        <div className="cover-toolbar">
          <Icon name="pen" />
          <Icon name="eraser" />
          <span />
          <Icon name="shapes" />
          <Icon name="pages" />
          <Icon name="save" />
        </div>
      </div>
      <div className="cover-bottom">
        <div>
          <b>写在课件上</b>
          <span>桌面 · PPT · PDF</span>
        </div>
        <div>
          <b>在白板中展开</b>
          <span>手写 · 图形 · 交互</span>
        </div>
        <div>
          <b>把课堂内容留下</b>
          <span>存档 · 文稿库 · 可选云同步</span>
        </div>
      </div>
      <div className="cover-edition">
        2026.11<span>Windows v3.15.0 · 鸿蒙适配进度</span>
      </div>
    </Page>
  );
}

function Scenarios() {
  const scenes = [
    [
      "课前",
      "把材料准备在一起",
      "打开 PDF 讲义，整理已有白板文稿，为需要演示的函数或几何构造做好准备。",
      "pdf",
    ],
    [
      "课中",
      "跟着讲解随手书写",
      "在课件上圈出重点，切到白板展开推导，再用图形、倒计时与点名组织互动。",
      "pen",
    ],
    [
      "课后",
      "保留可以继续的内容",
      "把墨迹、图形与媒体保存为文稿，通过预览寻找课程内容，下次接着讲。",
      "save",
    ],
  ];
  return (
    <Page label="场景背景">
      <Heading
        eyebrow="CONTEXT / 场景背景"
        title={
          <>
            一节课里的
            <br />
            讲解与书写
          </>
        }
      >
        课堂内容分布在演示文稿、电子讲义、网页和白板中。教师需要在这些材料旁解释、推导和补充，并保留有价值的过程。
      </Heading>
      <Figure
        src="ppt-annotation.png"
        alt="PPT 放映中的 Ink Canvas 浮动工具栏"
        caption="PPT 放映场景 · 浮动工具栏与课件同时呈现"
        className="scenario-shot"
      />
      <div className="scene-list">
        {scenes.map(([phase, title, text, icon]) => (
          <div className="scene" key={phase}>
            <span className="phase">{phase}</span>
            <Icon name={icon} />
            <div>
              <h3>{title}</h3>
              <p>{text}</p>
            </div>
          </div>
        ))}
      </div>
      <nav className="contents-grid" aria-label="说明书目录">
        {[
          [2, "项目来源与重构"],
          [4, "架构与渲染管线"],
          [7, "原生桌面与触摸"],
          [8, "书写与对象编辑"],
          [11, "PPT 与 PDF"],
          [13, "数学与 AI 助手"],
          [16, "课堂工具与存档"],
          [18, "插件与鸿蒙适配"],
          [21, "功能索引与案例"],
          [24, "部署与码道实践"],
        ].map(([n, t]) => (
          <a href={`#page-${n}`} key={n}>
            <span>{String(n).padStart(2, "0")}</span>
            {t}
          </a>
        ))}
      </nav>
    </Page>
  );
}

function Architecture() {
  return (
    <Page label="功能架构">
      <Heading
        eyebrow="ARCHITECTURE / 功能架构"
        title={
          <>
            统一的书写基础
            <br />
            按需扩展课堂能力
          </>
        }
      >
        主程序承载批注与白板，原生模块扩展文档、数学和 AI
        等功能。工具、对象与存档通过共用接口协作。
      </Heading>
      <div className="architecture">
        <div className="input-row">
          <span>输入设备</span>
          <b>鼠标</b>
          <b>触摸大屏</b>
          <b>压感笔／数位板</b>
        </div>
        <div className="flow-connector">↓</div>
        <div className="core-shell">
          <div className="core-title">
            <b>Ink Canvas 主程序</b>
            <span>共用浮动工具栏</span>
          </div>
          <div className="mode-row">
            <div>
              <Icon name="pen" />
              <b>屏幕批注</b>
              <small>在现有界面上标记</small>
            </div>
            <div>
              <Icon name="board" />
              <b>分页白板</b>
              <small>持续书写与对象编辑</small>
            </div>
            <div>
              <Icon name="play" />
              <b>演示联动</b>
              <small>PPT 翻页与批注切换</small>
            </div>
          </div>
          <div className="engine-row">
            <span>墨迹与对象模型</span>
            <span>撤销／重做</span>
            <span>选择与手势</span>
          </div>
        </div>
        <div className="flow-connector">
          ↓ <span>原生模块与插件接口</span> ↓
        </div>
        <div className="module-grid">
          {[
            ["pdf", "PDF 阅读与批注"],
            ["shapes", "解析几何"],
            ["ai", "AI 助手"],
            ["pages", "文稿库"],
            ["save", "云同步"],
            ["setting", "设备与外部工具"],
          ].map(([i, t]) => (
            <div key={t}>
              <Icon name={i} />
              <span>{t}</span>
            </div>
          ))}
        </div>
        <div className="flow-connector">↓</div>
        <div className="output-row">
          <div>
            <strong>.icink</strong>
            <span>保留可编辑白板</span>
          </div>
          <div>
            <strong>PDF</strong>
            <span>保存文档批注</span>
          </div>
          <div>
            <strong>PNG</strong>
            <span>分享课堂画面</span>
          </div>
        </div>
      </div>
      <div className="technical-strip">
        <div>
          <b>WinUI 3</b>
          <p>窗口、原生控件与触摸界面</p>
        </div>
        <div>
          <b>Win2D</b>
          <p>墨迹、图形与增量渲染</p>
        </div>
        <div>
          <b>模块化</b>
          <p>独立安装、启用与更新</p>
        </div>
      </div>
      <Note>
        云同步需要自行配置服务；AI
        能力取决于已配置的模型与已启用的工具。具体功能随安装的模块显示。
      </Note>
    </Page>
  );
}

function Writing() {
  return (
    <Page label="核心亮点 / 自然书写">
      <Heading
        eyebrow="WRITING / 自然书写"
        title={
          <>
            画笔，调到顺手
            <br />
            笔迹，保留个性
          </>
        }
      >
        普通触摸、硬件压感与不同笔锋共用书写入口。颜色、粗细和透明度可以随课堂需要快速调整。
      </Heading>
      <div className="writing-showcase">
        <div className="writing-paper">
          <img
            src={assetPath + "cases/advanced-writing-final.png"}
            alt="通过实际墨迹引擎逐笔绘制的椭圆题板书"
          />
        </div>
        <img
          className="pen-float"
          src={assetPath + "pen-panel.png"}
          alt="实际画笔选项面板"
        />
      </div>
      <div className="writing-two">
        <div>
          <Feature icon="pen" title="八种笔迹风格">
            从平滑硬笔、自然笔锋，到书法与水墨效果。硬件压感笔按真实压力改变笔宽，普通触摸使用笔速等轨迹信息模拟笔触。
          </Feature>
          <div className="brush-samples">
            {[
              ["SmoothPen", "平滑硬笔"],
              ["Natural", "自然笔锋"],
              ["Calligraphy", "书法笔锋"],
              ["InkWash", "水墨笔锋"],
            ].map(([id, title]) => (
              <div key={id}>
                <img
                  src={assetPath + `stroke-${id}.png`}
                  alt={title + "实际渲染笔迹"}
                />
                <span>{title}</span>
              </div>
            ))}
          </div>
        </div>
        <div className="eraser-callout">
          <div>
            <Label>面积擦</Label>
            <h3>边调大小，边看范围</h3>
            <p>10–200 px 直径滑动条与圆形预览联动，也可切换墨迹擦和套索擦。</p>
          </div>
          <img
            src={assetPath + "eraser-panel.png"}
            alt="带直径滑动条及圆形预览的面积擦面板"
          />
        </div>
      </div>
      <div className="bottom-band">
        <strong>调节实时生效</strong>
        <p>
          粗细与不透明度拖动时更新，松手后合并保存。预览按帧更新，颜色和透明度变化复用已有轮廓。
        </p>
      </div>
    </Page>
  );
}

function Whiteboard() {
  return (
    <Page label="核心亮点 / 白板与对象">
      <Heading
        eyebrow="WHITEBOARD / 白板与对象"
        title={
          <>
            把想法画出来
            <br />
            再把细节调整好
          </>
        }
      >
        白板提供分页、纸型和对象操作。手写、形状、图片与媒体可以组合呈现，讲解过程可以继续修改。
      </Heading>
      <Figure
        src="board-solids.png"
        alt="白板中绘制并标注的立体几何图形"
        caption="用户素材中的立体几何板书 · 图形与手写标注组合使用"
        className="whiteboard-shot"
      />
      <div className="feature-grid">
        <Feature icon="shapes" title="图形绘制与调整">
          平面图形、坐标与立体图形分类选取。长按保持工具选中，适合连续绘制。
        </Feature>
        <Feature icon="board" title="矩形与套索选择">
          框选或圈选对象，使用全选、反选、缩放、旋转和图层顺序完成整理。
        </Feature>
        <Feature icon="pages" title="分页与不同纸型">
          通过页面导航切换内容，为不同页面设置合适的背景，容纳多步骤板书。
        </Feature>
        <Feature icon="undo" title="拖动、克隆与撤销">
          移动选区查看中间过程，按需复制对象。误操作可通过撤销、重做修正。
        </Feature>
      </div>
      <div className="object-flow">
        <span>选择对象</span>
        <b>→</b>
        <span>平移／旋转／缩放</span>
        <b>→</b>
        <span>复制与调整层级</span>
      </div>
      <Note>
        多指手势与设备输入可在设置中调整，适配教学大屏和二合一设备的操作习惯。
      </Note>
    </Page>
  );
}

function Documents() {
  return (
    <Page label="原生模块 / PDF 阅读与批注">
      <Heading
        eyebrow="PDF / 原生阅读与批注"
        title={
          <>
            顺着讲义阅读
            <br />
            将讲解留在文档中
          </>
        }
      >
        原生 PDF
        窗口沿用主工具栏。阅读、跨页书写与矢量批注保存衔接，墨迹形状和原文档内容一同保留。
      </Heading>
      <div className="document-primary">
        <Figure
          src="cases/gaokao-pdf-final.png"
          alt="原生 PDF 窗口定位第18题，圈记焦点、面积条件并手写椭圆方程"
          caption="全国一卷 PDF 第 18 题 · 标记焦点与面积条件，翻页后返回保留批注"
        />
        <div className="pdf-label">
          <Icon name="pdf" />
          <b>PDF 原生阅读与批注</b>
        </div>
      </div>
      <div className="three-columns">
        <div>
          <h3>读得连贯</h3>
          <p>连续翻页、页面缩略图、搜索与缩放，配合触摸滚动浏览讲义。</p>
        </div>
        <div>
          <h3>直接批注</h3>
          <p>沿用浮动主工具栏，将手写与高亮写在页面上，并保存到 PDF。</p>
        </div>
        <div>
          <h3>继续阅读</h3>
          <p>按文档记忆阅读页码，支持自动恢复或短暂提醒，设置独立控制。</p>
        </div>
      </div>
      <div className="pdf-details">
        <Feature icon="pages" title="连续滚动与触摸阅读">
          单页／双页连续布局、单独显示封面与页面旋转；单指阅读、双指平移缩放，缩略图和目录导航可折叠。
        </Feature>
        <Feature icon="pen" title="矢量批注与页边空间">
          跨页笔迹归入相应页面，文档共用撤销记录。页外批注保存时按轮廓扩展页面与出血范围，再次打开可继续编辑。
        </Feature>
      </div>
      <Note>
        支持从 Windows“打开方式”打开
        PDF，也可选页导入白板。文字搜索需要文档已有文字层，扫描件需预先
        OCR。页面按需渲染并限制位图缓存。
      </Note>
    </Page>
  );
}

function Geometry() {
  return (
    <Page label="原生模块 / 解析几何">
      <Heading
        eyebrow="MATHEMATICS / 解析几何"
        title={
          <>
            让表达式与图形
            <br />
            一起变化
          </>
        }
      >
        输入函数与构造关系，在原生数学工作区中观察二维、三维结果，并将图形插入白板继续讲解。
      </Heading>
      <Figure
        src="cases/geometry3d-final.png"
        alt="WinUI 3 数学工作区中旋转观察立方体、截面和平面的空间关系"
        caption="空间探究案例 · 立方体、平面与截线关联构造，旋转视角观察空间关系"
        className="geometry-hero"
      />
      <div className="geometry-detail">
        <Figure
          src="cases/geometry3d-15.jpg"
          alt="同一立方体截面构造在另一观察角度下的显示"
          caption="旋转前的视角 · 构造对象与关联保持一致"
        />
        <div>
          <h3>让截面关系可以转动观察</h3>
          <p>
            创建立方体，再用过原点的平面求交。转动视角辨认交线，讨论平面与几何体的空间关系。
          </p>
          <div className="formula-example">
            g = Cube((-2, -2, -2), 4)
            <br />p = Plane((0, 0, 0), Vector((1, 1, 1)))
            <br />c = Intersect(p, g)
          </div>
        </div>
      </div>
      <div className="feature-grid tight">
        <Feature icon="setting" title="原生公式输入">
          数学键盘与 MathML 显示配合使用，也可调用 Windows 本地手写候选。
        </Feature>
        <Feature icon="shapes" title="作为对象插入">
          保留公式信息，便于整体选择、变换和再次编辑，在课堂中继续调整。
        </Feature>
      </div>
      <Note>
        解析几何为可选模块。本地计算与工具支持范围详见项目文档，Windows
        手写识别需对应语言支持。
      </Note>
    </Page>
  );
}

function Assistant() {
  return (
    <Page label="原生模块 / AI 助手" className="real-assistant-page">
      <Heading
        eyebrow="AI ASSISTANT / AI 助手"
        title={
          <>
            围绕课堂内容
            <br />
            展开下一步讨论
          </>
        }
      >
        在原生对话侧栏中提交问题、白板截图与文件。教师可以选择模型服务，并控制是否允许助手使用画布工具。
      </Heading>
      <Figure
        src="cases/assistant-response-detail.png"
        alt="原生AI助手呈现DeepSeek返回的椭圆方程、韦达与面积关系推导"
        caption="真实 DeepSeek 回复重新取景 · 围绕同一道椭圆题生成讲解，并成功调用新建白板页工具"
        className="real-assistant-shot"
      />
      <div className="feature-grid tight">
        <Feature icon="pages" title="围绕原题继续追问">
          截图、选区与附件提供课堂上下文。回复支持
          Markdown、流式阅读和停止生成。
        </Feature>
        <Feature icon="board" title="让建议衔接画布操作">
          本次真实请求调用 board_page，新建第 2
          页。工具注册表还可对接对象、工作区及已安装模块。
        </Feature>
      </div>
      <div className="protocols">
        <Label>连接方式</Label>
        <p>
          OpenAI Chat Completions · OpenAI Responses
          <br />
          Anthropic Messages · Google Gemini
        </p>
      </div>
      <Note>
        本页采用已保存的真实接口响应原样取景，生成等待时间未在镜头中完整呈现。使用
        AI 需配置模型服务，课堂结论由教师核验。
      </Note>
    </Page>
  );
}

function Classroom() {
  return (
    <Page label="课堂工具" className="classroom-page">
      <Heading
        eyebrow="CLASSROOM / 课堂工具"
        title={
          <>
            让课堂活动
            <br />
            有节奏地展开
          </>
        }
      >
        计时、点名与书写保持在同一个工具体系中。独立的原生窗口适合教师在课堂中随时打开、调整和收起。
      </Heading>
      <div className="classroom-pair">
        <div>
          <Figure
            src="timer.png"
            alt="WinUI 3 倒计时窗口"
            caption="倒计时 · 点击时间直接修改"
          />
          <h3>倒计时与秒表</h3>
          <p>
            使用预设时长快速开始，暂停、复位或补充时间。秒表可用于记录活动耗时，触摸按钮便于大屏操作。
          </p>
        </div>
        <div>
          <Figure
            src="lottery.png"
            alt="WinUI 3 点名抽选窗口"
            caption="点名抽选 · 数字示例"
          />
          <h3>点名与抽选</h3>
          <p>
            可选择抽选人数，使用名单或数字点名。软冷却方式依据近期历史降低重复概率，并随时间恢复。
          </p>
        </div>
      </div>
      <div className="cooldown-panel">
        <div>
          <Label>默认点名策略</Label>
          <h2>软冷却，保留随机性</h2>
          <p>
            近期抽中过的对象仍有机会再次抽中。默认冷却半衰期为 10
            天，可在设置中单独调整。
          </p>
        </div>
        <div
          className="cooldown-bars"
          aria-label="抽中后概率权重逐渐恢复的示意"
        >
          <div>
            <span>刚抽中</span>
            <i style={{ "--bar": "44%" }} />
          </div>
          <div>
            <span>一段时间后</span>
            <i style={{ "--bar": "67%" }} />
          </div>
          <div>
            <span>更久以后</span>
            <i style={{ "--bar": "90%" }} />
          </div>
          <small>相对权重恢复示意</small>
        </div>
      </div>
      <div className="stopwatch-strip">
        <img
          src={assetPath + "stopwatch.png"}
          alt="原生秒表暂停后显示两次计次结果"
        />
        <div>
          <h3>秒表与计次</h3>
          <p>
            切换到秒表，开始活动并记录每次用时。累计时间和单次时间分别呈现，支持暂停、继续与复位。
          </p>
        </div>
      </div>
      <Note>
        名单在设置中管理；抽选结果在独立窗口显示。示例截图中的数字用于展示界面。
      </Note>
    </Page>
  );
}

function Archive() {
  return (
    <Page label="文稿与同步">
      <Heading
        eyebrow="CONTINUITY / 文稿与同步"
        title={
          <>
            课堂结束以后
            <br />
            板书仍然可以继续
          </>
        }
      >
        保存可编辑内容，通过页面预览找到熟悉的课程，再根据需要恢复阅读进度或同步到其他设备。
      </Heading>
      <Figure
        src="library.png"
        alt="具有文件导航、缩略图和详情预览的文稿库"
        caption="本机 WinUI 3 实际截图 · 文稿库的导航、缩略图与多页预览"
        className="library-hero"
      />
      <div className="feature-grid">
        <Feature icon="search" title="看图寻找文稿">
          最近使用、收藏、文件夹筛选与名称搜索，配合缩略图和逐页预览快速定位内容。
        </Feature>
        <Feature icon="save" title="自动保存与备份">
          自定义默认存档目录，设置自动保存、退出保存及文件命名规则。
        </Feature>
        <Feature icon="pages" title="从中断处继续">
          白板与 PDF 分别选择自动恢复或快速提醒。白板恢复页面与视图，PDF
          记忆阅读页码。
        </Feature>
        <Feature icon="setting" title="可选云同步">
          连接自行部署的服务，登录后开启白板备份或个人书写、外观偏好同步。
        </Feature>
      </div>
      <div className="archive-path">
        <div>
          <strong>保存</strong>
          <span>墨迹与对象</span>
        </div>
        <b>→</b>
        <div>
          <strong>预览</strong>
          <span>封面与页面</span>
        </div>
        <b>→</b>
        <div>
          <strong>继续</strong>
          <span>打开与再编辑</span>
        </div>
      </div>
      <Note>
        文稿保存在用户选定的文件夹。云同步需单独配置 HTTPS
        服务并登录，默认不预设服务地址。
      </Note>
    </Page>
  );
}

function GetStarted() {
  return (
    <Page label="部署与上手">
      <Heading
        eyebrow="GET STARTED / 部署与上手"
        title={
          <>
            准备好设备
            <br />
            开始第一节课
          </>
        }
      >
        安装应用后，先确认显示器、输入与保存位置。按需要添加原生模块，再把常用操作调整到适合自己的方式。
      </Heading>
      <div className="start-steps">
        {[
          [
            "安装",
            "下载 MSI 或 ZIP，核对 SHA256。准备 .NET 10 x64 Runtime，运行应用。",
          ],
          [
            "配置",
            "选择主题与书写模式，检查多屏显示与触控；设置存档目录和自动保存。",
          ],
          [
            "扩展",
            "在“模块与插件”中按需安装 PDF、解析几何、AI 助手和文稿库等模块。",
          ],
          [
            "使用",
            "打开课件或白板，选中画笔开始书写；再次点击当前工具，可打开其选项面板。",
          ],
        ].map(([t, p], i) => (
          <div key={t}>
            <span>{i + 1}</span>
            <div>
              <h3>{t}</h3>
              <p>{p}</p>
            </div>
          </div>
        ))}
      </div>
      <div className="environment">
        <h2>运行环境</h2>
        <dl>
          <div>
            <dt>推荐系统</dt>
            <dd>Windows 11 x64</dd>
          </div>
          <div>
            <dt>必需运行时</dt>
            <dd>.NET 10 x64 Runtime</dd>
          </div>
          <div>
            <dt>界面组件</dt>
            <dd>Windows App SDK 随默认包携带</dd>
          </div>
          <div>
            <dt>演示联动</dt>
            <dd>PowerPoint／WPS 桌面版，按需启用</dd>
          </div>
        </dl>
      </div>
      <div className="start-bottom">
        <div>
          <h3>一键检查与安装</h3>
          <p>
            发行包的 Tools 目录包含安装脚本，可检查运行时，并继续安装本地 MSI。
          </p>
          <code>.\Tools\install-runtime.ps1</code>
        </div>
        <div>
          <h3>开源与说明</h3>
          <p>
            项目沿用上游开源许可。WinUI 3、Win2D、PDFsharp、PdfPig、AngouriMath
            等组件的说明和许可随项目提供。
          </p>
        </div>
      </div>
      <a
        className="repository"
        href="https://github.com/QiXinia/Ink-Canvas-HEHS"
      >
        <Icon name="download" />
        <div>
          <b>获取最新版本与完整文档</b>
          <span>github.com/QiXinia/Ink-Canvas-HEHS</span>
        </div>
        <span className="link-arrow">↗</span>
      </a>
      <Note>
        本文基于 v3.15.0。截图来自用户提供素材、本机 WinUI 3
        界面及项目演示资料；真实硬件手感需在目标教室设备上体验。
      </Note>
    </Page>
  );
}

function App() {
  const [active, setActive] = useState(1);
  useEffect(() => {
    const observer = new IntersectionObserver(
      (entries) => {
        const visible = entries
          .filter((entry) => entry.isIntersecting)
          .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];
        if (visible) setActive(Number(visible.target.id.replace("page-", "")));
      },
      { threshold: [0.2, 0.5, 0.8] },
    );
    document
      .querySelectorAll(".page")
      .forEach((page) => observer.observe(page));
    return () => observer.disconnect();
  }, []);
  return (
    <>
      <div className="reader-controls">
        <a href="#page-1" className="reader-brand">
          <img src={assetPath + "logo.png"} alt="" />
          <span>
            Ink Canvas <small>产品说明书</small>
          </span>
        </a>
        <div className="reader-navigation">
          <label htmlFor="page-select">页码</label>
          <select
            id="page-select"
            value={active}
            onChange={(event) =>
              document
                .getElementById(`page-${event.target.value}`)
                .scrollIntoView({ behavior: "smooth" })
            }
          >
            {pages.map(([t], i) => (
              <option value={i + 1} key={t}>
                {String(i + 1).padStart(2, "0")} / {t}
              </option>
            ))}
          </select>
          <button onClick={() => window.print()}>
            <Icon name="download" />
            打印 / 保存 PDF
          </button>
        </div>
      </div>
      <main>
        {pages.map(([title, Component], index) => (
          <PageNumber.Provider value={index + 1} key={title}>
            <Component />
          </PageNumber.Provider>
        ))}
      </main>
    </>
  );
}

createRoot(document.getElementById("root")).render(<App />);
