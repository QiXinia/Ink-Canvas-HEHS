// @parity src/InkCanvas.Features/Tools/Tools.cs（ScreenshotService 区域截图；选区裁剪+压缩为新增）
// 选区截图：componentSnapshot 按区域截取本应用组件（鸿蒙无跨应用截图），
// 超出长边 1600 时等比缩小，输出无前缀 base64（JPEG）。
import { componentSnapshot, display } from '@kit.ArkUI';
import { image } from '@kit.ImageKit';
import { util } from '@kit.ArkTS';

export const CAPTURE_MAX_EDGE: number = 1600;

function clampFloor(value: number): number {
  return Math.floor(value < 0 ? 0 : value);
}

export async function captureRegionBase64(
  componentId: string,
  left: number,
  top: number,
  right: number,
  bottom: number,
  maxEdge: number = CAPTURE_MAX_EDGE
): Promise<string> {
  let pixelMap: image.PixelMap | null = null;
  let packer: image.ImagePacker | null = null;
  try {
    const density = display.getDefaultDisplaySync().densityPixels;
    const region: componentSnapshot.SnapshotRegion = {
      left: clampFloor(left * density),
      top: clampFloor(top * density),
      right: clampFloor(right * density),
      bottom: clampFloor(bottom * density)
    };
    pixelMap = await componentSnapshot.get(componentId, { region: region, waitUntilRenderFinished: true });
    const info = await pixelMap.getImageInfo();
    const longEdge = Math.max(info.size.width, info.size.height);
    if (longEdge > maxEdge && longEdge > 0) {
      const ratio = maxEdge / longEdge;
      await pixelMap.scale(ratio, ratio);
    }
    packer = image.createImagePacker();
    const data = await packer.packing(pixelMap, { format: 'image/jpeg', quality: 80 });
    const helper = new util.Base64Helper();
    return helper.encodeToStringSync(new Uint8Array(data));
  } catch (e) {
    return '';
  } finally {
    if (packer !== null) {
      try {
        await packer.release();
      } catch (releaseError) {
        void releaseError;
      }
    }
    if (pixelMap !== null) {
      try {
        pixelMap.release();
      } catch (releaseError) {
        void releaseError;
      }
    }
  }
}