// @parity src/InkCanvas.Ink/Rendering/InkSceneRenderer.cs
// 纯逻辑场景构建：把文档/活动笔画/选区转换为渲染命令（轮廓由 core 生成）。
import {
  InkStroke,
  InkDocument,
  Matrix3x2,
  matrixToArray,
  InkPoint,
  InkColor,
  StrokeAttributes,
  buildUnionContours,
  transformPointByMatrix
} from '@inkcanvas/core';
import { InkScene } from './Renderer';
import type { DrawCommand, HandlePoint, SelectionOverlay } from './Renderer';

export interface SceneStrokeInput {
  BaseWidth: number;
  ColorPacked: number;
  IsHighlighter: boolean;
  HighlighterAlpha: number;
  TaperKind: number;
  Points: ScenePointInput[];
}

export interface ScenePointInput {
  X: number;
  Y: number;
  Width: number;
}

export interface SceneSelectionInput {
  Left: number;
  Top: number;
  Right: number;
  Bottom: number;
  RotationRadians: number;
}

function buildStroke(input: SceneStrokeInput): InkStroke {
  const attributes = new StrokeAttributes();
  attributes.BaseWidth = input.BaseWidth;
  attributes.Color = InkColor.fromPackedArgb(input.ColorPacked);
  attributes.IsHighlighter = input.IsHighlighter;
  attributes.HighlighterAlpha = input.HighlighterAlpha;
  attributes.TaperKind = input.TaperKind;
  const stroke = new InkStroke(attributes);
  const points: InkPoint[] = [];
  for (let i = 0; i < input.Points.length; i++) {
    const p = input.Points[i];
    points.push(new InkPoint(p.X, p.Y, 0.5, p.Width, 0));
  }
  stroke.restorePoints(points);
  return stroke;
}

function commandFor(stroke: InkStroke, smooth: boolean = false): DrawCommand {
  const contours = buildUnionContours(smooth ? smoothRenderPoints(stroke.Points) : stroke.Points);
  const nib = stroke.Attributes.nibTransform();
  const transformed: number[][] = [];
  for (let i = 0; i < contours.length; i++) {
    const contour = contours[i];
    const out: number[] = [];
    for (let k = 0; k + 1 < contour.length; k += 2) {
      const point = transformPointByMatrix(nib, contour[k], contour[k + 1]);
      out.push(point.X);
      out.push(point.Y);
    }
    transformed.push(out);
  }
  return {
    Contours: transformed,
    Color: stroke.Attributes.effectiveColor().packedArgb()
  };
}

// Interpolation belongs to rendering. The controller keeps the sampled points and their original widths.
export function smoothRenderPoints(points: InkPoint[]): InkPoint[] {
  if (points.length < 3) {
    return points;
  }
  const result: InkPoint[] = [points[0]];
  for (let i = 0; i + 1 < points.length; i++) {
    const a = points[i];
    const b = points[i + 1];
    const before = points[Math.max(0, i - 1)];
    const after = points[Math.min(points.length - 1, i + 2)];
    const length = Math.hypot(b.X - a.X, b.Y - a.Y);
    if (length > 1.5) {
      // Bound the tangents by the segment length so irregular spacing cannot create large excursions.
      const startLimit = Math.min(0.5, length / Math.max(length, Math.hypot(b.X - before.X, b.Y - before.Y)));
      const endLimit = Math.min(0.5, length / Math.max(length, Math.hypot(after.X - a.X, after.Y - a.Y)));
      for (let step = 1; step < 3; step++) {
        const t = step / 3;
        const t2 = t * t;
        const t3 = t2 * t;
        const start = t3 - 2 * t2 + t;
        const end = t3 - t2;
        const mix = 3 * t2 - 2 * t3;
        result.push(new InkPoint(
          a.X + (b.X - a.X) * mix + start * (b.X - before.X) * startLimit + end * (after.X - a.X) * endLimit,
          a.Y + (b.Y - a.Y) * mix + start * (b.Y - before.Y) * startLimit + end * (after.Y - a.Y) * endLimit,
          0.5, a.Width + (b.Width - a.Width) * t
        ));
      }
    }
    result.push(b);
  }
  return result;
}

export function sceneCommandForStroke(stroke: InkStroke, smooth: boolean = false,
  preview: Matrix3x2 | null = null): DrawCommand {
  const attributes = stroke.Attributes;
  const points: ScenePointInput[] = [];
  for (const point of stroke.Points) {
    if (preview === null) {
      points.push({ X: point.X, Y: point.Y, Width: point.Width });
    } else {
      const position = transformPointByMatrix(preview, point.X, point.Y);
      points.push({ X: position.X, Y: position.Y, Width: point.Width });
    }
  }
  const input: SceneStrokeInput = {
    BaseWidth: attributes.BaseWidth,
    ColorPacked: attributes.Color.packedArgb(),
    IsHighlighter: attributes.IsHighlighter,
    HighlighterAlpha: attributes.HighlighterAlpha,
    TaperKind: attributes.TaperKind,
    Points: points
  };
  return commandFor(buildStroke(input), smooth && !stroke.IsRecognizedShape && stroke.ShapeSource === null);
}

interface CachedStrokeCommand {
  key: string;
  command: DrawCommand;
}

export class CommittedStrokeCommands {
  private entries: Map<InkStroke, CachedStrokeCommand> = new Map<InkStroke, CachedStrokeCommand>();
  private document: InkDocument | null = null;
  private version: number = -1;

  updateDocument(document: InkDocument): void {
    if (document !== this.document) {
      this.entries.clear();
      this.document = document;
    } else if (document.Version === this.version) {
      return;
    } else {
      const present = new Set<InkStroke>(document.Strokes);
      this.entries.forEach((entry: CachedStrokeCommand, stroke: InkStroke) => {
        if (!present.has(stroke)) {
          this.entries.delete(stroke);
        }
      });
    }
    this.version = document.Version;
  }

  get(stroke: InkStroke, smooth: boolean, preview: Matrix3x2 | null = null): DrawCommand {
    const attr = stroke.Attributes;
    const key = stroke.GeometryVersion + '|' + attr.BaseWidth + '|' + attr.Color.packedArgb() + '|'
      + attr.IsHighlighter + '|' + attr.HighlighterAlpha + '|' + attr.TaperKind + '|'
      + smooth + '|' + stroke.IsRecognizedShape + '|' + (stroke.ShapeSource !== null) + '|'
      + (preview === null ? '' : matrixToArray(preview).join(','));
    const existing = this.entries.get(stroke);
    if (existing !== undefined && existing.key === key) {
      return existing.command;
    }
    const command = sceneCommandForStroke(stroke, smooth, preview);
    this.entries.set(stroke, { key: key, command: command });
    return command;
  }
}

export function buildScene(
  committed: SceneStrokeInput[],
  active: SceneStrokeInput | null,
  selection: SceneSelectionInput | null,
  view?: number[],
  preview?: SceneStrokeInput[],
  marquee?: number[] | null
): InkScene {
  const commands = committed.map((input: SceneStrokeInput) => commandFor(buildStroke(input)));
  const live = active === null ? null : commandFor(buildStroke(active));
  const previewCommands = preview?.map((input: SceneStrokeInput) => commandFor(buildStroke(input)));
  return buildSceneFromCommands(commands, live, selection, view, previewCommands, marquee);
}

export function buildSceneFromCommands(
  committed: DrawCommand[],
  active: DrawCommand | null,
  selection: SceneSelectionInput | null,
  view?: number[],
  preview?: DrawCommand[],
  marquee?: number[] | null
): InkScene {
  const scene = new InkScene();
  if (view !== undefined && view.length === 6) {
    scene.View = view;
  }
  scene.Committed = committed;
  scene.Active = active;
  if (preview !== undefined) {
    scene.Preview = preview;
  }
  if (marquee !== null && marquee !== undefined && marquee.length === 4) {
    scene.Marquee = marquee.slice();
  }
  if (selection !== null) {
    const handles: HandlePoint[] = [
      { X: selection.Left, Y: selection.Top },
      { X: selection.Right, Y: selection.Top },
      { X: selection.Right, Y: selection.Bottom },
      { X: selection.Left, Y: selection.Bottom },
      { X: (selection.Left + selection.Right) / 2, Y: selection.Top - 28 }
    ];
    scene.Selection = {
      Bounds: [selection.Left, selection.Top, selection.Right, selection.Bottom],
      RotationRadians: selection.RotationRadians,
      Handles: handles
    };
  }
  return scene;
}

export interface SceneSummaryResult {
  Committed: { Contours: number; Color: number }[];
  Active: { Contours: number; Color: number } | null;
  Selection: { HandleCount: number; Bounds: number[] } | null;
}

export function sceneSummary(
  committed: SceneStrokeInput[],
  active: SceneStrokeInput | null,
  selection: SceneSelectionInput | null
): SceneSummaryResult {
  const scene = buildScene(committed, active, selection);
  const result: SceneSummaryResult = {
    Committed: [],
    Active: null,
    Selection: null
  };
  for (let i = 0; i < scene.Committed.length; i++) {
    result.Committed.push({ Contours: scene.Committed[i].Contours.length, Color: scene.Committed[i].Color });
  }
  if (scene.Active !== null) {
    result.Active = { Contours: scene.Active.Contours.length, Color: scene.Active.Color };
  }
  if (scene.Selection !== null) {
    result.Selection = { HandleCount: scene.Selection.Handles.length, Bounds: scene.Selection.Bounds };
  }
  return result;
}
