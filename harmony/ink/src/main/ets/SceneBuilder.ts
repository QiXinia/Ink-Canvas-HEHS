// @parity src/InkCanvas.Ink/Rendering/InkSceneRenderer.cs
// 纯逻辑场景构建：把文档/活动笔画/选区转换为渲染命令（轮廓由 core 生成）。
import {
  InkStroke,
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

function commandFor(stroke: InkStroke): DrawCommand {
  const contours = buildUnionContours(stroke.Points);
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

export function buildScene(
  committed: SceneStrokeInput[],
  active: SceneStrokeInput | null,
  selection: SceneSelectionInput | null,
  view?: number[],
  preview?: SceneStrokeInput[],
  marquee?: number[] | null
): InkScene {
  const scene = new InkScene();
  if (view !== undefined && view.length === 6) {
    scene.View = view;
  }
  for (let i = 0; i < committed.length; i++) {
    scene.Committed.push(commandFor(buildStroke(committed[i])));
  }
  if (active !== null) {
    scene.Active = commandFor(buildStroke(active));
  }
  if (preview !== undefined && preview !== null) {
    for (let i = 0; i < preview.length; i++) {
      scene.Preview.push(commandFor(buildStroke(preview[i])));
    }
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
