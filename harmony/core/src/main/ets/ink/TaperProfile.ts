// @parity src/InkCanvas.Core/Ink/TaperProfile.cs（含 SimulatedPressureTaper.cs 新增笔锋）
// 因果增量笔锋：只读写 tipIndex 及之前的点；宽度只增不减；抬笔不重算。
// 基线演进同步（2026-11-19）：类化 TaperProfile（有状态实例，InkStroke 持有）；
// 新增 SmoothPen/SoftPen/Natural/Calligraphy（SimulatedPressureTaper：时间戳虚拟压感 +
// 距离三次笔尾）与 StablePointCount（前缀已冻结点数，供撤销/回放前缀复用）。
import { InkPoint } from './InkPoint';
import { TaperKind } from './StrokeAttributes';

export interface TaperOptionsInput {
  TailPoints?: number;
  HeadPoints?: number;
  TipRatio?: number;
}

// 真实手写笔压感到宽度的调制因子（鸿蒙 M-Pencil 平台增强，C# 端暂无对应能力）。
// 设计约束：p=0.5（常规按压力度/中立点）必须返回 1.0，使硬件路径在中立按压下与速度笔锋
// 完全重合；轻压收细、重压加粗，硬截断防止极端采样产生毛刺。仅在 InkPoint.HasPressure
// 为 true 时由 settlePoint 施加，黄金向量与手指/鼠标路径逐位不变。
export function pressureWidthFactor(pressure: number): number {
  return clamp(0.5 + pressure, 0.35, 1.4);
}

export function settlePoint(points: InkPoint[], index: number, candidate: number): void {
  const target = points[index].HasPressure
    ? candidate * pressureWidthFactor(points[index].Pressure)
    : candidate;
  if (target > points[index].Width) {
    points[index].Width = target;
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

function distance(a: InkPoint, b: InkPoint): number {
  const dx = b.X - a.X;
  const dy = b.Y - a.Y;
  return Math.sqrt(dx * dx + dy * dy);
}

function smoothStep(value: number): number {
  return value * value * (3 - 2 * value);
}

// 设备真实压感因子：p=0.5（无压感设备/手指）为中性 1.0，因此上游黄金向量不变。
// 0 -> 0.45x，0.5 -> 1.0x，1 -> 1.35x。
function devicePressureScale(pressure: number): number {
  if (pressure <= 0.5) {
    return 0.45 + 0.55 * clamp(pressure / 0.5, 0, 1);
  }
  return 1 + 0.35 * clamp((pressure - 0.5) / 0.5, 0, 1);
}

export abstract class TaperProfile {
  abstract get Kind(): TaperKind;

  // points：已采样全部点（宽度就地更新）；tipIndex：刚到达的点；baseWidth：全宽。
  abstract Apply(points: InkPoint[], tipIndex: number, baseWidth: number): void;

  // 前缀中宽度不会再随后续采样变化的最长点数。
  StablePointCount(count: number): number {
    return count;
  }
}

export class NoneTaperProfile extends TaperProfile {
  get Kind(): TaperKind {
    return TaperKind.None;
  }

  Apply(points: InkPoint[], tipIndex: number, baseWidth: number): void {
    settlePoint(points, tipIndex, baseWidth);
  }
}

export class VelocityTaperProfile extends TaperProfile {
  get Kind(): TaperKind {
    return TaperKind.Velocity;
  }

  Apply(points: InkPoint[], tipIndex: number, baseWidth: number): void {
    let pressure = 0.5;
    if (tipIndex >= 2) {
      const d1 = distance(points[tipIndex - 2], points[tipIndex - 1]);
      const d2 = distance(points[tipIndex - 1], points[tipIndex]);
      const speed = (d1 + d2) / 20.0;
      if (speed >= 0.25) {
        pressure = 0.5 - 0.3 * (Math.min(speed, 1.5) - 0.3) / 1.2;
      } else if (speed < 0.05) {
        pressure = 0.5 + 0.4 * (0.05 - speed) / 0.05;
      }
    }
    const ratio = clamp(pressure / 0.5, 0.15, 1.8);
    settlePoint(points, tipIndex, baseWidth * ratio);
  }
}

export class TailTaperProfile extends TaperProfile {
  readonly tailPoints: number;
  readonly headPoints: number;
  readonly tipRatio: number;

  constructor(options?: TaperOptionsInput) {
    super();
    this.tailPoints = options !== undefined && options.TailPoints !== undefined ? options.TailPoints : 10;
    this.headPoints = options !== undefined && options.HeadPoints !== undefined ? options.HeadPoints : 4;
    this.tipRatio = options !== undefined && options.TipRatio !== undefined ? options.TipRatio : 0.2;
  }

  get Kind(): TaperKind {
    return TaperKind.Tail;
  }

  StablePointCount(count: number): number {
    return Math.max(0, count - Math.max(2, this.tailPoints));
  }

  Apply(points: InkPoint[], tipIndex: number, baseWidth: number): void {
    const n = Math.max(2, this.tailPoints);
    const start = Math.max(0, tipIndex - n + 1);
    for (let j = start; j <= tipIndex; j++) {
      const d = tipIndex - j;
      const t = d / (n - 1);
      const ratio = this.tipRatio + (1 - this.tipRatio) * t;
      settlePoint(points, j, baseWidth * ratio);
    }

    if (tipIndex < this.headPoints) {
      const t = (tipIndex + 1) / (this.headPoints + 1);
      const ratio = this.tipRatio + (1 - this.tipRatio) * t;
      settlePoint(points, tipIndex, baseWidth * ratio);
    }
  }
}

// 时间戳虚拟压感 + 有界距离三次笔尾（C# SimulatedPressureTaper 1:1 移植）。
// 有状态：宽度环缓冲 64、累计距离、EMA 速度、ratio 一阶响应、稳定前缀游标。
export class SimulatedPressureTaperProfile extends TaperProfile {
  private readonly kindValue: TaperKind;
  private readonly widths: number[] = [];
  private readonly distances: number[] = [];
  private distanceAcc: number = 0;
  private headDistance: number = 0;
  private speed: number = 0;
  private ratio: number = 0;
  private hasSpeed: boolean = false;
  private stable: number = 0;

  constructor(kind: TaperKind) {
    super();
    this.kindValue = kind;
    for (let i = 0; i < 64; i++) {
      this.widths.push(0);
      this.distances.push(0);
    }
  }

  get Kind(): TaperKind {
    return this.kindValue;
  }

  StablePointCount(count: number): number {
    return this.kindValue === TaperKind.Natural || this.kindValue === TaperKind.Calligraphy
      ? this.stable
      : count;
  }

  Apply(points: InkPoint[], tipIndex: number, baseWidth: number): void {
    const current = points[tipIndex];
    let elapsed = 0;
    let stepDistance = 0;
    if (tipIndex > 0) {
      stepDistance = distance(current, points[tipIndex - 1]);
      elapsed = current.TimeMs - points[tipIndex - 1].TimeMs;
      this.distanceAcc += stepDistance;
    }

    if (elapsed > 0 && elapsed <= 200 && Number.isFinite(elapsed)) {
      const speed = stepDistance / elapsed;
      const weight = 1 - Math.exp(-elapsed / 35);
      this.speed = this.hasSpeed ? this.speed + weight * (speed - this.speed) : speed;
      this.hasSpeed = true;
    }

    const speedUnit = this.hasSpeed
      ? clamp((this.speed - 0.03) / (1.8 + baseWidth * 0.04), 0, 1)
      : 0.35;
    const pressure = 1 - smoothStep(speedUnit);
    let ratio: number;
    if (this.kindValue === TaperKind.SmoothPen) {
      ratio = 0.82 + 0.18 * pressure;
    } else if (this.kindValue === TaperKind.SoftPen) {
      ratio = 0.42 + 0.66 * Math.pow(pressure, 0.85);
    } else if (this.kindValue === TaperKind.Calligraphy) {
      ratio = 0.24 + 1.05 * Math.pow(pressure, 1.25);
    } else {
      ratio = 0.38 + 0.72 * pressure;
    }
    if (this.kindValue === TaperKind.Calligraphy && tipIndex > 1) {
      const ax = points[tipIndex - 1].X - points[tipIndex - 2].X;
      const ay = points[tipIndex - 1].Y - points[tipIndex - 2].Y;
      const bx = current.X - points[tipIndex - 1].X;
      const by = current.Y - points[tipIndex - 1].Y;
      const aLenSq = ax * ax + ay * ay;
      const bLenSq = bx * bx + by * by;
      if (aLenSq > 0.01 && bLenSq > 0.01) {
        const aLen = Math.sqrt(aLenSq);
        const bLen = Math.sqrt(bLenSq);
        const dot = (ax / aLen * bx / bLen) + (ay / aLen * by / bLen);
        const turn = (1 - dot) * 0.5;
        ratio *= 1 + 0.18 * turn;
      }
    }
    const response = elapsed > 0 && elapsed <= 200 ? 1 - Math.exp(-elapsed / 22) : 1;
    this.ratio = tipIndex === 0 ? ratio : this.ratio + response * (ratio - this.ratio);

    if (this.kindValue === TaperKind.SmoothPen || this.kindValue === TaperKind.SoftPen) {
      settlePoint(points, tipIndex, baseWidth * this.ratio * devicePressureScale(current.Pressure));
      return;
    }

    const tipRatio = this.kindValue === TaperKind.Calligraphy ? 0.09 : 0.18;
    const headLength = Math.max(5, baseWidth * 1.6);
    // 起笔接触内的停顿/抖动不得抬高笔头：按到首点的实际距离增长。
    if (this.headDistance < headLength) {
      this.headDistance = Math.max(this.headDistance, distance(current, points[0]));
    }
    const head = tipRatio + (1 - tipRatio) * smoothStep(clamp(this.headDistance / headLength, 0, 1));
    const slot = tipIndex % this.widths.length;
    this.widths[slot] = baseWidth * this.ratio * head;
    this.distances[slot] = this.distanceAcc;
    const tailLength = Math.max(10, baseWidth * (this.kindValue === TaperKind.Calligraphy ? 4 : 2.5));
    const first = Math.max(this.stable, tipIndex - this.widths.length + 1);
    for (let index = first; index <= tipIndex; index++) {
      const progress = clamp((this.distanceAcc - this.distances[index % this.distances.length]) / tailLength, 0, 1);
      const cubic = 1 - Math.pow(1 - progress, 3);
      settlePoint(points, index, this.widths[index % this.widths.length] * (tipRatio + (1 - tipRatio) * cubic) * devicePressureScale(points[index].Pressure));
      if (progress >= 1) {
        this.stable = index + 1;
      }
    }
    this.stable = Math.max(this.stable, tipIndex - this.widths.length + 1);
  }
}

export function createTaperProfile(kind: TaperKind, options?: TaperOptionsInput): TaperProfile {
  if (kind === TaperKind.None) {
    return new NoneTaperProfile();
  }
  if (kind === TaperKind.Velocity) {
    return new VelocityTaperProfile();
  }
  if (kind === TaperKind.SmoothPen || kind === TaperKind.SoftPen
    || kind === TaperKind.Natural || kind === TaperKind.Calligraphy) {
    return new SimulatedPressureTaperProfile(kind);
  }
  return new TailTaperProfile(options);
}

// 兼容入口：无状态 kind（None/Tail/Velocity）单点应用；4 种模拟压感笔锋有跨点状态，
// 必须持有 profile 实例（InkStroke/effectiveWidths），此处显式抛错防止静默错值。
export function applyTaperAt(
  points: InkPoint[],
  tipIndex: number,
  baseWidth: number,
  kind: TaperKind,
  options?: TaperOptionsInput
): void {
  if (kind === TaperKind.SmoothPen || kind === TaperKind.SoftPen
    || kind === TaperKind.Natural || kind === TaperKind.Calligraphy) {
    throw new Error('simulated-pressure taper requires a per-stroke TaperProfile instance');
  }
  createTaperProfile(kind, kind === TaperKind.Tail ? options : undefined).Apply(points, tipIndex, baseWidth);
}

// 向量入口：从零宽度开始，按 AddPoint 的时序用同一 profile 实例逐点 Apply，返回逐点全宽。
export function effectiveWidths(
  baseWidth: number,
  inputPoints: InkPoint[],
  kind: TaperKind,
  options?: TaperOptionsInput
): number[] {
  const points: InkPoint[] = [];
  for (let i = 0; i < inputPoints.length; i++) {
    const p = inputPoints[i];
    points.push(new InkPoint(p.X, p.Y, p.Pressure, 0, p.TimeMs));
  }
  const profile = createTaperProfile(kind, kind === TaperKind.Tail ? options : undefined);
  for (let tip = 0; tip < points.length; tip++) {
    profile.Apply(points, tip, baseWidth);
  }
  const widths: number[] = [];
  for (let i = 0; i < points.length; i++) {
    widths.push(points[i].Width);
  }
  return widths;
}