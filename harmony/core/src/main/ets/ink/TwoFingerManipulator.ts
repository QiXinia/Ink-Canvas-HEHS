// @parity src/InkCanvas.Ink/Manipulation/TwoFingerManipulator.cs
import {
  Matrix3x2,
  identityMatrix,
  matrixEquals,
  multiplyMatrix,
  matrixFromArray,
  matrixToArray,
  createTranslationMatrix,
  createScaleMatrix,
  createRotationMatrix
} from '../math/Matrix3x2';

export interface TwoFingerOptionsInput {
  allowZoom: boolean;
  allowTranslate: boolean;
  allowRotation: boolean;
  minScale: number;
  maxScale: number;
  zoomDamping?: number;
  zoomDeadZone?: number;
}

export interface TwoFingerSample {
  AX: number;
  AY: number;
  BX: number;
  BY: number;
}

export interface TwoFingerUpdate {
  changed: boolean;
  transform: Matrix3x2;
}

export interface TwoFingerSimResult {
  changed: boolean[];
  final: number[];
}

export class TwoFingerManipulator {
  AllowZoom: boolean = true;
  AllowTranslate: boolean = true;
  AllowRotation: boolean = false;
  MinScale: number = 0.2;
  MaxScale: number = 8;

  /** 对数缩放增量的比例；1 = 旧行为，往返手势和不同采样率不产生额外缩放漂移。 */
  ZoomDamping: number = 1;

  /** 两指间距死区（DIP），低于此值不缩放/不旋转。 */
  ZoomDeadZone: number = 1;

  private prevAX: number = 0;
  private prevAY: number = 0;
  private prevBX: number = 0;
  private prevBY: number = 0;
  private hasPrevious: boolean = false;

  begin(ax: number, ay: number, bx: number, by: number): void {
    this.prevAX = ax;
    this.prevAY = ay;
    this.prevBX = bx;
    this.prevBY = by;
    this.hasPrevious = true;
  }

  reset(): void {
    this.hasPrevious = false;
  }

  /**
   * 与 C# Update(a, b, ref transform, center, screenSpace = false) 对齐：
   * screenSpace=true 时 delta 右乘（transform * delta，视图手势用屏幕坐标），
   * 否则左乘（delta * transform，世界坐标）。钳制按当前累计缩放
   * sqrt(|det|) 归一：scale ∈ [MinScale/currentScale, MaxScale/currentScale]。
   */
  update(ax: number, ay: number, bx: number, by: number, transform: Matrix3x2, centerX: number, centerY: number, screenSpace: boolean = false): TwoFingerUpdate {
    if (!this.hasPrevious) {
      this.begin(ax, ay, bx, by);
      return { changed: false, transform };
    }

    const prevMidX = (this.prevAX + this.prevBX) * 0.5;
    const prevMidY = (this.prevAY + this.prevBY) * 0.5;
    const midX = (ax + bx) * 0.5;
    const midY = (ay + by) * 0.5;
    const prevVX = this.prevBX - this.prevAX;
    const prevVY = this.prevBY - this.prevAY;
    const vx = bx - ax;
    const vy = by - ay;
    const prevLen = Math.sqrt(prevVX * prevVX + prevVY * prevVY);
    const len = Math.sqrt(vx * vx + vy * vy);

    let delta = identityMatrix();

    if (this.AllowTranslate) {
      delta = multiplyMatrix(delta, createTranslationMatrix(midX - prevMidX, midY - prevMidY));
    }

    if (this.AllowZoom && prevLen > this.ZoomDeadZone && len > this.ZoomDeadZone) {
      let scale = len / prevLen;
      scale = Math.pow(scale, Math.min(Math.max(this.ZoomDamping, 0.05), 1));
      const determinant = transform.M11 * transform.M22 - transform.M12 * transform.M21;
      const currentScale = Math.sqrt(Math.abs(determinant));
      const denominator = Math.max(currentScale, 1e-3);
      scale = Math.min(Math.max(scale, this.MinScale / denominator), this.MaxScale / denominator);
      const around = multiplyMatrix(
        multiplyMatrix(createTranslationMatrix(-centerX, -centerY), createScaleMatrix(scale, scale)),
        createTranslationMatrix(centerX, centerY)
      );
      delta = multiplyMatrix(delta, around);
    }

    if (this.AllowRotation && prevLen > this.ZoomDeadZone && len > this.ZoomDeadZone) {
      let angle = Math.atan2(vy, vx) - Math.atan2(prevVY, prevVX);
      while (angle > Math.PI) {
        angle -= 2 * Math.PI;
      }
      while (angle < -Math.PI) {
        angle += 2 * Math.PI;
      }
      const around = multiplyMatrix(
        multiplyMatrix(createTranslationMatrix(-centerX, -centerY), createRotationMatrix(angle)),
        createTranslationMatrix(centerX, centerY)
      );
      delta = multiplyMatrix(delta, around);
    }

    this.prevAX = ax;
    this.prevAY = ay;
    this.prevBX = bx;
    this.prevBY = by;

    if (matrixEquals(delta, identityMatrix())) {
      return { changed: false, transform };
    }
    return {
      changed: true,
      transform: screenSpace ? multiplyMatrix(transform, delta) : multiplyMatrix(delta, transform)
    };
  }
}

export function simulateTwoFinger(
  options: TwoFingerOptionsInput,
  initial: number[],
  centerX: number,
  centerY: number,
  samples: TwoFingerSample[]
): TwoFingerSimResult {
  const manipulator = new TwoFingerManipulator();
  manipulator.AllowZoom = options.allowZoom;
  manipulator.AllowTranslate = options.allowTranslate;
  manipulator.AllowRotation = options.allowRotation;
  manipulator.MinScale = options.minScale;
  manipulator.MaxScale = options.maxScale;
  manipulator.ZoomDamping = options.zoomDamping !== undefined ? options.zoomDamping : 1;
  manipulator.ZoomDeadZone = options.zoomDeadZone !== undefined ? options.zoomDeadZone : 1;

  let transform = matrixFromArray(initial);
  const changed: boolean[] = [];
  for (let i = 0; i < samples.length; i++) {
    const s = samples[i];
    const result = manipulator.update(s.AX, s.AY, s.BX, s.BY, transform, centerX, centerY);
    changed.push(result.changed);
    transform = result.transform;
  }
  return { changed, final: matrixToArray(transform) };
}
