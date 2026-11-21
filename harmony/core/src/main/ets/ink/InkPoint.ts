// @parity src/InkCanvas.Core/Ink/InkPoint.cs
// 可变点（C# 为 struct；ArkTS 用 class，所有复制点必须显式 clone）。
export class InkPoint {
  X: number;
  Y: number;
  Pressure: number;
  Width: number;
  TimeMs: number;
  // 手写笔硬件倾角（度，ArkUI TouchObject.tiltX/tiltY 原值）；无硬件数据时为 0。
  // 运行期字段：不进 PdfInkCodec/IcinkCodec 持久化行，跨端存档格式保持 5 元素不变。
  TiltX: number;
  TiltY: number;
  // true 表示 Pressure 来自手写笔真实压感（sourceTool=Pen 且 pressure>0）；
  // false 时 Pressure 为 0.5 回退/速度模拟值，宽度必须与 C# 速度笔锋逐位一致（黄金向量路径）。
  HasPressure: boolean;

  constructor(
    x: number = 0,
    y: number = 0,
    pressure: number = 0.5,
    width: number = 0,
    timeMs: number = 0,
    tiltX: number = 0,
    tiltY: number = 0,
    hasPressure: boolean = false
  ) {
    this.X = x;
    this.Y = y;
    this.Pressure = pressure;
    this.Width = width;
    this.TimeMs = timeMs;
    this.TiltX = tiltX;
    this.TiltY = tiltY;
    this.HasPressure = hasPressure;
  }

  clone(): InkPoint {
    return new InkPoint(
      this.X,
      this.Y,
      this.Pressure,
      this.Width,
      this.TimeMs,
      this.TiltX,
      this.TiltY,
      this.HasPressure
    );
  }

  static fromArray(row: number[]): InkPoint {
    if (row.length < 4) {
      throw new Error('InkPoint row needs at least 4 values');
    }
    return new InkPoint(row[0], row[1], row[2], row[3], row.length > 4 ? row[4] : 0);
  }
}
