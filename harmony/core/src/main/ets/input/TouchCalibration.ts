// @parity src/InkCanvas.App/Views/SettingsWindow.cs（触摸大小倍数校准垫数学）
// 校准：采集若干手指接触宽度样本，乘数 = 目标参考宽度 / 平均测量宽度，钳制 [0.1, 5]。
// 空样本或零均值回退 1（不改变现状）。乘数 applied 到 advanced.touchMultiplier。
export function calibrationMultiplier(samples: number[], targetWidth: number): number {
  if (samples.length === 0) {
    return 1;
  }
  let sum = 0;
  for (let i = 0; i < samples.length; i++) {
    sum += samples[i];
  }
  const avg = sum / samples.length;
  if (avg <= 0) {
    return 1;
  }
  const multiplier = targetWidth / avg;
  return Math.min(5, Math.max(0.1, multiplier));
}

export const PEN_OFFSET_LIMIT: number = 50;

export function normalizePenOffset(value: number): number {
  return Number.isFinite(value) ? Math.max(-PEN_OFFSET_LIMIT, Math.min(PEN_OFFSET_LIMIT, value)) : 0;
}
