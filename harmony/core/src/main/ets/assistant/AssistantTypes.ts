// @parity src/InkCanvas.App/Services/ShellController.cs（AI 助教为鸿蒙侧新增能力，C# 侧未回移）
// AI 魔法套索的纯逻辑契约：状态、请求/事件模型。本文件不依赖 ArkUI/@kit，Node 可直接运行。

export enum AssistantState {
  Idle = 0,
  Capturing = 1,
  Recognizing = 2,
  Streaming = 3,
  Done = 4,
  Error = 5
}

export enum AssistantMode {
  Auto = 0,
  Formula = 1,
  Solve = 2,
  Shape = 3,
  Chat = 4
}

export enum AssistantEventType {
  Token = 0,
  Formula = 1,
  Step = 2,
  ShapeAnnotation = 3,
  Done = 4,
  Error = 5
}

export enum AssistantRole {
  User = 0,
  Assistant = 1
}

export interface SelectionRect {
  Left: number;
  Top: number;
  Right: number;
  Bottom: number;
}

export interface InkPointPayload {
  X: number;
  Y: number;
  Pressure: number;
  TimeMs: number;
}

export interface InkStrokePayload {
  BaseWidth: number;
  ColorPacked: number;
  Points: InkPointPayload[];
}

export interface AssistantViewport {
  Scale: number;
  Rect: SelectionRect;
}

export interface AssistantRequest {
  ImageBase64: string;
  InkStrokes: InkStrokePayload[];
  Viewport: AssistantViewport;
  Mode: number;
  SessionId: string;
}

export interface AssistantEvent {
  Type: number;
  Text: string;
  Latex: string;
  Index: number;
  Title: string;
  Points: number[][];
}

export interface AssistantStep {
  Index: number;
  Text: string;
  Latex: string;
}

export interface AssistantMessage {
  Role: number;
  Text: string;
  Complete: boolean;
}

export const ASSISTANT_EMPTY_SESSION_ID: string = '';

export function modeName(mode: number): string {
  if (mode === AssistantMode.Formula) {
    return 'formula';
  }
  if (mode === AssistantMode.Solve) {
    return 'solve';
  }
  if (mode === AssistantMode.Shape) {
    return 'shape';
  }
  if (mode === AssistantMode.Chat) {
    return 'chat';
  }
  return 'auto';
}

export function stateName(state: number): string {
  if (state === AssistantState.Capturing) {
    return 'capturing';
  }
  if (state === AssistantState.Recognizing) {
    return 'recognizing';
  }
  if (state === AssistantState.Streaming) {
    return 'streaming';
  }
  if (state === AssistantState.Done) {
    return 'done';
  }
  if (state === AssistantState.Error) {
    return 'error';
  }
  return 'idle';
}

export function emptyEvent(type: number): AssistantEvent {
  return {
    Type: type,
    Text: '',
    Latex: '',
    Index: 0,
    Title: '',
    Points: []
  };
}

export function eventFromText(type: number, text: string): AssistantEvent {
  const event = emptyEvent(type);
  event.Text = text;
  return event;
}