// @parity src/InkCanvas.App/Views/InfoWindow.cs（Markdown 子集；渐进解析与表格为新增）
// 渐进式 Markdown：未闭合代码块/公式不报错，先按已到达内容成块渲染。

export enum MdBlockKind {
  Paragraph = 0,
  Heading = 1,
  ListItem = 2,
  Code = 3,
  Table = 4,
  Quote = 5
}

export interface MdBlock {
  Kind: number;
  Level: number;
  Text: string;
  Rows: string[][];
  Complete: boolean;
}

export interface MarkdownProbe {
  BlockCount: number;
  OpenFence: boolean;
  IncompleteFormula: number;
  Plain: string;
}

function isTableRow(line: string): boolean {
  return line.indexOf('|') >= 0;
}

function isTableSeparator(line: string): boolean {
  const trimmed = line.trim();
  if (trimmed.length < 3) {
    return false;
  }
  for (let i = 0; i < trimmed.length; i++) {
    const ch = trimmed.charAt(i);
    if (ch !== '-' && ch !== '|' && ch !== ':' && ch !== ' ') {
      return false;
    }
  }
  return trimmed.indexOf('-') >= 0 && trimmed.indexOf('|') >= 0;
}

function splitCells(line: string): string[] {
  let body = line.trim();
  if (body.startsWith('|')) {
    body = body.substring(1);
  }
  if (body.endsWith('|')) {
    body = body.substring(0, body.length - 1);
  }
  const cells = body.split('|');
  const result: string[] = [];
  for (let i = 0; i < cells.length; i++) {
    result.push(cells[i].trim());
  }
  return result;
}

function countUnclosedDollar(text: string): number {
  let count = 0;
  let cursor = 0;
  while (cursor < text.length) {
    if (text.charAt(cursor) === '$') {
      if (cursor + 1 < text.length && text.charAt(cursor + 1) === '$') {
        cursor += 2;
      } else {
        cursor += 1;
      }
      count++;
      continue;
    }
    cursor++;
  }
  return count % 2;
}

function appendParagraph(blocks: MdBlock[], lines: string[]): void {
  if (lines.length === 0) {
    return;
  }
  blocks.push({ Kind: MdBlockKind.Paragraph, Level: 0, Text: lines.join('\n'), Rows: [], Complete: true });
}

export function parseMarkdownProgressive(text: string): MdBlock[] {
  const blocks: MdBlock[] = [];
  const lines = text.split('\n');
  let paragraph: string[] = [];
  let inCode = false;
  let codeLines: string[] = [];
  let index = 0;
  while (index < lines.length) {
    const line = lines[index];
    const trimmed = line.trim();

    if (trimmed.startsWith('```')) {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      if (inCode) {
        blocks.push({ Kind: MdBlockKind.Code, Level: 0, Text: codeLines.join('\n'), Rows: [], Complete: true });
        codeLines = [];
        inCode = false;
      } else {
        inCode = true;
      }
      index++;
      continue;
    }
    if (inCode) {
      codeLines.push(line);
      index++;
      continue;
    }

    if (trimmed.length === 0) {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      index++;
      continue;
    }

    let headingLevel = 0;
    while (headingLevel < trimmed.length && trimmed.charAt(headingLevel) === '#') {
      headingLevel++;
    }
    if (headingLevel > 0 && headingLevel <= 6 && trimmed.charAt(headingLevel) === ' ') {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      blocks.push({
        Kind: MdBlockKind.Heading,
        Level: headingLevel,
        Text: trimmed.substring(headingLevel + 1),
        Rows: [],
        Complete: true
      });
      index++;
      continue;
    }

    if (trimmed.startsWith('- ') || trimmed.startsWith('* ')) {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      blocks.push({ Kind: MdBlockKind.ListItem, Level: 0, Text: trimmed.substring(2), Rows: [], Complete: true });
      index++;
      continue;
    }

    let digits = 0;
    while (digits < trimmed.length && trimmed.charAt(digits) >= '0' && trimmed.charAt(digits) <= '9') {
      digits++;
    }
    if (digits > 0 && digits < trimmed.length && trimmed.charAt(digits) === '.' && trimmed.charAt(digits + 1) === ' ') {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      blocks.push({
        Kind: MdBlockKind.ListItem,
        Level: Number(trimmed.substring(0, digits)),
        Text: trimmed.substring(digits + 2),
        Rows: [],
        Complete: true
      });
      index++;
      continue;
    }

    if (trimmed.startsWith('> ')) {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      blocks.push({ Kind: MdBlockKind.Quote, Level: 0, Text: trimmed.substring(2), Rows: [], Complete: true });
      index++;
      continue;
    }

    if (isTableRow(trimmed) && index + 1 < lines.length && isTableSeparator(lines[index + 1])) {
      appendParagraph(blocks, paragraph);
      paragraph = [];
      const rows: string[][] = [];
      rows.push(splitCells(trimmed));
      index += 2;
      while (index < lines.length && isTableRow(lines[index].trim()) && lines[index].trim().length > 0) {
        rows.push(splitCells(lines[index]));
        index++;
      }
      blocks.push({ Kind: MdBlockKind.Table, Level: 0, Text: '', Rows: rows, Complete: true });
      continue;
    }

    paragraph.push(line);
    index++;
  }

  if (inCode) {
    blocks.push({ Kind: MdBlockKind.Code, Level: 0, Text: codeLines.join('\n'), Rows: [], Complete: false });
  }
  appendParagraph(blocks, paragraph);
  return blocks;
}

export function markdownProbe(text: string): MarkdownProbe {
  const blocks = parseMarkdownProgressive(text);
  let openFence = false;
  const parts: string[] = [];
  for (let i = 0; i < blocks.length; i++) {
    if (!blocks[i].Complete) {
      openFence = true;
    }
    if (blocks[i].Kind === MdBlockKind.Table) {
      for (let r = 0; r < blocks[i].Rows.length; r++) {
        parts.push(blocks[i].Rows[r].join(' | '));
      }
    } else {
      parts.push(blocks[i].Text);
    }
  }
  return {
    BlockCount: blocks.length,
    OpenFence: openFence,
    IncompleteFormula: countUnclosedDollar(text),
    Plain: parts.join('\n')
  };
}