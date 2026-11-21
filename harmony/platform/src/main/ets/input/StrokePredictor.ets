// Pen Kit 报点预测封装（@kit.Penkit PointPredictor，起始 5.0.0(12)，
// SystemCapability.Stylus.Handwrite，无需权限）。
// 职责单一：给定原始 TouchEvent 返回一个预测点（组件坐标 vp，与 TouchObject.x/y 同空间）。
// 不持有文档状态、不渲染、不写缓存、不做坐标变换；不支持时全部方法空操作返回 null，
// 调用方可按 null 零开销降级，手指/鼠标路径与不支持该能力的设备行为完全不变。
//
// 关键实现约束：@kit.Penkit 由 com.huawei.hmos.hwstylusfeature/Penkit 共享 HSP 提供，
// 未携带该 HSP 的设备（如标准模拟器）在模块实例化阶段静态 import 会直接 LoadJSPandaFile
// 失败并 SIGABRT 闪退，且早于任何 canIUse/try-catch 执行。因此这里必须使用常量动态
// import()：编译期照常打入依赖树，运行期才解析 HSP；加载失败走 catch 永久降级，不崩溃。
// import type 仅在编译期提供类型，产物中擦除，不产生运行时 HSP 依赖。
// 注意：动态 import() 的 .then 回调参数 ns 由编译器推断为该模块的命名空间类型（非 any），
// 不可改标 ESObject——ESObject 上的方法调用返回 any 会触发 arkts-no-any-unknown。
import type { PointPredictor } from '@kit.Penkit';
import type { BusinessError } from '@kit.BasicServicesKit';
import { hasSysCap, SYSCAP_STYLUS_HANDWRITE } from '../compat/Capabilities';

export interface PredictedPoint {
  X: number;
  Y: number;
}

export class StrokePredictor {
  private predictor: PointPredictor | null = null;
  private loading: boolean = false;
  private unavailable: boolean = false;

  constructor() {
    this.ensureLoaded();
  }

  // 异步预热 Pen Kit：能力缺失或 HSP 加载失败时永久标记不可用，之后不再重试。
  private ensureLoaded(): void {
    if (this.loading || this.unavailable || this.predictor !== null) {
      return;
    }
    try {
      if (!hasSysCap(SYSCAP_STYLUS_HANDWRITE)) {
        this.unavailable = true;
        return;
      }
    } catch (e) {
      void e;
      this.unavailable = true;
      return;
    }
    this.loading = true;
    import('@kit.Penkit').then((ns): void => {
      this.predictor = new ns.PointPredictor();
      this.loading = false;
    }).catch((error: BusinessError): void => {
      void error;
      // 设备未安装 Pen Kit HSP（典型为模拟器）：静默降级，预测功能整体不可用。
      this.loading = false;
      this.unavailable = true;
    });
  }

  // 仅在 TouchType.Move 且手写笔书写场景调用；任何异常都视为本帧无预测点。
  predict(event: TouchEvent): PredictedPoint | null {
    const predictor = this.predictor;
    if (predictor === null) {
      return null;
    }
    try {
      const point: TouchPoint = predictor.getPredictionPoint(event);
      if (point === null || point === undefined) {
        return null;
      }
      // TouchPoint.x/y 声明为 Dimension（number | string），运行时为数值坐标，统一收敛为 number。
      const px = Number(point.x);
      const py = Number(point.y);
      if (!Number.isFinite(px) || !Number.isFinite(py)) {
        return null;
      }
      return { X: px, Y: py };
    } catch (e) {
      void e;
      return null;
    }
  }
}
