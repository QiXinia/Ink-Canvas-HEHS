// @parity src/InkCanvas.App/Views/FloatingBarWindow.cs（浮窗吸附/翻转/跟随为鸿蒙侧新增）
// 助教浮窗定位：优先选区下方，空间不足翻到上方；水平优先右侧，不足翻左；
// 距视口边界不小于 SAFE_MARGIN，且永不出界（卡片大于视口时贴左上）。
import type { SelectionRect } from './AssistantTypes';

export interface ViewportSize {
  Width: number;
  Height: number;
}

export interface CardSize {
  Width: number;
  Height: number;
}

export interface CardPlacement {
  X: number;
  Y: number;
  Side: number;
  FlippedX: boolean;
  FlippedY: boolean;
}

export const SAFE_MARGIN: number = 12;
export const CARD_GAP: number = 12;

export enum CardSide {
  Below = 0,
  Above = 1,
  Right = 2,
  Left = 3
}

function clamp(value: number, min: number, max: number): number {
  if (max < min) {
    return min;
  }
  if (value < min) {
    return min;
  }
  if (value > max) {
    return max;
  }
  return value;
}

export function clampPlacement(x: number, y: number, card: CardSize, viewport: ViewportSize): number[] {
  const maxX = viewport.Width - card.Width - SAFE_MARGIN;
  const maxY = viewport.Height - card.Height - SAFE_MARGIN;
  return [clamp(x, SAFE_MARGIN, maxX), clamp(y, SAFE_MARGIN, maxY)];
}

// 需求：默认吸附在选区下方/右侧；下方不够高时翻上方。
// 水平：优先让卡片左缘与选区左缘对齐（下方布局），右侧空间不足时整体左移并钳制。
export function placeCard(
  anchor: SelectionRect,
  card: CardSize,
  viewport: ViewportSize,
  gap: number = CARD_GAP
): CardPlacement {
  const belowY = anchor.Bottom + gap;
  const aboveY = anchor.Top - card.Height - gap;
  let flippedY = false;
  let y = belowY;
  let side = CardSide.Below;
  if (belowY + card.Height + SAFE_MARGIN > viewport.Height) {
    if (aboveY >= SAFE_MARGIN) {
      y = aboveY;
      flippedY = true;
      side = CardSide.Above;
    }
  }

  const rightX = anchor.Right + gap;
  const leftX = anchor.Left - card.Width - gap;
  let x = anchor.Left;
  let flippedX = false;
  if (x + card.Width + SAFE_MARGIN > viewport.Width) {
    if (rightX + card.Width + SAFE_MARGIN <= viewport.Width) {
      x = rightX;
      flippedX = true;
      side = CardSide.Right;
    } else if (leftX >= SAFE_MARGIN) {
      x = leftX;
      flippedX = true;
      side = CardSide.Left;
    }
  }

  const placed = clampPlacement(x, y, card, viewport);
  return { X: placed[0], Y: placed[1], Side: side, FlippedX: flippedX, FlippedY: flippedY };
}

// 白板平移/缩放：浮窗以选区中心为锚点跟随。
// scale 为视图缩放；offset 为白板平移（视图坐标）。返回新的选区矩形。
export function followAnchor(anchor: SelectionRect, scale: number, offsetX: number, offsetY: number): SelectionRect {
  const safeScale = scale <= 0 ? 1 : scale;
  const cx = (anchor.Left + anchor.Right) * 0.5;
  const cy = (anchor.Top + anchor.Bottom) * 0.5;
  const halfWidth = (anchor.Right - anchor.Left) * 0.5 * safeScale;
  const halfHeight = (anchor.Bottom - anchor.Top) * 0.5 * safeScale;
  const ncx = cx * safeScale + offsetX;
  const ncy = cy * safeScale + offsetY;
  return {
    Left: ncx - halfWidth,
    Top: ncy - halfHeight,
    Right: ncx + halfWidth,
    Bottom: ncy + halfHeight
  };
}

// 指针拖拽浮窗后的位置：只在卡片整体仍与视口相交时接受，否则回钳。
export function dragPlacement(
  fromX: number,
  fromY: number,
  dx: number,
  dy: number,
  card: CardSize,
  viewport: ViewportSize
): number[] {
  return clampPlacement(fromX + dx, fromY + dy, card, viewport);
}

export function placementProbe(
  anchor: number[],
  cardWidth: number,
  cardHeight: number,
  viewportWidth: number,
  viewportHeight: number
): CardPlacement {
  const rect: SelectionRect = {
    Left: anchor[0],
    Top: anchor[1],
    Right: anchor[2],
    Bottom: anchor[3]
  };
  return placeCard(
    rect,
    { Width: cardWidth, Height: cardHeight },
    { Width: viewportWidth, Height: viewportHeight }
  );
}

export function followProbe(anchor: number[], scale: number, offsetX: number, offsetY: number): number[] {
  const rect: SelectionRect = {
    Left: anchor[0],
    Top: anchor[1],
    Right: anchor[2],
    Bottom: anchor[3]
  };
  const moved = followAnchor(rect, scale, offsetX, offsetY);
  return [moved.Left, moved.Top, moved.Right, moved.Bottom];
}