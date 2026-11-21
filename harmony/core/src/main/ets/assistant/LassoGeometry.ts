// @parity src/InkCanvas.Core/Selection/SelectionGeometry.cs（套索闭合并集与命中为鸿蒙侧新增）
// 自由曲线套索：闭合判定、多边形点包含、笔画命中比例、选区有效性。
// 复用 SelectionGeometry 的矩形框选语义，不改动既有矩形选择路径；这里只提供只读几何。
import type { SelectionRect } from './AssistantTypes';

export interface LassoPoint {
  X: number;
  Y: number;
}

export interface LassoSummary {
  Closed: boolean;
  Area: number;
  Usable: boolean;
  HitCount: number;
  Bounds: SelectionRect;
}

export const MIN_SELECTION_SIZE: number = 32;

export function emptyRect(): SelectionRect {
  return { Left: 0, Top: 0, Right: 0, Bottom: 0 };
}

export function isEmptyRect(rect: SelectionRect): boolean {
  return rect.Right <= rect.Left || rect.Bottom <= rect.Top;
}

export function rectWidth(rect: SelectionRect): number {
  return Math.max(0, rect.Right - rect.Left);
}

export function rectHeight(rect: SelectionRect): number {
  return Math.max(0, rect.Bottom - rect.Top);
}

export function rectCenterX(rect: SelectionRect): number {
  return (rect.Left + rect.Right) * 0.5;
}

export function rectCenterY(rect: SelectionRect): number {
  return (rect.Top + rect.Bottom) * 0.5;
}

export function lassoBounds(points: LassoPoint[]): SelectionRect {
  if (points.length === 0) {
    return emptyRect();
  }
  let left = points[0].X;
  let right = points[0].X;
  let top = points[0].Y;
  let bottom = points[0].Y;
  for (let i = 1; i < points.length; i++) {
    const p = points[i];
    if (p.X < left) {
      left = p.X;
    }
    if (p.X > right) {
      right = p.X;
    }
    if (p.Y < top) {
      top = p.Y;
    }
    if (p.Y > bottom) {
      bottom = p.Y;
    }
  }
  return { Left: left, Top: top, Right: right, Bottom: bottom };
}

export function lassoPerimeter(points: LassoPoint[]): number {
  if (points.length < 2) {
    return 0;
  }
  let total = 0;
  for (let i = 1; i < points.length; i++) {
    const dx = points[i].X - points[i - 1].X;
    const dy = points[i].Y - points[i - 1].Y;
    total += Math.sqrt(dx * dx + dy * dy);
  }
  return total;
}

// 闭合判定：首尾距离不超过容差，或相对周长足够小（手绘套索常见有缺口）。
export function isClosedPath(points: LassoPoint[], tolerance: number = 8): boolean {
  if (points.length < 4) {
    return false;
  }
  const first = points[0];
  const last = points[points.length - 1];
  const dx = last.X - first.X;
  const dy = last.Y - first.Y;
  const gap = Math.sqrt(dx * dx + dy * dy);
  if (gap <= tolerance) {
    return true;
  }
  const perimeter = lassoPerimeter(points);
  return perimeter > 0 && gap / perimeter <= 0.03;
}

// 首尾不重合时补回首点，供点包含测试使用。
export function closePath(points: LassoPoint[], tolerance: number = 8): LassoPoint[] {
  const result: LassoPoint[] = [];
  for (let i = 0; i < points.length; i++) {
    result.push({ X: points[i].X, Y: points[i].Y });
  }
  if (result.length === 0) {
    return result;
  }
  const first = result[0];
  const last = result[result.length - 1];
  const dx = last.X - first.X;
  const dy = last.Y - first.Y;
  if (Math.sqrt(dx * dx + dy * dy) > tolerance) {
    result.push({ X: first.X, Y: first.Y });
  }
  return result;
}

export function polygonArea(points: LassoPoint[]): number {
  if (points.length < 3) {
    return 0;
  }
  let sum = 0;
  for (let i = 0; i < points.length; i++) {
    const a = points[i];
    const b = points[(i + 1) % points.length];
    sum += a.X * b.Y - b.X * a.Y;
  }
  return Math.abs(sum) * 0.5;
}

// 射线法；边界点按命中处理（先做距离容差，再判断跨越）。
export function pointInPolygon(points: LassoPoint[], x: number, y: number): boolean {
  if (points.length < 3) {
    return false;
  }
  let inside = false;
  let j = points.length - 1;
  for (let i = 0; i < points.length; i++) {
    const pi = points[i];
    const pj = points[j];
    const d = (pi.X - x) * (pj.Y - y) - (pj.X - x) * (pi.Y - y);
    if (Math.abs(d) < 1e-6) {
      const minX = Math.min(pi.X, pj.X);
      const maxX = Math.max(pi.X, pj.X);
      const minY = Math.min(pi.Y, pj.Y);
      const maxY = Math.max(pi.Y, pj.Y);
      if (x >= minX && x <= maxX && y >= minY && y <= maxY) {
        return true;
      }
    }
    if ((pi.Y > y) !== (pj.Y > y)) {
      const crossX = (pj.X - pi.X) * (y - pi.Y) / (pj.Y - pi.Y) + pi.X;
      if (x < crossX) {
        inside = !inside;
      }
    }
    j = i;
  }
  return inside;
}

// 笔画落在套索内的采样点比例；用于判定整笔是否被框住。
export function strokeCoverageRatio(stroke: LassoPoint[], polygon: LassoPoint[]): number {
  if (stroke.length === 0 || polygon.length < 3) {
    return 0;
  }
  let inside = 0;
  for (let i = 0; i < stroke.length; i++) {
    if (pointInPolygon(polygon, stroke[i].X, stroke[i].Y)) {
      inside++;
    }
  }
  return inside / stroke.length;
}

export function strokeHitByLasso(stroke: LassoPoint[], polygon: LassoPoint[], ratio: number = 0.6): boolean {
  if (stroke.length === 0) {
    return false;
  }
  if (strokeCoverageRatio(stroke, polygon) >= ratio) {
    return true;
  }
  // 起笔在框内但主体在外（或反之）时，用中点兜底，避免细小笔画被完全漏掉。
  const mid = stroke[Math.floor(stroke.length / 2)];
  return pointInPolygon(polygon, mid.X, mid.Y);
}

export function usableSelection(rect: SelectionRect, minSize: number = MIN_SELECTION_SIZE): boolean {
  return rectWidth(rect) >= minSize && rectHeight(rect) >= minSize;
}

export function lassoProbe(path: number[][], strokes: number[][][]): LassoSummary {
  const points: LassoPoint[] = [];
  for (let i = 0; i < path.length; i++) {
    points.push({ X: path[i][0], Y: path[i][1] });
  }
  const closed = isClosedPath(points);
  const polygon = closePath(points);
  const bounds = lassoBounds(points);
  let hits = 0;
  for (let s = 0; s < strokes.length; s++) {
    const strokePoints: LassoPoint[] = [];
    for (let i = 0; i < strokes[s].length; i++) {
      strokePoints.push({ X: strokes[s][i][0], Y: strokes[s][i][1] });
    }
    if (strokeHitByLasso(strokePoints, polygon)) {
      hits++;
    }
  }
  return {
    Closed: closed,
    Area: polygonArea(polygon),
    Usable: closed && usableSelection(bounds),
    HitCount: hits,
    Bounds: bounds
  };
}