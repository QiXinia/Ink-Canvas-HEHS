// 手写笔手势订阅封装：@kit.Penkit 的 stylusInteraction（双击 doubleTap / 轻捏 squeeze）。
// 接口起始版本 5.1.1(19)，SystemCapability.Stylus.StylusService，Stage 模型，无需任何权限。
// 声明依据：D:\HarmonyVM\DevEcoStudio\sdk\default\hms\ets\api\@hms.officeservice.stylusInteraction.d.ts
// （@kit.Penkit 经 hms\ets\kits\@kit.Penkit.d.ts 同名再导出 stylusInteraction）。
//
// HSP 缺失降级（实测运行时坑）：@kit.Penkit 由共享 HSP 提供，标准模拟器镜像不带该 HSP；
// 顶层静态 import 会在 ES module 实例化阶段、早于 canIUse/try-catch 执行时加载失败并 SIGABRT 闪退。
// 因此这里：① 仅用 import type 取类型，编译后擦除、零运行时 HSP 依赖；
// ② 运行期以常量字符串动态 import 懒加载，HSP 缺失时在 catch 永久标记 unavailable，后续 on/off 全部跳过；
// ③ hasSysCap 能力门控 + 调用 try/catch 双保险。设备不支持或异常时静默降级：不崩溃、不打错误级别日志干扰。
//
// ArkTS 约束：stylusInteraction 在 SDK 中声明为 namespace，arkts-no-ns-as-obj 禁止把它提取为
// 普通对象/接口变量，因此动态导入的模块命名空间以 ESObject 持有，on/off 均经该命名空间直接调用，
// 不做 namespace → interface 的断言。
import type { stylusInteraction } from '@kit.Penkit';
import type { BusinessError } from '@kit.BasicServicesKit';
import { hasSysCap, SYSCAP_STYLUS_SERVICE } from '../compat/Capabilities';

// 事件类型与 SDK 声明一致：DoubleTapEvent / SqueezeEvent 均只含 timestamp（ms，距系统启动）。
export type StylusDoubleTapEvent = stylusInteraction.DoubleTapEvent;
export type StylusSqueezeEvent = stylusInteraction.SqueezeEvent;

export type StylusDoubleTapCallback = (event: StylusDoubleTapEvent) => void;
export type StylusSqueezeCallback = (event: StylusSqueezeEvent) => void;

export type StylusGestureType = 'doubleTap' | 'squeeze';

type LoadState = 'idle' | 'loading' | 'ready' | 'unavailable';

let loadState: LoadState = 'idle';
// 动态导入的 Penkit 模块命名空间（ESObject 为 ArkTS 动态互操作类型，承载延迟加载模块）。
let penkitNs: ESObject | null = null;
let loadPromise: Promise<ESObject | null> | null = null;

// 懒加载 Penkit：同一页面生命周期内只发起一次动态 import。
// HSP 缺失/能力不支持时永久返回 null，调用方全部走静默降级路径。
function loadInteraction(): Promise<ESObject | null> {
  if (loadState === 'unavailable') {
    return Promise.resolve(null);
  }
  if (penkitNs !== null) {
    return Promise.resolve(penkitNs);
  }
  if (loadPromise !== null) {
    return loadPromise;
  }
  try {
    if (!hasSysCap(SYSCAP_STYLUS_SERVICE)) {
      loadState = 'unavailable';
      return Promise.resolve(null);
    }
  } catch (e) {
    void e;
    loadState = 'unavailable';
    return Promise.resolve(null);
  }
  loadState = 'loading';
  // 常量字符串动态 import：编译期自动入依赖树，无需改 build-profile runtimeOnly 配置。
  loadPromise = import('@kit.Penkit').then((ns: ESObject): ESObject => {
    penkitNs = ns;
    loadState = 'ready';
    return ns;
  }).catch((err: BusinessError): ESObject | null => {
    void err;
    penkitNs = null;
    loadState = 'unavailable';
    return null;
  });
  return loadPromise;
}

// 手写笔手势订阅/释放句柄。on 与 off 成对调用；页面销毁时统一走 release()。
export class StylusBinding {
  private doubleTapReceiver: StylusDoubleTapCallback | null = null;
  private squeezeReceiver: StylusSqueezeCallback | null = null;
  private detached: boolean = false;

  on(type: StylusGestureType, cb: StylusDoubleTapCallback | StylusSqueezeCallback): void {
    if (loadState === 'unavailable' || this.detached) {
      return;
    }
    if (type === 'doubleTap') {
      // 库防御：同类型重复 on 时先按旧引用摘除 native 回调，避免字段覆盖后旧监听泄漏。
      // penkitNs 尚在加载时旧回调从未注册，无需 off，加载完成后只会注册最新引用。
      if (this.doubleTapReceiver !== null) {
        this.detach('doubleTap', this.doubleTapReceiver);
      }
      this.doubleTapReceiver = cb as StylusDoubleTapCallback;
    } else {
      if (this.squeezeReceiver !== null) {
        this.detach('squeeze', this.squeezeReceiver);
      }
      this.squeezeReceiver = cb as StylusSqueezeCallback;
    }
    void loadInteraction().then((api: ESObject | null): void => {
      // 加载完成前已 off/release，或 HSP 不可用：不再挂接。
      if (this.detached || api === null) {
        return;
      }
      try {
        if (type === 'doubleTap' && this.doubleTapReceiver !== null) {
          api.stylusInteraction.on('doubleTap', this.doubleTapReceiver);
        } else if (type === 'squeeze' && this.squeezeReceiver !== null) {
          api.stylusInteraction.on('squeeze', this.squeezeReceiver);
        }
      } catch (e) {
        void e;
      }
    });
  }

  off(type: StylusGestureType): void {
    if (type === 'doubleTap') {
      const receiver = this.doubleTapReceiver;
      this.doubleTapReceiver = null;
      this.detach('doubleTap', receiver);
    } else {
      const receiver = this.squeezeReceiver;
      this.squeezeReceiver = null;
      this.detach('squeeze', receiver);
    }
  }

  // 页面销毁：成对释放两类手势监听，避免泄漏。
  release(): void {
    this.detached = true;
    this.off('doubleTap');
    this.off('squeeze');
  }

  private detach(type: 'doubleTap' | 'squeeze',
    receiver: StylusDoubleTapCallback | StylusSqueezeCallback | null): void {
    if (receiver === null || penkitNs === null) {
      return;
    }
    try {
      if (type === 'doubleTap') {
        penkitNs.stylusInteraction.off('doubleTap', receiver);
      } else {
        penkitNs.stylusInteraction.off('squeeze', receiver);
      }
    } catch (e) {
      void e;
    }
  }
}
