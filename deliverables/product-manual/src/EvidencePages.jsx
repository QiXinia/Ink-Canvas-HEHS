import React from "react";

export function createEvidencePages({ Page, Heading, Figure, Feature, Note }) {
  function CodeArtsOverview() {
    return (
      <Page
        label="华为云码道 / AI 交互与 Prompt"
        className="expanded-page codearts-evidence"
      >
        <Heading
          eyebrow="CODEARTS / 开发实践"
          title={
            <>
              把课堂需求
              <br />
              转成可迭代的开发任务
            </>
          }
        >
          使用华为云码道 CodeArts
          代码智能体，围绕鸿蒙迁移、工具栏交互、公式识别和化学绘制提出需求，通过读取、修改、构建与设备反馈持续调整实现。
        </Heading>
        <Figure
          src="codearts/prompt-follow.jpg"
          alt="用户在码道中要求设置跟随工具栏、折叠后可切换工具和滚动操作的原始Prompt"
          caption="CA-03 用户原始 Prompt 局部 · 明确跟随、折叠、滚动及常用工具保留等验收条件"
          className="prompt-evidence"
        />
        <div className="evidence-workflow">
          <span>提出问题与参考图</span>
          <b>→</b>
          <span>读取相关代码</span>
          <b>→</b>
          <span>生成／重构</span>
          <b>→</b>
          <span>编译与反馈</span>
        </div>
        <div className="proof-columns">
          <Figure
            src="codearts/CA-04.jpg"
            alt="码道中排查拖动偏移、读取图标目录并生成码点检查脚本的过程"
            caption="CA-04 · Prompt、读文件、编辑与命令轨迹"
            className="proof-tall"
          />
          <div className="proof-description">
            <Feature icon="setting" title="具体约束进入 Prompt">
              要求重复拖动保持触点对齐、图标清晰且不重复、窄屏折叠仍能选择常用工具。
            </Feature>
            <Feature icon="pages" title="以项目代码为上下文">
              截图中可见读取 FloatingBarCatalog.ts、IconGlyph.ets，生成
              glyph-dump.js 检查图标码点。
            </Feature>
            <Feature icon="board" title="开发者继续核对与追问">
              通过截图、运行反馈和追加要求推进修复，保留平台权限、OCR
              与设备验证的边界。
            </Feature>
          </div>
        </div>
        <div className="evidence-footnote">
          材料索引 · 13 张交互截图 · 6 份过程案例与成员原文 · 3 段鸿蒙操作录屏 · 源文件
          SHA-256 与日志行号随证明材料提供
        </div>
        <Note>
          本页截图由用户提供，保留码道界面和原始输入。设备标识已遮盖，具体技术结论以代码与可见验证记录核对。
        </Note>
      </Page>
    );
  }

  function CodeArtsGeneration() {
    const inputCode = [
      "if (this.chemOpen && (id === 'pen' || id === 'highlighter'",
      "    || id === 'mouse' || id === 'eraserCircle'",
      "    || id === 'eraserStroke' || id === 'select' || id === 'shapes')) {",
      "  this.chemInputActive = false;",
      "}",
    ].join("\n");
    return (
      <Page
        label="华为云码道 / 代码生成与重构"
        className="expanded-page codearts-evidence generation-proof-page"
      >
        <Heading
          eyebrow="CODEARTS / 代码生成与重构"
          title={
            <>
              围绕真实交互
              <br />
              逐步重组界面与输入逻辑
            </>
          }
        >
          日志保留了具体 edit／write
          调用及目标文件。原始输入、修改片段和可见回复按案例编号整理，便于核对需求如何落到代码。
        </Heading>
        <Figure
          src="codearts/refactor-toolbar.jpg"
          alt="码道中Windows工具栏与鸿蒙工具栏样式对照表"
          caption="CA-02 局部 · 胶囊、图标、选中态、拖拽点与边框的逐项设计对照"
          className="refactor-proof"
        />
        <div className="generation-cases">
          <div>
            <span>R-01 · 公式与搜题</span>
            <h3>从固定结果到内容相关反馈</h3>
            <p>
              按数学符号和运算密度挑选候选公式，组织已知／所求、质量提示和易错提醒。识别为空时显示墨迹统计与引擎状态。
            </p>
            <small>AiAssistant.ts · AiStudio.ets · Index.ets</small>
          </div>
          <div>
            <span>R-02 · 面板与折叠</span>
            <h3>共享位移，单独约束边界</h3>
            <p>
              面板跟随工具栏，settingsPanelOffsetX/Y 限制在可视区内。CompactCard
              保留常用工具，设置分组使用手风琴折叠。
            </p>
            <small>FloatingBar.ets · Index.ets</small>
          </div>
          <div>
            <span>R-03 · 化学与书写</span>
            <h3>按当前工具分配输入</h3>
            <p>
              化学结构层改为画布内交互。切到笔、荧光笔、橡皮和选择时，让化学层交出触摸，保留批注入口。
            </p>
            <small>ChemBoard.ets · Index.ets</small>
          </div>
        </div>
        <div className="logged-code">
          <span>R-03 日志中的实际修改节选</span>
          <pre>{inputCode}</pre>
        </div>
        <div className="evidence-footnote">
          代码修改的准确源文件与行号见 R-03
          JSONL，原始工具调用包含旧片段、新片段与 edit 执行结果。
        </div>
        <Note>
          日志摘录保留原有字段及源文件位置。完整 ChemDraw 兼容与手写公式 OCR
          全链路仍有差距，按各案例说明判断完成范围。
        </Note>
      </Page>
    );
  }

  function CodeArtsDebug() {
    return (
      <Page
        label="华为云码道 / 排错与验证"
        className="expanded-page codearts-evidence debug-proof-page"
      >
        <Heading
          eyebrow="CODEARTS / 排错与验证"
          title={
            <>
              记录问题怎样定位
              <br />
              也记录验证到哪一步
            </>
          }
        >
          过程材料包含编译失败、重新构建、触摸命中分析和 OCR
          空结果处理。构建、逻辑断言、设备操作与模型可见回复分别注明来源。
        </Heading>
        <div className="debug-proof-pair">
          <Figure
            src="codearts/debug-hit-test.jpg"
            alt="码道分析透明命中导致拖动触摸进入下层画布，并说明拦截方式"
            caption="CA-07 局部 · 工具栏拖动误写的根因和修复"
          />
          <Figure
            src="codearts/ocr-boundary.jpg"
            alt="码道记录移除固定结果、改用真实OCR和明确空结果反馈"
            caption="CA-06 局部 · OCR 来源与可用性边界"
          />
        </div>
        <div className="debug-records">
          <div>
            <b>D-01</b>
            <h3>编译失败后迭代构建</h3>
            <p>
              日志中先出现 ArkTSCompilerError／BUILD FAILED，随后记录 BUILD
              SUCCESSFUL，73 tasks。保留失败输出与后续构建结果供核对。
            </p>
          </div>
          <div>
            <b>D-02</b>
            <h3>触摸事件与布局命中排查</h3>
            <p>
              透明命中容器会将拖动送入下层画布。截图说明按工具栏实际屏幕范围拦截
              Down，并一致处理后续 Move／Up。
            </p>
          </div>
          <div>
            <b>D-03</b>
            <h3>识别空结果的可解释反馈</h3>
            <p>
              模拟器缺少 OCR
              引擎，设备手写也有空文本情况。改为提示能力状态或墨迹统计，相关逻辑输出
              25 passed, 0 failed。
            </p>
          </div>
        </div>
        <div className="verification-boundary">
          <h3>已补入的运行材料</h3>
          <p>
            HM-01 至 HM-03 可观察书写、工具切换、折叠、化学结构和 PDF
            批注。码道截图报告 MatePad／HarmonyOS 7.0.0.111
            安装与测试，本次按用户材料整理，未重新连接设备。
          </p>
        </div>
        <Note>
          完整证明文件夹包含逐案 Markdown、JSONL
          摘录、截图原图整理副本和索引。OCR
          识别精度、多窗／分屏覆盖、设备延迟及帧率仍需专项验收。
        </Note>
      </Page>
    );
  }

  function CodeArtsCollaboration() {
    return (
      <Page
        label="华为云码道 / 契约与网关协作"
        className="expanded-page member-proof-page"
      >
        <Heading
          eyebrow="CODEARTS / 团队补充案例"
          title={
            <>
              从协议校验
              <br />
              到网关与脚本排错
            </>
          }
        >
          艾尼萨尔补充的材料覆盖双端数据契约与 AI 网关，提供 5
          张码道截图和两份开发文档，将函数解释、代码生成及错误定位串成可核对的过程。
        </Heading>
        <Figure
          src="codearts/CA-S02-detail.png"
          alt="码道生成pageIndex连续性校验并接入主流程的可见回复"
          caption="CA-S02 局部 · 校验函数接入主流程，回复列出边界输入及样本回归"
          className="member-generation"
        />
        <div className="member-proof-grid">
          <div>
            <Figure
              src="codearts/CA-S03-detail.png"
              alt="码道检查poly参数与渲染层线宽的排错回复"
              caption="CA-S03 · poly(width=...) 参数定位"
            />
            <h3>从调用关系确认修改位置</h3>
            <p>
              检查函数签名和同名覆盖，将几何采样与渲染线宽分开，识别被
              **_ignored 吞掉的样式参数。
            </p>
          </div>
          <div>
            <Figure
              src="codearts/CA-S04-detail.png"
              alt="码道分析bat中文乱码与执行错误的回复"
              caption="CA-S04 · 编码、代码页与换行符排查"
            />
            <h3>用运行反馈修正建议</h3>
            <p>
              对照脚本编码、代码页与换行，作者记录最终让 bat 只调用
              Python，将中文输出放到 Python 中。
            </p>
          </div>
        </div>
        <div className="verification-boundary">
          <h3>跨端契约与网关的过程记录</h3>
          <p>
            作者原文还包括坐标单位、JSON
            解析与重试、提示词注入处理、规范化内容指纹，以及
            call_upstream_stream
            的流式处理解释。完整截图、原文与整理说明保存在独立证明材料文件夹。
          </p>
        </div>
        <Note>
          本页依据成员提供的截图与文档，测试和节省时间数字保留为作者报告。本次未取得文档引用的提交对象，也未重新运行该组代码，当前应用的数据单位以实现为准。
        </Note>
      </Page>
    );
  }

  return {
    CodeArtsOverview,
    CodeArtsGeneration,
    CodeArtsDebug,
    CodeArtsCollaboration,
  };
}
