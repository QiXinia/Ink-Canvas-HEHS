// @parity src/InkCanvas.Platform/Windows/LayeredInkWindow.cs（输入归一化部分）
// ArkUI 事件 → 控制器归一化指针。数值映射需在模拟器/真机核对（见 notes）。
export enum PointerKind {
  Pen = 0,
  Touch = 1,
  Mouse = 2
}

// ArkUI SourceTool 的数值（Finger=1, Pen=2, MOUSE=3, TOUCHPAD=4）；
// SourceType（Mouse=1, TouchScreen=2）。均为 SDK 枚举，非臆造。
export interface RawTouchEvent {
  Id: number;
  X: number;
  Y: number;
  Pressure: number;
  TiltX: number;
  TiltY: number;
  ContactWidth: number;
  ContactHeight: number;
  TimestampNs: number;
  Source: number;
  SourceTool: number;
}

export interface NormalizedPointer {
  Id: number;
  X: number;
  Y: number;
  Pressure: number;
  TiltX: number;
  TiltY: number;
  ContactWidth: number;
  ContactHeight: number;
  TimeMs: number;
  Kind: number;
  HasPressure: boolean;
}

export function pointerKind(source: number, sourceTool: number): number {
  if (sourceTool === 2) {
    return PointerKind.Pen;
  }
  if (source === 1 || sourceTool === 3 || sourceTool === 4) {
    return PointerKind.Mouse;
  }
  return PointerKind.Touch;
}

export function normalizePointer(raw: RawTouchEvent): NormalizedPointer {
  const pressure = raw.Pressure > 0 ? raw.Pressure : 0.5;
  return {
    Id: raw.Id,
    X: raw.X,
    Y: raw.Y,
    Pressure: pressure,
    TiltX: raw.TiltX,
    TiltY: raw.TiltY,
    ContactWidth: raw.ContactWidth,
    ContactHeight: raw.ContactHeight,
    TimeMs: raw.TimestampNs / 1000000,
    Kind: pointerKind(raw.Source, raw.SourceTool),
    // 仅手写笔（SourceTool.Pen=2）且硬件给出非零压感时才视为真实压感。
    HasPressure: raw.SourceTool === 2 && raw.Pressure > 0
  };
}

// 鼠标左键会被 ArkUI 当触摸事件派发；必须按 source/sourceTool 过滤。
export function isMouseEvent(source: number, sourceTool: number): boolean {
  return pointerKind(source, sourceTool) === PointerKind.Mouse;
}
