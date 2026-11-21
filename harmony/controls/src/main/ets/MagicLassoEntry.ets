// @parity src/InkCanvas.Controls/Controls/FloatingBar.cs（入口按钮样式；AI 入口为新增）
// 魔法套索入口：选择工具下浮在光标/选区附近的 pill 按钮，也可用 Q 触发。
import { ThemeTokens, themeTokens } from './ThemeTokens';

export const LASSO_ENTRY_WIDTH: number = 148;
export const LASSO_ENTRY_HEIGHT: number = 40;

@Component
export struct MagicLassoEntry {
  @Prop dark: boolean = false;
  @Prop entryEnabled: boolean = true;
  @Prop armed: boolean = false;
  @Prop reduceMotion: boolean = false;
  onTrigger: () => void = () => {
  };
  onArm: () => void = () => {
  };

  private tokens(): ThemeTokens {
    return themeTokens(this.dark);
  }

  private bg(): ResourceColor {
    if (!this.entryEnabled) {
      return this.dark ? '#66000000' : '#66FFFFFF';
    }
    if (this.armed) {
      return this.tokens().floatBarSelected;
    }
    return this.tokens().floatBarBackground;
  }

  build() {
    Row({ space: 6 }) {
      Text('✦')
        .fontSize(16)
        .fontColor(this.entryEnabled ? this.tokens().accent : this.tokens().textSecondary)
      Text(this.armed ? '手绘圈选' : 'AI 魔法识别')
        .fontSize(12)
        .fontColor(this.entryEnabled ? this.tokens().textPrimary : this.tokens().textSecondary)
        .maxLines(1)
      Text('Q')
        .fontSize(10)
        .fontColor(this.tokens().textSecondary)
        .padding({ left: 4, right: 4, top: 1, bottom: 1 })
        .borderRadius(3)
        .border({ width: 1, color: this.tokens().stroke })
    }
    .height(LASSO_ENTRY_HEIGHT)
    .padding({ left: 10, right: 10 })
    .backgroundColor(this.bg())
    .backgroundBlurStyle(this.armed ? BlurStyle.Thick : BlurStyle.Thin)
    .borderRadius(LASSO_ENTRY_HEIGHT / 2)
    .border({ width: 1, color: this.tokens().floatBarBorder })
    .shadow({ radius: 12, color: '#26000000', offsetY: 4 })
    .opacity(this.entryEnabled ? 1 : 0.55)
    .onClick(() => {
      if (!this.entryEnabled) {
        return;
      }
      this.onTrigger();
    })
    .gesture(
      LongPressGesture({ repeat: false })
        .onAction(() => {
          if (this.entryEnabled) {
            this.onArm();
          }
        })
    )
    .accessibilityText(this.armed ? 'AI 魔法识别已就绪，请手绘圈选' : 'AI 魔法识别')
    .accessibilityLevel('auto');
  }
}