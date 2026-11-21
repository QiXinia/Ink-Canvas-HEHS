// @parity src/InkCanvas.Core/Settings/AppSettings.cs（AI 端点/模型/超时配置；环境变量覆盖为新增）
// 端点为空时走本地 mock 流，保证无网络也能自测；真实服务由 AI_ENDPOINT 注入。
import { process } from '@kit.ArkTS';

export class AiConfig {
  Endpoint: string = '';
  Model: string = 'inkcanvas-assistant';
  TimeoutMs: number = 15000;
}

export const AI_DEFAULT_TIMEOUT_MS: number = 15000;
export const AI_DEFAULT_MODEL: string = 'inkcanvas-assistant';

export function envValue(name: string, fallback: string): string {
  try {
    const value = process.getEnvironmentVar(name);
    if (value !== undefined && value !== null && value.length > 0) {
      return value;
    }
  } catch (e) {
    return fallback;
  }
  return fallback;
}

export function parseTimeoutMs(value: string, fallback: number): number {
  const parsed = Number(value);
  if (Number.isNaN(parsed) || parsed <= 0) {
    return fallback;
  }
  return Math.min(Math.max(Math.round(parsed), 1000), 120000);
}

export function readAiConfig(): AiConfig {
  const config = new AiConfig();
  config.Endpoint = envValue('AI_ENDPOINT', '');
  config.Model = envValue('AI_MODEL', AI_DEFAULT_MODEL);
  config.TimeoutMs = parseTimeoutMs(envValue('AI_TIMEOUT_MS', String(AI_DEFAULT_TIMEOUT_MS)), AI_DEFAULT_TIMEOUT_MS);
  return config;
}

export function describeAiConfig(config: AiConfig): string {
  const endpoint = config.Endpoint.length > 0 ? config.Endpoint : 'mock（本地流式）';
  return 'AI 服务：' + endpoint + ' · 模型 ' + config.Model + ' · 超时 ' + config.TimeoutMs + 'ms';
}