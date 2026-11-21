// @parity src/InkCanvas.App/Views/InfoWindow.cs（Markdown 子集渲染；渐进流式与公式为新增）
// 流式消息：Markdown 渐进解析 + LaTeX 子集转纯文本排版（不引入第三方 KaTeX），
// 未闭合公式按纯文本兜底；末尾闪烁光标仅在 streaming 时显示。
import {
  FormulaKind,
  MdBlock,
  MdBlockKind,
  FormulaSegment,
  parseMarkdownProgressive,
  splitFormulas
} from '@inkcanvas/core';
import { ThemeTokens, themeTokens } from './ThemeTokens';

@Component
export struct StreamMessage {
  @Prop dark: boolean = false;
  @Prop text: string = '';
  @Prop streaming: boolean = false;
  @State private cursorOn: boolean = true;
  private timer: number = -1;

  aboutToAppear(): void {
    this.timer = setInterval(() => {
      this.cursorOn = !this.cursorOn;
    }, 520);
  }

  aboutToDisappear(): void {
    if (this.timer >= 0) {
      clearInterval(this.timer);
      this.timer = -1;
    }
  }

  private tokens(): ThemeTokens {
    return themeTokens(this.dark);
  }

  build() {
    Column({ space: 6 }) {
      ForEach(parseMarkdownProgressive(this.text), (block: MdBlock, index: number) => {
        this.BlockView(block)
      }, (block: MdBlock, index: number) => String(index) + '-' + block.Kind)
      if (this.streaming) {
        Text('▍')
          .fontSize(14)
          .fontColor(this.tokens().accent)
          .opacity(this.cursorOn ? 1 : 0.15)
      }
    }
    .width('100%')
    .alignItems(HorizontalAlign.Start);
  }

  @Builder
  BlockView(block: MdBlock) {
    if (block.Kind === MdBlockKind.Heading) {
      Text(block.Text)
        .fontSize(block.Level <= 2 ? 16 : 14)
        .fontWeight(FontWeight.Medium)
        .fontColor(this.tokens().textPrimary)
        .width('100%')
    } else if (block.Kind === MdBlockKind.ListItem) {
      Row({ space: 6 }) {
        Text(block.Level > 0 ? String(block.Level) + '.' : '•')
          .fontSize(13)
          .fontColor(this.tokens().accent)
        this.InlineText(block.Text)
      }
      .width('100%')
      .alignItems(VerticalAlign.Top)
    } else if (block.Kind === MdBlockKind.Code) {
      Text(block.Text)
        .fontSize(12)
        .fontFamily('monospace')
        .fontColor(this.tokens().textPrimary)
        .width('100%')
        .padding(8)
        .backgroundColor(this.dark ? '#33000000' : '#0F000000')
        .borderRadius(6)
        .border({ width: 1, color: block.Complete ? this.tokens().stroke : this.tokens().accent })
    } else if (block.Kind === MdBlockKind.Table) {
      Column({ space: 2 }) {
        ForEach(block.Rows, (row: string[], rowIndex: number) => {
          Row({ space: 6 }) {
            ForEach(row, (cell: string, cellIndex: number) => {
              Text(cell)
                .fontSize(12)
                .fontColor(rowIndex === 0 ? this.tokens().textPrimary : this.tokens().textSecondary)
                .fontWeight(rowIndex === 0 ? FontWeight.Medium : FontWeight.Normal)
                .layoutWeight(1)
            }, (cell: string, cellIndex: number) => String(rowIndex) + '-' + String(cellIndex))
          }
          .width('100%')
          .padding({ top: 3, bottom: 3 })
        }, (row: string[], rowIndex: number) => String(rowIndex))
      }
      .width('100%')
      .padding(6)
      .border({ width: 1, color: this.tokens().stroke })
      .borderRadius(6)
    } else if (block.Kind === MdBlockKind.Quote) {
      Row() {
        Divider().vertical(true).height('100%').color(this.tokens().accent)
        this.InlineText(block.Text)
      }
      .width('100%')
      .padding({ left: 6 })
      .alignItems(VerticalAlign.Top)
    } else {
      this.InlineText(block.Text)
    }
  }

  // 行内/块级公式统一按 plain 显示；未闭合片段用强调色虚框提示“正在生成”。
  @Builder
  InlineText(text: string) {
    Flex({ wrap: FlexWrap.Wrap }) {
      ForEach(splitFormulas(text), (segment: FormulaSegment, index: number) => {
        if (segment.Kind === FormulaKind.Text) {
          Text(segment.Text)
            .fontSize(13)
            .fontColor(this.tokens().textPrimary)
            .lineHeight(20)
        } else {
          Text(segment.Text)
            .fontSize(13)
            .fontColor(this.tokens().accent)
            .lineHeight(20)
            .padding({ left: 2, right: 2 })
            .backgroundColor(segment.Complete ? (this.dark ? '#1A4CC2FF' : '#0F0067C0') : Color.Transparent)
            .border({ width: segment.Complete ? 0 : 1, color: this.tokens().accent })
        }
      }, (segment: FormulaSegment, index: number) => String(index) + '-' + segment.Kind)
    }
    .width('100%')

  }
}