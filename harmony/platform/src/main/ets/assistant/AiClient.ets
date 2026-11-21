// @parity src/InkCanvas.Features/Update/UpdateService.cs（HttpClient 流式读取；AI SSE 客户端为新增）
// AI 客户端：端点为空时走本地 mock 流（无网络可自测）；配置端点时按 6.1 请求体发 POST，
// 用 requestInStream + dataReceive 逐块喂 SseParser，首 token 超时与 Abort 均保留已生成内容。
import { http } from '@kit.NetworkKit';
import { util } from '@kit.ArkTS';
import { BusinessError } from '@kit.BasicServicesKit';
import { AssistantMode, AssistantEventType, SseParser, emptyEvent } from '@inkcanvas/core';
import type { AssistantEvent, AssistantRequest, AssistantViewport, InkStrokePayload } from '@inkcanvas/core';
import { AiConfig, AI_DEFAULT_TIMEOUT_MS } from './AiConfig';

export type AssistantEventSink = (event: AssistantEvent) => void;
export type AssistantErrorSink = (message: string) => void;
export type AssistantDoneSink = () => void;

export interface AssistantStream {
  Abort(): void;
}

export interface ChatMessage {
  role: string;
  content: string;
}

export interface AssistantPayload {
  model: string;
  mode: string;
  sessionId: string;
  imageBase64: string;
  inkStrokes: InkStrokePayload[];
  viewport: AssistantViewport;
  messages: ChatMessage[];
}

export function buildPrompt(mode: number): string {
  if (mode === AssistantMode.Formula) {
    return '请把手写内容转写为标准 LaTeX 排版，并给出原迹含义。';
  }
  if (mode === AssistantMode.Solve) {
    return '请解出框选中的算式或方程，先给最终答案，再给出分步推导（每步一句中文说明 + 公式）。';
  }
  if (mode === AssistantMode.Shape) {
    return '请识别框选中的几何图形，说明图形类型、已知条件与可推导性质。';
  }
  if (mode === AssistantMode.Chat) {
    return '请基于框选内容回答用户问题。';
  }
  return '请自动判断框选内容类型：公式转写、自动求解或图形释义，并给出结论。';
}

export function buildPayload(request: AssistantRequest, model: string): string {
  const messages: ChatMessage[] = [
    { role: 'system', content: '你是课堂白板 AI 助教，回答使用简体中文，公式使用 LaTeX（行内 $...$，块级 $$...$$）。' },
    { role: 'user', content: buildPrompt(request.Mode) }
  ];
  const payload: AssistantPayload = {
    model: model,
    mode: request.Mode === AssistantMode.Formula ? 'formula'
      : (request.Mode === AssistantMode.Solve ? 'solve'
        : (request.Mode === AssistantMode.Shape ? 'shape'
          : (request.Mode === AssistantMode.Chat ? 'chat' : 'auto'))),
    sessionId: request.SessionId,
    imageBase64: request.ImageBase64,
    inkStrokes: request.InkStrokes,
    viewport: request.Viewport,
    messages: messages
  };
  return JSON.stringify(payload);
}

function tokenEvent(text: string): AssistantEvent {
  const event = emptyEvent(AssistantEventType.Token);
  event.Text = text;
  return event;
}

function formulaEvent(latex: string): AssistantEvent {
  const event = emptyEvent(AssistantEventType.Formula);
  event.Latex = latex;
  return event;
}

function stepEvent(index: number, text: string, latex: string): AssistantEvent {
  const event = emptyEvent(AssistantEventType.Step);
  event.Index = index;
  event.Text = text;
  event.Latex = latex;
  return event;
}

function shapeEvent(title: string, points: number[][]): AssistantEvent {
  const event = emptyEvent(AssistantEventType.ShapeAnnotation);
  event.Title = title;
  event.Points = points;
  return event;
}

// 本地 mock：覆盖公式转写、自动求解、图形释义、自由问答四条路径；
// 事件顺序与真实 SSE 一致，供流式渲染/中断/错误态自测。
export function buildMockScript(mode: number): AssistantEvent[] {
  const events: AssistantEvent[] = [];
  if (mode === AssistantMode.Formula) {
    events.push(tokenEvent('已识别到手写公式。\n\n'));
    events.push(formulaEvent('\\frac{1}{2}x^2 - 3x + 2 = 0'));
    events.push(tokenEvent('\n\n对照原迹：二次项系数为 $\\frac{1}{2}$，一次项为 $-3x$，常数项为 $2$。\n'));
    events.push(stepEvent(1, '两边乘以 2，消去分式系数', 'x^2 - 6x + 4 = 0'));
    events.push(stepEvent(2, '移项后配方', '(x-3)^2 = 5'));
    events.push(tokenEvent('\n可继续追问该式的判别式或图像。'));
  } else if (mode === AssistantMode.Solve) {
    events.push(tokenEvent('下面开始求解。\n\n'));
    events.push(stepEvent(1, '原方程为', 'x^2 - 5x + 6 = 0'));
    events.push(stepEvent(2, '因式分解', '(x-2)(x-3) = 0'));
    events.push(stepEvent(3, '因此', 'x_1 = 2,\\ x_2 = 3'));
    events.push(formulaEvent('x_1 = 2,\\ x_2 = 3'));
    events.push(tokenEvent('\n\n检验：把 $x=2$ 与 $x=3$ 代回原式均成立。'));
  } else if (mode === AssistantMode.Shape) {
    events.push(tokenEvent('识别到直角三角形。\n\n'));
    events.push(shapeEvent('直角三角形', [[120, 80], [280, 80], [120, 240], [120, 80]]));
    events.push(tokenEvent('已知两条直角边分别为 3 与 4，斜边为 5。\n\n'));
    events.push(tokenEvent('可推导性质：两锐角互余，且满足 $a^2+b^2=c^2$。'));
  } else {
    events.push(tokenEvent('这是框选内容的要点：\n\n'));
    events.push(tokenEvent('1. 先识别手写公式的结构与符号；\n'));
    events.push(tokenEvent('2. 再给出可验证的推导步骤；\n'));
    events.push(tokenEvent('3. 最后给出一句话结论。\n\n'));
    events.push(formulaEvent('E = mc^2'));
    events.push(tokenEvent('如需更精确的识别，可重新框选更清晰的区域。'));
  }
  events.push(emptyEvent(AssistantEventType.Done));
  return events;
}

class MockStream implements AssistantStream {
  private aborted: boolean = false;
  private timer: number = -1;
  private cursor: number = 0;
  private script: AssistantEvent[];
  private onEvent: AssistantEventSink;
  private onError: AssistantErrorSink;
  private onDone: AssistantDoneSink;

  constructor(
    mode: number,
    onEvent: AssistantEventSink,
    onError: AssistantErrorSink,
    onDone: AssistantDoneSink
  ) {
    this.script = buildMockScript(mode);
    this.onEvent = onEvent;
    this.onError = onError;
    this.onDone = onDone;
    this.timer = setTimeout(() => {
      this.tick();
    }, 60);
  }

  private tick(): void {
    if (this.aborted) {
      return;
    }
    if (this.cursor >= this.script.length) {
      this.onDone();
      return;
    }
    const event = this.script[this.cursor];
    this.cursor++;
    if (event.Type === AssistantEventType.Error) {
      this.onError(event.Text);
      return;
    }
    this.onEvent(event);
    if (event.Type === AssistantEventType.Done) {
      this.onDone();
      return;
    }
    this.timer = setTimeout(() => {
      this.tick();
    }, 45);
  }

  Abort(): void {
    this.aborted = true;
    if (this.timer >= 0) {
      clearTimeout(this.timer);
      this.timer = -1;
    }
  }
}

class HttpStream implements AssistantStream {
  private aborted: boolean = false;
  private request: http.HttpRequest | null = null;
  private timer: number = -1;
  private gotToken: boolean = false;
  private parser: SseParser = new SseParser();
  private decoder: util.TextDecoder = util.TextDecoder.create('utf-8', { ignoreBOM: true });
  private onEvent: AssistantEventSink;
  private onError: AssistantErrorSink;
  private onDone: AssistantDoneSink;

  constructor(
    config: AiConfig,
    request: AssistantRequest,
    onEvent: AssistantEventSink,
    onError: AssistantErrorSink,
    onDone: AssistantDoneSink
  ) {
    this.onEvent = onEvent;
    this.onError = onError;
    this.onDone = onDone;
    const timeoutMs = config.TimeoutMs > 0 ? config.TimeoutMs : AI_DEFAULT_TIMEOUT_MS;
    this.timer = setTimeout(() => {
      if (!this.aborted && !this.gotToken) {
        this.Abort();
        this.onError('首 token 超时（' + timeoutMs + 'ms）');
      }
    }, timeoutMs);

    const httpRequest = http.createHttp();
    this.request = httpRequest;
    httpRequest.on('dataReceive', (data: ArrayBuffer) => {
      if (this.aborted) {
        return;
      }
      try {
        const text = this.decoder.decodeWithStream(new Uint8Array(data), { stream: true });
        const events = this.parser.push(text);
        for (let i = 0; i < events.length; i++) {
          if (events[i].Type === AssistantEventType.Token) {
            this.gotToken = true;
          }
          this.onEvent(events[i]);
        }
      } catch (decodeError) {
        void decodeError;
      }
    });

    const body = buildPayload(request, config.Model);
    httpRequest.requestInStream(config.Endpoint, {
      method: http.RequestMethod.POST,
      header: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
      extraData: body,
      expectDataType: http.HttpDataType.STRING,
      connectTimeout: timeoutMs,
      readTimeout: timeoutMs * 4
    }).then((code: number) => {
      if (this.aborted) {
        return;
      }
      this.clearTimer();
      const rest = this.parser.flush();
      for (let i = 0; i < rest.length; i++) {
        if (rest[i].Type === AssistantEventType.Token) {
          this.gotToken = true;
        }
        this.onEvent(rest[i]);
      }
      this.dispose();
      if (code >= 200 && code < 300) {
        this.onDone();
      } else {
        this.gotToken = true;
        this.onError('HTTP ' + code);
      }
    }).catch((error: BusinessError) => {
      if (this.aborted) {
        return;
      }
      this.clearTimer();
      this.dispose();
      this.gotToken = true;
      this.onError(error.message.length > 0 ? error.message : '网络请求失败');
    });
  }

  private clearTimer(): void {
    if (this.timer >= 0) {
      clearTimeout(this.timer);
      this.timer = -1;
    }
  }

  private dispose(): void {
    if (this.request !== null) {
      try {
        this.request.destroy();
      } catch (destroyError) {
        void destroyError;
      }
      this.request = null;
    }
  }

  Abort(): void {
    this.aborted = true;
    this.clearTimer();
    this.dispose();
  }
}

export class AiClient {
  private config: AiConfig;

  constructor(config: AiConfig) {
    this.config = config;
  }

  Config(): AiConfig {
    return this.config;
  }

  start(
    request: AssistantRequest,
    onEvent: AssistantEventSink,
    onError: AssistantErrorSink,
    onDone: AssistantDoneSink
  ): AssistantStream {
    if (this.config.Endpoint.length === 0) {
      return new MockStream(request.Mode, onEvent, onError, onDone);
    }
    return new HttpStream(this.config, request, onEvent, onError, onDone);
  }
}