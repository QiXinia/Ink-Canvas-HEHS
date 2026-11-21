// @parity src/InkCanvas.Core/Ink/InkDocument.cs
// 线条擦：只切掉中心线被圆盘扫过的部分，返回新碎片（新 Id，保留宽度，不重算笔锋）。
import { InkPoint } from './InkPoint';
import { InkStroke } from './InkStroke';
import { InkColor } from './InkColor';
import { StrokeAttributes, TaperKind } from './StrokeAttributes';

export class EraseOutcome {
  Touched: boolean = false;
  Fragments: InkStroke[] = [];
}

function interpolate(a: InkPoint, b: InkPoint, t: number): InkPoint {
  return new InkPoint(
    a.X + (b.X - a.X) * t,
    a.Y + (b.Y - a.Y) * t,
    a.Pressure + (b.Pressure - a.Pressure) * t,
    a.Width + (b.Width - a.Width) * t,
    a.TimeMs + (b.TimeMs - a.TimeMs) * t
  );
}

function appendDistinct(list: InkPoint[], point: InkPoint): void {
  if (list.length > 0) {
    const last = list[list.length - 1];
    const dx = last.X - point.X;
    const dy = last.Y - point.Y;
    if (dx * dx + dy * dy <= 1e-4) {
      return;
    }
  }
  list.push(point);
}

function flush(current: InkPoint[], fragments: InkPoint[][]): void {
  if (current.length > 0) {
    fragments.push(current.splice(0, current.length));
  }
}

export function eraseDisc(stroke: InkStroke, centerX: number, centerY: number, radius: number): EraseOutcome {
  const outcome = new EraseOutcome();
  if (stroke.Count === 0 || radius <= 0) {
    return outcome;
  }

  if (stroke.Count === 1) {
    const point = stroke.Points[0];
    const dx = point.X - centerX;
    const dy = point.Y - centerY;
    if (Math.sqrt(dx * dx + dy * dy) <= radius + point.Width * 0.5) {
      outcome.Touched = true;
      return outcome;
    }
    outcome.Fragments.push(stroke);
    return outcome;
  }

  let maxHalfWidth = 0;
  for (let i = 0; i < stroke.Count; i++) {
    maxHalfWidth = Math.max(maxHalfWidth, stroke.Points[i].Width);
  }
  maxHalfWidth = maxHalfWidth * 0.5;
  const cutRadius = radius + maxHalfWidth;
  const cutRadiusSquared = cutRadius * cutRadius;

  let touched = false;
  const fragments: InkPoint[][] = [];
  let current: InkPoint[] = [];

  for (let i = 1; i < stroke.Count; i++) {
    const a = stroke.Points[i - 1];
    const b = stroke.Points[i];
    const p0x = a.X;
    const p0y = a.Y;
    const dx = b.X - p0x;
    const dy = b.Y - p0y;
    const fx = p0x - centerX;
    const fy = p0y - centerY;
    const aa = dx * dx + dy * dy;
    const cuts: number[] = [0, 1];

    if (aa > 1e-8) {
      const bb = 2 * (fx * dx + fy * dy);
      const cc = fx * fx + fy * fy - cutRadiusSquared;
      const disc = bb * bb - 4 * aa * cc;
      if (disc >= 0) {
        const root = Math.sqrt(disc);
        const t1 = (-bb - root) / (2 * aa);
        const t2 = (-bb + root) / (2 * aa);
        if (t1 > 0 && t1 < 1) {
          cuts.push(t1);
        }
        if (t2 > 0 && t2 < 1) {
          cuts.push(t2);
        }
      }
    }
    cuts.sort((x: number, y: number): number => x - y);

    for (let k = 1; k < cuts.length; k++) {
      const t0 = cuts[k - 1];
      const t1 = cuts[k];
      const tm = (t0 + t1) / 2;
      const mx = p0x + dx * tm;
      const my = p0y + dy * tm;
      const ddx = mx - centerX;
      const ddy = my - centerY;
      if (ddx * ddx + ddy * ddy <= cutRadiusSquared) {
        touched = true;
        flush(current, fragments);
        continue;
      }
      appendDistinct(current, interpolate(a, b, t0));
      appendDistinct(current, interpolate(a, b, t1));
    }
  }

  flush(current, fragments);
  if (!touched) {
    outcome.Fragments.push(stroke);
    return outcome;
  }

  for (let i = 0; i < fragments.length; i++) {
    const fragment = fragments[i];
    if (fragment.length === 0) {
      continue;
    }
    const copy = new InkStroke(stroke.Attributes.clone());
    copy.IsRecognizedShape = stroke.IsRecognizedShape;
    copy.restorePoints(fragment);
    copy.complete();
    outcome.Fragments.push(copy);
  }
  outcome.Touched = true;
  return outcome;
}

export interface EraserPointInput {
  X: number;
  Y: number;
  Pressure: number;
  Width: number;
  TimeMs: number;
}

export interface EraserStrokeInput {
  ColorPacked: number;
  BaseWidth: number;
  IsHighlighter: boolean;
  HighlighterAlpha: number;
  TaperKind: number;
  IsRecognizedShape: boolean;
  Points: EraserPointInput[];
}

export interface EraserFragmentSummary {
  ColorPacked: number;
  BaseWidth: number;
  IsHighlighter: boolean;
  HighlighterAlpha: number;
  TaperKind: number;
  IsRecognizedShape: boolean;
  Points: EraserPointInput[];
}

export interface EraserSummary {
  touched: boolean;
  fragments: EraserFragmentSummary[];
}

export function eraseDiscSummary(
  input: EraserStrokeInput,
  centerX: number,
  centerY: number,
  radius: number
): EraserSummary {
  const attributes = new StrokeAttributes();
  attributes.Color = InkColor.fromPackedArgb(input.ColorPacked);
  attributes.BaseWidth = input.BaseWidth;
  attributes.IsHighlighter = input.IsHighlighter;
  attributes.HighlighterAlpha = input.HighlighterAlpha;
  attributes.TaperKind = input.TaperKind as TaperKind;
  const stroke = new InkStroke(attributes);
  stroke.IsRecognizedShape = input.IsRecognizedShape;
  const points: InkPoint[] = [];
  for (let i = 0; i < input.Points.length; i++) {
    const p = input.Points[i];
    points.push(new InkPoint(p.X, p.Y, p.Pressure, p.Width, p.TimeMs));
  }
  stroke.restorePoints(points);

  const outcome = eraseDisc(stroke, centerX, centerY, radius);
  const fragments: EraserFragmentSummary[] = [];
  for (let i = 0; i < outcome.Fragments.length; i++) {
    const fragment = outcome.Fragments[i];
    const fragmentPoints: EraserPointInput[] = [];
    for (let j = 0; j < fragment.Count; j++) {
      const p = fragment.Points[j];
      fragmentPoints.push({ X: p.X, Y: p.Y, Pressure: p.Pressure, Width: p.Width, TimeMs: p.TimeMs });
    }
    fragments.push({
      ColorPacked: fragment.Attributes.Color.packedArgb(),
      BaseWidth: fragment.Attributes.BaseWidth,
      IsHighlighter: fragment.Attributes.IsHighlighter,
      HighlighterAlpha: fragment.Attributes.HighlighterAlpha,
      TaperKind: fragment.Attributes.TaperKind,
      IsRecognizedShape: fragment.IsRecognizedShape,
      Points: fragmentPoints
    });
  }
  return { touched: outcome.Touched, fragments };
}
