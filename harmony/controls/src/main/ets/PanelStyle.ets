// @parity src/InkCanvas.Controls/Themes/Tokens.xaml
// 面板卡片材质：API 26（supportsSystemMaterial）开启 backgroundBlurStyle + 更透的底色；
// 不支持时回退现有不透明/半透卡片（BlurStyle.NONE）。能力门控在 entry 侧探测后以布尔传入。

export function panelBackground(dark: boolean, blur: boolean): string {
  if (blur) {
    return dark ? '#CC202124' : '#CCF6F7F9';
  }
  return dark ? '#F21F1F1F' : '#F2F0F3F9';
}

export function panelBlurStyle(blur: boolean): BlurStyle {
  return blur ? BlurStyle.Thin : BlurStyle.NONE;
}