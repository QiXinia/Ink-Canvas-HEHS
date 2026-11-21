// @parity src/InkCanvas.Features/Board/BoardService.cs（AI 结果贴回为鸿蒙侧新增对象形态）
// 贴回对象：复用画板 Images 资产（ContentType 标记 AI 卡片，Data 存 JSON 元数据），
// 插入/撤销/重做各算一个撤销单元；插入位置默认选区右侧 16vp。
import type { SelectionRect } from './AssistantTypes';

export enum PasteKind {
  Formula = 0,
  Steps = 1,
  Shape = 2
}

export interface PasteInput {
  Kind: number;
  Text: string;
  Latex: string;
  Anchor: SelectionRect;
  Scale: number;
}

export interface PasteObject {
  Id: string;
  Kind: number;
  Text: string;
  Latex: string;
  Left: number;
  Top: number;
  Width: number;
  Height: number;
  ContentType: string;
  Data: string;
}

export interface PasteResult {
  Left: number;
  Top: number;
  Width: number;
  Height: number;
  ContentType: string;
  Data: string;
}

export const AI_CARD_CONTENT_TYPE: string = 'application/vnd.inkcanvas.ai';
export const PASTE_GAP: number = 16;
export const AI_CARD_MIN_WIDTH: number = 180;
export const AI_CARD_MAX_WIDTH: number = 320;
export const AI_CARD_FONT_SIZE: number = 14;
export const AI_CARD_LINE_HEIGHT: number = 22;

function charWidth(text: string): number {
  let total = 0;
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    total += code > 0x2E80 ? 1 : 0.55;
  }
  return total;
}

export function estimateCardSize(input: PasteInput): number[] {
  const source = input.Kind === PasteKind.Formula && input.Latex.length > 0 ? input.Latex : input.Text;
  const lines = source.length === 0 ? [''] : source.split('\n');
  let widest = 0;
  for (let i = 0; i < lines.length; i++) {
    const width = charWidth(lines[i]) * AI_CARD_FONT_SIZE + 24;
    if (width > widest) {
      widest = width;
    }
  }
  const width = Math.min(AI_CARD_MAX_WIDTH, Math.max(AI_CARD_MIN_WIDTH, widest));
  const wrappedExtra = widest > AI_CARD_MAX_WIDTH ? Math.ceil(widest / AI_CARD_MAX_WIDTH) : 1;
  const height = Math.max(56, lines.length * AI_CARD_LINE_HEIGHT * wrappedExtra + 24);
  return [Math.round(width), Math.round(height)];
}

export function buildPasteObject(input: PasteInput, seq: number): PasteObject {
  const size = estimateCardSize(input);
  const scale = input.Scale > 0 ? input.Scale : 1;
  const left = input.Anchor.Right + PASTE_GAP;
  const top = input.Anchor.Top;
  const payload = {
    Kind: input.Kind,
    Text: input.Text,
    Latex: input.Latex
  };
  return {
    Id: 'ai-' + seq + '-' + Math.round(left) + '-' + Math.round(top),
    Kind: input.Kind,
    Text: input.Text,
    Latex: input.Latex,
    Left: Math.round(left),
    Top: Math.round(top),
    Width: Math.round(size[0] / scale),
    Height: Math.round(size[1] / scale),
    ContentType: AI_CARD_CONTENT_TYPE,
    Data: JSON.stringify(payload)
  };
}

// 贴回位置钳制：不越过视口右/下边界；空间不足时退回选区右侧并联到选区下沿。
export function clampPasteRect(
  object: PasteResult,
  anchor: SelectionRect,
  viewportWidth: number,
  viewportHeight: number
): PasteResult {
  let left = object.Left;
  let top = object.Top;
  if (left + object.Width > viewportWidth) {
    left = anchor.Right + PASTE_GAP;
    if (left + object.Width > viewportWidth) {
      left = viewportWidth - object.Width;
    }
  }
  if (left < 0) {
    left = 0;
  }
  if (top + object.Height > viewportHeight) {
    top = anchor.Bottom - object.Height;
    if (top < 0) {
      top = 0;
    }
  }
  return {
    Left: Math.round(left),
    Top: Math.round(top),
    Width: object.Width,
    Height: object.Height,
    ContentType: object.ContentType,
    Data: object.Data
  };
}

// 撤销/重做探针：模拟“插入一条资产 → undo 移除 → redo 复现”。
export function pasteUndoProbe(
  anchor: number[],
  kind: number,
  text: string,
  latex: string,
  viewportWidth: number,
  viewportHeight: number
): number[][][] {
  const rect: SelectionRect = { Left: anchor[0], Top: anchor[1], Right: anchor[2], Bottom: anchor[3] };
  const input: PasteInput = { Kind: kind, Text: text, Latex: latex, Anchor: rect, Scale: 1 };
  const built = buildPasteObject(input, 1);
  const result = clampPasteRect(built, rect, viewportWidth, viewportHeight);
  const inserted = [[result.Left, result.Top, result.Width, result.Height]];
  const afterUndo: number[][] = [];
  const afterRedo = [[result.Left, result.Top, result.Width, result.Height]];
  return [inserted, afterUndo, afterRedo];
}

export function pasteResult(input: PasteInput, seq: number, viewportWidth: number, viewportHeight: number): PasteResult {
  const built = buildPasteObject(input, seq);
  return clampPasteRect(built, input.Anchor, viewportWidth, viewportHeight);
}