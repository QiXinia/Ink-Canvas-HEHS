// @parity src/InkCanvas.Platform/Windows/LayeredInkWindow.cs
// 归一化指针样本、工具/橡皮模式与控制器注入配置。
// 本文件不 import 任何 ArkUI/@kit：宿主（Phase C）负责把 TouchEvent/MouseEvent 归一化为 PointerSample。
import type { InkDocument, PalmRejector, SelectionFrameState, Matrix3x2 } from '@inkcanvas/core';

/** 与 C# OverlayPointerKind 同值。 */
export enum PointerKind {
  Pen = 0,
  Touch = 1,
  Mouse = 2
}

/** 与 C# InkTool 同值。 */
export enum InkTool {
  Pen = 0,
  Highlighter = 1,
  Eraser = 2,
  Select = 3,
  Shape = 4,
  Pan = 5
}

/** 与 C# InkEraserMode 同值。Lasso 为保留枚举值，Phase B2/B3 未移植（见 porting notes）。 */
export enum InkEraserMode {
  Area = 0,
  WholeStroke = 1,
  Lasso = 2
}

/** 与 C# OptionalOperation 同值（Yes/No/Ask）。 */
export enum OptionalOperation {
  Yes = 0,
  No = 1,
  Ask = 2
}

/** 与 C# MatrixTransformCenterPointOptions 同值。 */
export enum TransformCenterOption {
  CanvasCenterPoint = 0,
  SelectedElementsCenterPoint = 1,
  GestureOperationCenterPoint = 2
}

export type VoidCallback = () => void;
export type SlideFlipCallback = (next: boolean) => void;
export type TimeSource = () => number;
export type AskRemoveAsymptotesCallback = () => boolean;

// ---- G2 元素选区桥（C# ISelectionElement/BoardSelectionElement） --------------------------
// 宿主提供的页面元素（图片/视频）：参与框选/点击选中/删除/克隆/双指手势/套索擦。
// 坐标约定：Frame 为世界坐标（Bounds LTRB + 行向量 Transform）；SetPresent 带 z 序恢复。

/** 元素几何快照（Left/Top/Width/Height + 行向量 3x2 变换；与 features ElementGeometry 同形）。 */
export interface ElementSnapshot {
  Left: number;
  Top: number;
  Width: number;
  Height: number;
  Transform: number[] | null;
}

/** 可选元素契约（C# ISelectionElement；D1 增 layerOrder/isImageElement）。 */
export interface ISelectionElement {
  id(): string;
  isPresent(): boolean;
  /** 世界坐标定向框（LTRB + Transform）。 */
  getFrame(): SelectionFrameState;
  /** 离场/在场（离场记住 z 序索引，在场按原序恢复）。 */
  setPresent(present: boolean): void;
  /** 对元素应用世界坐标增量矩阵（行向量：existing * delta）。 */
  transform(delta: Matrix3x2): void;
  /** 复制元素并使其在场（共享字节、新 Id）。 */
  duplicate(): ISelectionElement;
  capture(): ElementSnapshot;
  restore(snapshot: ElementSnapshot): void;
  /** D1 层序：与墨迹共享的自后向前 z 序（null = 未分层，按类型默认位）。 */
  layerOrder(): number | null;
  setLayerOrder(order: number | null): void;
  /** 图片元素（排最底层的默认位）；视频等其它元素返回 false。 */
  isImageElement(): boolean;
}

/**
 * C# OverlayPointer 的 ArkTS 等价物（视图 DIP）。
 * 宿主必须完成：ns→ms 时间戳、pressure 0→0.5 回退、source/sourceTool→Kind、接触宽高、鼠标过滤。
 * TiltX/TiltY 为手写笔硬件倾角（度），无硬件数据时传 0；HasPressure=true 表示 Pressure 为
 * 手写笔真实压感（sourceTool=Pen 且 pressure>0），宽度管线据此启用压感调制。
 */
export class PointerSample {
  Id: number;
  X: number;
  Y: number;
  Pressure: number;
  ContactWidth: number;
  ContactHeight: number;
  Kind: number;
  TimeMs: number;
  Shift: boolean;
  Control: boolean;
  TiltX: number;
  TiltY: number;
  HasPressure: boolean;

  constructor(
    id: number,
    x: number,
    y: number,
    pressure: number = 0.5,
    contactWidth: number = 0,
    contactHeight: number = 0,
    kind: number = PointerKind.Pen,
    timeMs: number = 0,
    shift: boolean = false,
    control: boolean = false,
    tiltX: number = 0,
    tiltY: number = 0,
    hasPressure: boolean = false
  ) {
    this.Id = id;
    this.X = x;
    this.Y = y;
    this.Pressure = pressure;
    this.ContactWidth = contactWidth;
    this.ContactHeight = contactHeight;
    this.Kind = kind;
    this.TimeMs = timeMs;
    this.Shift = shift;
    this.Control = control;
    this.TiltX = tiltX;
    this.TiltY = tiltY;
    this.HasPressure = hasPressure;
  }
}

/** 控制器宿主注入（对应 C# 的 Document/History 构造与事件订阅）。 */
export class ControllerOptions {
  Document: InkDocument | null = null;
  RequestRender: VoidCallback | null = null;
  TimeSource: TimeSource | null = null;
  Palm: PalmRejector | null = null;
  HistoryCapacity: number = 200;
}

/** core ShapeGenerator 的 StrokeAttributesInput 结构（core Index 未导出该类型）。 */
export interface ShapeAttributesInput {
  ColorPacked: number;
  BaseWidth: number;
  IsHighlighter: boolean;
  HighlighterAlpha: number;
  TaperKind: number;
}

// ---- 黄金向量场景（controller.json）-------------------------------------
// 场景由 C# InkController 驱动生成期望，ArkTS runControllerScenario 复现同一序列。
// 字段使用 PascalCase，与 C# 匿名对象序列化一致。

export interface ScenarioAttributes {
  ColorPacked: number;
  BaseWidth: number;
  IsHighlighter: boolean;
  HighlighterAlpha: number;
  TaperKind: number;
}

export interface ScenarioPoint {
  X: number;
  Y: number;
  Pressure?: number;
  Width?: number;
  TimeMs?: number;
}

export interface ScenarioPointer {
  Id: number;
  X: number;
  Y: number;
  Pressure?: number;
  ContactWidth?: number;
  ContactHeight?: number;
  Kind: number;
  TimeMs?: number;
  Shift?: boolean;
  Control?: boolean;
}

export interface ScenarioSeed {
  Attributes?: ScenarioAttributes;
  Points: ScenarioPoint[];
  IsCompleted?: boolean;
}

export interface ScenarioAdvanced {
  isSpecialScreen: boolean;
  isQuadIR: boolean;
  touchMultiplier: number;
  nibModeBoundsWidth: number;
  fingerModeBoundsWidth: number;
  nibModeBoundsWidthThresholdValue: number;
  fingerModeBoundsWidthThresholdValue: number;
  nibModeBoundsWidthEraserSize: number;
  fingerModeBoundsWidthEraserSize: number;
}

export interface ScenarioPalm {
  Advanced?: ScenarioAdvanced | null;
  NibMode?: boolean;
  ForceEraser?: boolean;
  ForcePointEraser?: boolean;
  PptGestureEligible?: boolean;
  PalmContactThreshold?: number;
  PenHasPriority?: boolean;
}

export interface ScenarioInitial {
  Tool?: number;
  ShapeKind?: number;
  EraserMode?: number;
  EraserRadius?: number;
  ViewTransform?: number[];
  ViewCenter?: number[];
  MultiTouchEnabled?: boolean;
  PalmErases?: boolean;
  RecognizeShapes?: boolean;
  GesturesTargetSelection?: boolean;
  CloneMode?: boolean;
  StickyShape?: boolean;
  HyperbolaAsymptoteOption?: number;
  AskRemoveAsymptotes?: boolean | null;
  TransformCenterOption?: number;
  AllowZoom?: boolean;
  AllowTranslate?: boolean;
  AllowRotation?: boolean;
  CurrentAttributes?: ScenarioAttributes;
  Palm?: ScenarioPalm;
}

export interface ScenarioOp {
  Op: string;
  Pointer?: ScenarioPointer;
  Stroke?: ScenarioSeed;
  Value?: number;
  Flag?: boolean;
  Zoom?: boolean;
  Translate?: boolean;
  Rotate?: boolean;
}

export interface ControllerScenario {
  Initial: ScenarioInitial;
  Ops: ScenarioOp[];
}

export interface ControllerTraceStep {
  Doc: number[];
  DocFirst: number[][];
  Live: number;
  Preview: number;
  Sel: number[];
  Undo: number;
  Redo: number;
  Tool: number;
  View: number[];
  Inv: number;
  SelCh: number;
  ToolCh: number;
  Slides: number;
}

export interface ControllerStrokeSummary {
  Points: number[][];
  BaseWidth: number;
  ColorPacked: number;
  IsHighlighter: boolean;
  HighlighterAlpha: number;
  TaperKind: number;
  IsRecognizedShape: boolean;
  IsCompleted: boolean;
}

export interface ControllerFrameSummary {
  Bounds: number[];
  Transform: number[];
}

export interface ControllerFinalState {
  Strokes: ControllerStrokeSummary[];
  Sel: number[];
  Tool: number;
  EraserMode: number;
  CloneMode: boolean;
  View: number[];
  ViewCenter: number[];
  Undo: number;
  Redo: number;
  SelectionBounds: number[];
  SelectionFrame: ControllerFrameSummary;
}

export interface ControllerScenarioResult {
  Trace: ControllerTraceStep[];
  Final: ControllerFinalState;
  SlideFlips: boolean[];
}
