// @parity src/InkCanvas.Ink/Rendering/InkSceneRenderer.cs
// 渲染接口：将场景（已提交/活动/选区）交给平台实现；本层不依赖 ArkUI。
export interface DrawCommand {
  Contours: number[][];
  Color: number;
}

export class Invalidation {
  Full: boolean = true;
  Left: number = 0;
  Top: number = 0;
  Right: number = 0;
  Bottom: number = 0;

  static full(): Invalidation {
    const invalidation = new Invalidation();
    invalidation.Full = true;
    return invalidation;
  }

  static box(left: number, top: number, right: number, bottom: number): Invalidation {
    const invalidation = new Invalidation();
    invalidation.Full = false;
    invalidation.Left = left;
    invalidation.Top = top;
    invalidation.Right = right;
    invalidation.Bottom = bottom;
    return invalidation;
  }

  union(other: Invalidation): Invalidation {
    if (this.Full || other.Full) {
      return Invalidation.full();
    }
    return Invalidation.box(
      Math.min(this.Left, other.Left),
      Math.min(this.Top, other.Top),
      Math.max(this.Right, other.Right),
      Math.max(this.Bottom, other.Bottom)
    );
  }
}

export interface HandlePoint {
  X: number;
  Y: number;
}

export interface SelectionOverlay {
  Bounds: number[];
  RotationRadians: number;
  Handles: HandlePoint[];
}

export class InkScene {
  Committed: DrawCommand[] = [];
  Active: DrawCommand | null = null;
  Selection: SelectionOverlay | null = null;
  // F3：形状拖动预览（世界坐标，与 Active 同款描边，叠在 Live 之后）；默认空数组，存量调用方不受影响。
  Preview: DrawCommand[] = [];
  // F3：框选橡皮筋（视图 DIP [left, top, right, bottom]）；无橡皮筋时为 null。
  Marquee: number[] | null = null;
  DocumentVersion: number = 0;
  View: number[] = [1, 0, 0, 1, 0, 0];
}

export interface InkRenderer {
  readonly Kind: string;
  render(scene: InkScene, invalidation: Invalidation): void;
}
