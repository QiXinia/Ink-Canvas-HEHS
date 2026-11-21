// @parity src/InkCanvas.Features/Update/UpdateService.cs（HTTP 流读取；SSE 解析为新增）
// Server-Sent Events 增量解析：兼容 OpenAI Chat Completions 的 delta 与自定义事件协议。
// 纯文本输入、纯事件输出，不含网络代码；platform 层负责把网络分块喂进来。
import { AssistantEventType, emptyEvent } from './AssistantTypes';
import type { AssistantEvent } from './AssistantTypes';

export interface SseParseResult {
  Events: AssistantEvent[];
  Carry: string;
}

interface OpenAiDelta {
  content?: string;
}

interface OpenAiChoice {
  delta?: OpenAiDelta;
  text?: string;
}

interface OpenAiErrorInfo {
  message?: string;
}

interface OpenAiChunk {
  choices?: OpenAiChoice[];
  error?: OpenAiErrorInfo;
}

interface AssistantEnvelope {
  type?: string;
  text?: string;
  latex?: string;
  index?: number;
  title?: string;
  points?: number[][];
}

function parseJson(text: string): Object | null {
  try {
    return JSON.parse(text) as Object;
  } catch (e) {
    return null;
  }
}

export function extractOpenAiDelta(data: string): string {
  const parsed = parseJson(data);
  if (parsed === null) {
    return '';
  }
  const chunk = parsed as OpenAiChunk;
  if (chunk.choices !== undefined && chunk.choices.length > 0) {
    const choice = chunk.choices[0];
    if (choice.delta !== undefined && choice.delta.content !== undefined) {
      return choice.delta.content;
    }
    if (choice.text !== undefined) {
      return choice.text;
    }
  }
  return '';
}

export function sseEventFrom(name: string, data: string): AssistantEvent {
  const trimmed = data.trim();
  if (trimmed.length === 0) {
    return emptyEvent(AssistantEventType.Done);
  }
  if (trimmed === '[DONE]') {
    return emptyEvent(AssistantEventType.Done);
  }
  const parsed = parseJson(trimmed);
  if (parsed !== null) {
    const envelope = parsed as AssistantEnvelope;
    if (envelope.type !== undefined) {
      const typed = envelopeToEvent(envelope);
      if (typed !== null) {
        return typed;
      }
    }
    if (name === 'formula' && envelope.latex !== undefined) {
      const formulaEvent = emptyEvent(AssistantEventType.Formula);
      formulaEvent.Latex = envelope.latex;
      return formulaEvent;
    }
    if (name === 'step' && (envelope.text !== undefined || envelope.latex !== undefined)) {
      const stepEvent = emptyEvent(AssistantEventType.Step);
      stepEvent.Index = envelope.index !== undefined ? envelope.index : 0;
      stepEvent.Text = envelope.text !== undefined ? envelope.text : '';
      stepEvent.Latex = envelope.latex !== undefined ? envelope.latex : '';
      return stepEvent;
    }
    if (name === 'shape_annotation' && envelope.points !== undefined) {
      const shapeEvent = emptyEvent(AssistantEventType.ShapeAnnotation);
      shapeEvent.Title = envelope.title !== undefined ? envelope.title : '';
      shapeEvent.Points = envelope.points;
      return shapeEvent;
    }
    const errorChunk = parsed as OpenAiChunk;
    if (errorChunk.error !== undefined) {
      const errorEvent = emptyEvent(AssistantEventType.Error);
      errorEvent.Text = errorChunk.error.message !== undefined ? errorChunk.error.message : '服务返回错误';
      return errorEvent;
    }
  }
  if (name === '' || name === 'message' || name === 'token') {
    const delta = extractOpenAiDelta(trimmed);
    if (delta.length > 0) {
      const tokenEvent = emptyEvent(AssistantEventType.Token);
      tokenEvent.Text = delta;
      return tokenEvent;
    }
    if (parsed !== null) {
      const envelope = parsed as AssistantEnvelope;
      if (envelope.text !== undefined) {
        const tokenEvent = emptyEvent(AssistantEventType.Token);
        tokenEvent.Text = envelope.text;
        return tokenEvent;
      }
    }
    const plain = emptyEvent(AssistantEventType.Token);
    plain.Text = trimmed;
    return plain;
  }
  if (name === 'formula') {
    const formulaEvent = emptyEvent(AssistantEventType.Formula);
    formulaEvent.Latex = trimmed;
    return formulaEvent;
  }
  if (name === 'step') {
    const stepEvent = emptyEvent(AssistantEventType.Step);
    stepEvent.Text = trimmed;
    return stepEvent;
  }
  if (name === 'shape_annotation') {
    const shapeEvent = emptyEvent(AssistantEventType.ShapeAnnotation);
    return shapeEvent;
  }
  if (name === 'error') {
    const errorEvent = emptyEvent(AssistantEventType.Error);
    errorEvent.Text = trimmed;
    return errorEvent;
  }
  if (name === 'done') {
    return emptyEvent(AssistantEventType.Done);
  }
  const fallback = emptyEvent(AssistantEventType.Token);
  fallback.Text = trimmed;
  return fallback;
}

function envelopeToEvent(envelope: AssistantEnvelope): AssistantEvent | null {
  if (envelope.type === undefined) {
    return null;
  }
  if (envelope.type === 'token') {
    const tokenEvent = emptyEvent(AssistantEventType.Token);
    tokenEvent.Text = envelope.text !== undefined ? envelope.text : '';
    return tokenEvent;
  }
  if (envelope.type === 'formula') {
    const formulaEvent = emptyEvent(AssistantEventType.Formula);
    formulaEvent.Latex = envelope.latex !== undefined ? envelope.latex : '';
    return formulaEvent;
  }
  if (envelope.type === 'step') {
    const stepEvent = emptyEvent(AssistantEventType.Step);
    stepEvent.Index = envelope.index !== undefined ? envelope.index : 0;
    stepEvent.Text = envelope.text !== undefined ? envelope.text : '';
    stepEvent.Latex = envelope.latex !== undefined ? envelope.latex : '';
    return stepEvent;
  }
  if (envelope.type === 'shape_annotation') {
    const shapeEvent = emptyEvent(AssistantEventType.ShapeAnnotation);
    shapeEvent.Title = envelope.title !== undefined ? envelope.title : '';
    shapeEvent.Points = envelope.points !== undefined ? envelope.points : [];
    return shapeEvent;
  }
  if (envelope.type === 'done') {
    return emptyEvent(AssistantEventType.Done);
  }
  if (envelope.type === 'error') {
    const errorEvent = emptyEvent(AssistantEventType.Error);
    errorEvent.Text = envelope.text !== undefined ? envelope.text : '服务返回错误';
    return errorEvent;
  }
  return null;
}

export class SseParser {
  private carry: string = '';
  private eventName: string = '';
  private dataLines: string[] = [];

  push(chunk: string): AssistantEvent[] {
    this.carry += chunk;
    const events: AssistantEvent[] = [];
    let newline = this.carry.indexOf('\n');
    while (newline >= 0) {
      let line = this.carry.substring(0, newline);
      this.carry = this.carry.substring(newline + 1);
      if (line.endsWith('\r')) {
        line = line.substring(0, line.length - 1);
      }
      this.consumeLine(line, events);
      newline = this.carry.indexOf('\n');
    }
    return events;
  }

  get Carry(): string {
    return this.carry;
  }

  flush(): AssistantEvent[] {
    const events: AssistantEvent[] = [];
    if (this.carry.length > 0) {
      const line = this.carry.endsWith('\r') ? this.carry.substring(0, this.carry.length - 1) : this.carry;
      this.carry = '';
      this.consumeLine(line, events);
    }
    this.dispatch(events);
    return events;
  }

  private consumeLine(line: string, events: AssistantEvent[]): void {
    if (line.length === 0) {
      this.dispatch(events);
      return;
    }
    if (line.startsWith(':')) {
      return;
    }
    const colon = line.indexOf(':');
    const field = colon >= 0 ? line.substring(0, colon) : line;
    let value = colon >= 0 ? line.substring(colon + 1) : '';
    if (value.startsWith(' ')) {
      value = value.substring(1);
    }
    if (field === 'event') {
      this.eventName = value;
    } else if (field === 'data') {
      this.dataLines.push(value);
    }
  }

  private dispatch(events: AssistantEvent[]): void {
    if (this.dataLines.length === 0 && this.eventName.length === 0) {
      return;
    }
    const data = this.dataLines.join('\n');
    const name = this.eventName;
    this.dataLines = [];
    this.eventName = '';
    if (data.length === 0 && name.length === 0) {
      return;
    }
    events.push(sseEventFrom(name, data));
  }
}

export function parseSseChunk(chunk: string, carry: string): SseParseResult {
  const parser = new SseParser();
  const events = parser.push(carry + chunk);
  return { Events: events, Carry: parser.Carry };
}