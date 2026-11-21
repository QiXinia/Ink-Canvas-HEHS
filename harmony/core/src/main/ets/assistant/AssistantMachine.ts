// @parity src/InkCanvas.App/Services/ShellController.cs（AI 会话状态机为鸿蒙侧新增）
// 显式状态机：idle → capturing → recognizing → streaming → done / error。
// 纯函数式 reducer：每次返回新会话，便于 UI 层与测试断言。
import { AssistantEventType, AssistantRole, AssistantState } from './AssistantTypes';
import type { AssistantEvent, AssistantMessage, AssistantStep } from './AssistantTypes';

export interface AssistantSession {
  State: number;
  Mode: number;
  SessionId: string;
  Text: string;
  Messages: AssistantMessage[];
  Steps: AssistantStep[];
  Formula: string;
  Annotations: number[][][];
  Error: string;
  TokenCount: number;
  StartedAt: number;
  FirstTokenAt: number;
}

function copySession(source: AssistantSession): AssistantSession {
  const messages: AssistantMessage[] = [];
  for (let i = 0; i < source.Messages.length; i++) {
    messages.push({
      Role: source.Messages[i].Role,
      Text: source.Messages[i].Text,
      Complete: source.Messages[i].Complete
    });
  }
  const steps: AssistantStep[] = [];
  for (let i = 0; i < source.Steps.length; i++) {
    steps.push({
      Index: source.Steps[i].Index,
      Text: source.Steps[i].Text,
      Latex: source.Steps[i].Latex
    });
  }
  const annotations: number[][][] = [];
  for (let i = 0; i < source.Annotations.length; i++) {
    annotations.push(source.Annotations[i].slice());
  }
  return {
    State: source.State,
    Mode: source.Mode,
    SessionId: source.SessionId,
    Text: source.Text,
    Messages: messages,
    Steps: steps,
    Formula: source.Formula,
    Annotations: annotations,
    Error: source.Error,
    TokenCount: source.TokenCount,
    StartedAt: source.StartedAt,
    FirstTokenAt: source.FirstTokenAt
  };
}

export function createSession(mode: number, sessionId: string): AssistantSession {
  return {
    State: AssistantState.Idle,
    Mode: mode,
    SessionId: sessionId,
    Text: '',
    Messages: [],
    Steps: [],
    Formula: '',
    Annotations: [],
    Error: '',
    TokenCount: 0,
    StartedAt: 0,
    FirstTokenAt: 0
  };
}

export function beginCapture(session: AssistantSession, nowMs: number): AssistantSession {
  const next = copySession(session);
  next.State = AssistantState.Capturing;
  next.StartedAt = nowMs;
  next.Error = '';
  return next;
}

export function beginRecognize(session: AssistantSession): AssistantSession {
  const next = copySession(session);
  next.State = AssistantState.Recognizing;
  next.Text = '';
  next.Formula = '';
  next.Steps = [];
  next.Annotations = [];
  next.Error = '';
  next.TokenCount = 0;
  next.FirstTokenAt = 0;
  return next;
}

export function beginStream(session: AssistantSession): AssistantSession {
  const next = copySession(session);
  next.State = AssistantState.Streaming;
  return next;
}

export function applyEvent(session: AssistantSession, event: AssistantEvent, nowMs: number = 0): AssistantSession {
  const next = copySession(session);
  if (next.State === AssistantState.Done || next.State === AssistantState.Error) {
    return next;
  }
  if (event.Type === AssistantEventType.Token) {
    if (next.State === AssistantState.Recognizing || next.State === AssistantState.Capturing) {
      next.State = AssistantState.Streaming;
    }
    if (next.FirstTokenAt === 0) {
      next.FirstTokenAt = nowMs;
    }
    if (event.Text.length > 0) {
      next.Text += event.Text;
      next.TokenCount++;
    }
    return next;
  }
  if (event.Type === AssistantEventType.Formula) {
    if (next.State === AssistantState.Capturing || next.State === AssistantState.Recognizing) {
      next.State = AssistantState.Streaming;
    }
    next.Formula = event.Latex;
    return next;
  }
  if (event.Type === AssistantEventType.Step) {
    if (next.State === AssistantState.Capturing || next.State === AssistantState.Recognizing) {
      next.State = AssistantState.Streaming;
    }
    next.Steps.push({ Index: event.Index, Text: event.Text, Latex: event.Latex });
    return next;
  }
  if (event.Type === AssistantEventType.ShapeAnnotation) {
    if (next.State === AssistantState.Capturing || next.State === AssistantState.Recognizing) {
      next.State = AssistantState.Streaming;
    }
    next.Annotations.push(event.Points);
    return next;
  }
  if (event.Type === AssistantEventType.Error) {
    next.State = AssistantState.Error;
    next.Error = event.Text;
    return next;
  }
  if (event.Type === AssistantEventType.Done) {
    next.State = AssistantState.Done;
    return next;
  }
  return next;
}

export function reduceEvents(session: AssistantSession, events: AssistantEvent[], startMs: number = 0): AssistantSession {
  let current = session;
  let clock = startMs;
  for (let i = 0; i < events.length; i++) {
    current = applyEvent(current, events[i], clock);
    clock += 1;
  }
  return current;
}

export function finish(session: AssistantSession): AssistantSession {
  const next = copySession(session);
  next.State = AssistantState.Done;
  return next;
}

export function fail(session: AssistantSession, message: string): AssistantSession {
  const next = copySession(session);
  next.State = AssistantState.Error;
  next.Error = message;
  return next;
}

// 首个 token 超时：从进入 recognizing 起算；未收到 token 且超过 timeout 即失败。
export function firstTokenTimedOut(session: AssistantSession, nowMs: number, timeoutMs: number): boolean {
  if (session.State !== AssistantState.Recognizing && session.State !== AssistantState.Capturing) {
    return false;
  }
  if (session.FirstTokenAt > 0) {
    return false;
  }
  return session.StartedAt > 0 && nowMs - session.StartedAt >= timeoutMs;
}

export function canRetry(session: AssistantSession): boolean {
  return session.State === AssistantState.Error;
}

// 用户追问：把当前回答标记完成，追加一条用户消息并回到 recognizing。
export function beginFollowUp(session: AssistantSession, question: string, nowMs: number): AssistantSession {
  const next = copySession(session);
  if (next.Text.length > 0) {
    next.Messages.push({ Role: AssistantRole.Assistant, Text: next.Text, Complete: true });
  }
  next.Messages.push({ Role: AssistantRole.User, Text: question, Complete: true });
  next.Text = '';
  next.Formula = '';
  next.Steps = [];
  next.Annotations = [];
  next.Error = '';
  next.TokenCount = 0;
  next.FirstTokenAt = 0;
  next.StartedAt = nowMs;
  next.State = AssistantState.Recognizing;
  return next;
}

export interface MachineProbe {
  State: number;
  StateName: string;
  Text: string;
  Steps: number;
  Formula: string;
  Annotations: number;
  Retry: boolean;
}

export function machineProbe(
  mode: number,
  events: AssistantEvent[],
  timeoutMs: number,
  nowMs: number
): MachineProbe {
  const started = beginCapture(createSession(mode, 'probe'), 1000);
  const recognized = beginRecognize(started);
  const reduced = reduceEvents(recognized, events, 2000);
  return {
    State: reduced.State,
    StateName: assistantStateName(reduced.State),
    Text: reduced.Text,
    Steps: reduced.Steps.length,
    Formula: reduced.Formula,
    Annotations: reduced.Annotations.length,
    Retry: canRetry(reduced) || firstTokenTimedOut(reduced, nowMs, timeoutMs)
  };
}

export function assistantStateName(state: number): string {
  if (state === AssistantState.Capturing) {
    return 'capturing';
  }
  if (state === AssistantState.Recognizing) {
    return 'recognizing';
  }
  if (state === AssistantState.Streaming) {
    return 'streaming';
  }
  if (state === AssistantState.Done) {
    return 'done';
  }
  if (state === AssistantState.Error) {
    return 'error';
  }
  return 'idle';
}