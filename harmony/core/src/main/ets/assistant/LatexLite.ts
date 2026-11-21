// @parity src/InkCanvas.App/Views/InfoWindow.cs（Markdown 子集渲染的等价实现，LaTeX 为新增）
// 渐进式公式解析：容忍未闭合 $ / \frac 等半成输入；不能排版时降级为纯文本，不抛错、不闪烁。

export enum FormulaKind {
  Text = 0,
  Inline = 1,
  Block = 2
}

export interface FormulaSegment {
  Kind: number;
  Text: string;
  Complete: boolean;
  Raw: string;
}

export interface FormulaProbe {
  SegmentCount: number;
  Incomplete: number;
  Plain: string;
}

const GREEK: string[][] = [
  ['\\alpha', 'α'], ['\\beta', 'β'], ['\\gamma', 'γ'], ['\\delta', 'δ'],
  ['\\epsilon', 'ε'], ['\\theta', 'θ'], ['\\lambda', 'λ'], ['\\mu', 'μ'],
  ['\\pi', 'π'], ['\\rho', 'ρ'], ['\\sigma', 'σ'], ['\\phi', 'φ'],
  ['\\omega', 'ω'], ['\\Delta', 'Δ'], ['\\Sigma', 'Σ'], ['\\Omega', 'Ω']
];

const OPERATORS: string[][] = [
  ['\\times', '×'], ['\\cdot', '·'], ['\\div', '÷'], ['\\pm', '±'],
  ['\\le', '≤'], ['\\leq', '≤'], ['\\ge', '≥'], ['\\geq', '≥'],
  ['\\neq', '≠'], ['\\approx', '≈'], ['\\infty', '∞'], ['\\degree', '°'],
  ['\\int', '∫'], ['\\iint', '∬'], ['\\sum', 'Σ'], ['\\prod', '∏'],
  ['\\rightarrow', '→'], ['\\to', '→'], ['\\left', ''], ['\\right', '']
];

function replaceAllSimple(text: string, pairs: string[][]): string {
  let result = text;
  for (let i = 0; i < pairs.length; i++) {
    result = result.split(pairs[i][0]).join(pairs[i][1]);
  }
  return result;
}

// 找到与 openIndex 处 '{' 匹配的 '}'；未闭合返回 -1。
function matchBrace(text: string, openIndex: number): number {
  let depth = 0;
  for (let i = openIndex; i < text.length; i++) {
    const ch = text.charAt(i);
    if (ch === '{') {
      depth++;
    } else if (ch === '}') {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }
  return -1;
}

function unwrapBraces(text: string): string {
  const trimmed = text.trim();
  if (trimmed.length >= 2 && trimmed.charAt(0) === '{' && trimmed.charAt(trimmed.length - 1) === '}') {
    return trimmed.substring(1, trimmed.length - 1);
  }
  return trimmed;
}

// 把 LaTeX 子集转成可读纯文本；结构完整时保持可辨识的层级（分式加括号、根号用 √）。
export function latexToPlain(latex: string): string {
  let text = latex;
  let guard = 0;
  while (guard < 32) {
    const fracIndex = text.indexOf('\\frac');
    if (fracIndex < 0) {
      break;
    }
    let cursor = fracIndex + 5;
    while (cursor < text.length && text.charAt(cursor) === ' ') {
      cursor++;
    }
    if (cursor >= text.length || text.charAt(cursor) !== '{') {
      break;
    }
    const numEnd = matchBrace(text, cursor);
    if (numEnd < 0) {
      break;
    }
    const numerator = text.substring(cursor + 1, numEnd);
    let denStart = numEnd + 1;
    while (denStart < text.length && text.charAt(denStart) === ' ') {
      denStart++;
    }
    if (denStart >= text.length || text.charAt(denStart) !== '{') {
      break;
    }
    const denEnd = matchBrace(text, denStart);
    if (denEnd < 0) {
      break;
    }
    const denominator = text.substring(denStart + 1, denEnd);
    const plainNum = latexToPlain(unwrapBraces(numerator));
    const plainDen = latexToPlain(unwrapBraces(denominator));
    const replacement = '(' + plainNum + ')/(' + plainDen + ')';
    text = text.substring(0, fracIndex) + replacement + text.substring(denEnd + 1);
    guard++;
  }

  let guardSqrt = 0;
  while (guardSqrt < 32) {
    const sqrtIndex = text.indexOf('\\sqrt');
    if (sqrtIndex < 0) {
      break;
    }
    let cursor = sqrtIndex + 5;
    while (cursor < text.length && text.charAt(cursor) === ' ') {
      cursor++;
    }
    if (cursor >= text.length || text.charAt(cursor) !== '{') {
      break;
    }
    const end = matchBrace(text, cursor);
    if (end < 0) {
      break;
    }
    const body = latexToPlain(unwrapBraces(text.substring(cursor + 1, end)));
    text = text.substring(0, sqrtIndex) + '√(' + body + ')' + text.substring(end + 1);
    guardSqrt++;
  }

  text = text.split('\\\\').join('\n');
  text = text.split('&').join(' ');
  text = replaceAllSimple(text, OPERATORS);
  text = replaceAllSimple(text, GREEK);
  text = text.split('^{').join('^');
  text = text.split('_{').join('_');
  text = text.split('}').join('');
  text = text.split('{').join('');
  text = text.split('\\,').join(' ');
  text = text.split('\\;').join(' ');
  text = text.split('\\!').join('');
  text = text.split('$').join('');
  return text.trim();
}

// 公式是否可交给排版层：括号/花括号平衡且没有孤立的排版命令。
export function latexIsRenderable(latex: string): boolean {
  if (latex.trim().length === 0) {
    return false;
  }
  let braces = 0;
  for (let i = 0; i < latex.length; i++) {
    const ch = latex.charAt(i);
    if (ch === '{') {
      braces++;
    } else if (ch === '}') {
      braces--;
      if (braces < 0) {
        return false;
      }
    }
  }
  if (braces !== 0) {
    return false;
  }
  const dangling = ['\\frac', '\\sqrt', '\\sum', '\\int'];
  for (let i = 0; i < dangling.length; i++) {
    const index = latex.indexOf(dangling[i]);
    if (index >= 0 && index + dangling[i].length >= latex.length) {
      return false;
    }
  }
  return true;
}

// 把整段回答切成文本/行内公式/块级公式；未闭合的公式完整标记为 Complete=false。
export function splitFormulas(text: string): FormulaSegment[] {
  const segments: FormulaSegment[] = [];
  let cursor = 0;
  let textStart = 0;
  while (cursor < text.length) {
    const ch = text.charAt(cursor);
    if (ch !== '$') {
      cursor++;
      continue;
    }
    const isBlock = cursor + 1 < text.length && text.charAt(cursor + 1) === '$';
    const marker = isBlock ? '$$' : '$';
    const openAt = cursor;
    const contentStart = cursor + marker.length;
    const closeAt = text.indexOf(marker, contentStart);
    if (textStart < openAt) {
      segments.push({ Kind: FormulaKind.Text, Text: text.substring(textStart, openAt), Complete: true, Raw: '' });
    }
    if (closeAt < 0) {
      segments.push({
        Kind: isBlock ? FormulaKind.Block : FormulaKind.Inline,
        Text: latexToPlain(text.substring(contentStart)),
        Complete: false,
        Raw: text.substring(contentStart)
      });
      textStart = text.length;
      cursor = text.length;
      break;
    }
    const raw = text.substring(contentStart, closeAt);
    segments.push({
      Kind: isBlock ? FormulaKind.Block : FormulaKind.Inline,
      Text: latexToPlain(raw),
      Complete: latexIsRenderable(raw),
      Raw: raw
    });
    cursor = closeAt + marker.length;
    textStart = cursor;
  }
  if (textStart < text.length) {
    segments.push({ Kind: FormulaKind.Text, Text: text.substring(textStart), Complete: true, Raw: '' });
  }
  return segments;
}

export function formulaProbe(text: string): FormulaProbe {
  const segments = splitFormulas(text);
  let incomplete = 0;
  const parts: string[] = [];
  for (let i = 0; i < segments.length; i++) {
    if (!segments[i].Complete) {
      incomplete++;
    }
    parts.push(segments[i].Text);
  }
  return {
    SegmentCount: segments.length,
    Incomplete: incomplete,
    Plain: parts.join('')
  };
}