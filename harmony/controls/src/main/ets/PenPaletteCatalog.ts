// @parity src/InkCanvas.Core/Settings/OriginalMenuCatalog.cs
// 9 色笔板（深/浅两套）与 6 色荧光笔板；宽度 1–30（存储 0.5–15）、透明度 20–240/20。
import { toTaperKind } from '@inkcanvas/core';

export interface Swatch {
  Name: string;
  R: number;
  G: number;
  B: number;
}

export const PEN_COLORS_DARK: Swatch[] = [
  { Name: '黑色', R: 0, G: 0, B: 0 },
  { Name: '白色', R: 255, G: 255, B: 255 },
  { Name: '红色', R: 220, G: 38, B: 38 },
  { Name: '粉色', R: 147, G: 51, B: 234 },
  { Name: '橙色', R: 234, G: 88, B: 12 },
  { Name: '黄色', R: 234, G: 179, B: 8 },
  { Name: '绿色', R: 22, G: 163, B: 74 },
  { Name: '青色', R: 13, G: 148, B: 136 },
  { Name: '蓝色', R: 37, G: 99, B: 235 }
];

export const PEN_COLORS_LIGHT: Swatch[] = [
  { Name: '黑色', R: 0, G: 0, B: 0 },
  { Name: '白色', R: 255, G: 255, B: 255 },
  { Name: '红色', R: 239, G: 68, B: 68 },
  { Name: '粉色', R: 236, G: 72, B: 153 },
  { Name: '橙色', R: 249, G: 115, B: 22 },
  { Name: '黄色', R: 250, G: 204, B: 21 },
  { Name: '绿色', R: 34, G: 197, B: 94 },
  { Name: '青色', R: 20, G: 184, B: 166 },
  { Name: '蓝色', R: 59, G: 130, B: 246 }
];

export const HIGHLIGHTER_COLORS: Swatch[] = [
  { Name: '荧光笔红色', R: 220, G: 38, B: 38 },
  { Name: '荧光笔橙色', R: 234, G: 88, B: 12 },
  { Name: '荧光笔黄色', R: 234, G: 179, B: 8 },
  { Name: '荧光笔青色', R: 13, G: 148, B: 136 },
  { Name: '荧光笔蓝色', R: 37, G: 99, B: 235 },
  { Name: '荧光笔紫色', R: 147, G: 51, B: 234 }
];

export const PEN_WIDTH_MIN: number = 1;
export const PEN_WIDTH_MAX: number = 30;
export const PEN_WIDTH_STEP: number = 1;
export const HIGHLIGHTER_ALPHA_MIN: number = 20;
export const HIGHLIGHTER_ALPHA_MAX: number = 240;
export const HIGHLIGHTER_ALPHA_STEP: number = 20;
export const HIGHLIGHTER_ALPHA_DEFAULT: number = 140;
// 基线演进：7 种笔迹风格（含 4 种模拟压感笔锋）；映射统一走 core OriginalMenuCatalog。
export const TAPER_LABELS: string[] = [
  '点集笔锋', '速率笔锋', '关闭笔锋',
  '平滑硬笔', '柔和压感', '自然笔锋', '书法笔锋'
];
// 荧光笔独立宽度菜单上限（存储 60；null = 跟随笔宽）。
export const HIGHLIGHTER_WIDTH_MAX: number = 120;

export function toStoredPenWidth(menuWidth: number): number {
  return menuWidth / 2;
}

export function toMenuPenWidth(storedWidth: number): number {
  return storedWidth * 2;
}

export function snap(value: number, minimum: number, step: number): number {
  return minimum + Math.round((value - minimum) / step) * step;
}

// 菜单索引（inkStyle）→ TaperKind：委托 core OriginalMenuCatalog.toTaperKind（0..6）。
export function taperKindForIndex(index: number): number {
  return toTaperKind(index);
}

// The compact palette exposes styles 0/1/2/5; its fourth button is Natural, not SmoothPen.
export function quickTaperStyleForIndex(index: number): number {
  return index === 3 ? 5 : index;
}

export function packedSwatch(swatch: Swatch): number {
  return (((255 << 24) | (swatch.R << 16) | (swatch.G << 8) | swatch.B) >>> 0);
}

export interface PaletteSummary {
  PenCount: number;
  HighlighterCount: number;
  PenPacked: number[];
  HighlighterPacked: number[];
}

export function paletteSummary(light: boolean): PaletteSummary {
  const pen = light ? PEN_COLORS_LIGHT : PEN_COLORS_DARK;
  const penPacked: number[] = [];
  const highlighterPacked: number[] = [];
  for (let i = 0; i < pen.length; i++) {
    penPacked.push(packedSwatch(pen[i]));
  }
  for (let i = 0; i < HIGHLIGHTER_COLORS.length; i++) {
    highlighterPacked.push(packedSwatch(HIGHLIGHTER_COLORS[i]));
  }
  return {
    PenCount: pen.length,
    HighlighterCount: HIGHLIGHTER_COLORS.length,
    PenPacked: penPacked,
    HighlighterPacked: highlighterPacked
  };
}
