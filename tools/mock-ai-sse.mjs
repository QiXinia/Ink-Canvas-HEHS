// AI 助教 mock SSE 服务（零依赖，Node 标准库）。
// 用法：node tools/mock-ai-sse.mjs
//       MOCK_AI_PORT=8787 node tools/mock-ai-sse.mjs
// 端点：POST /v1/chat/completions（任意路径均可），请求体为客户端 6.1 契约 JSON。
// 真机联调时把 AI_ENDPOINT 设为 http://<宿主局域网IP>:8787/v1/chat/completions。
import { createServer } from 'node:http';

const PORT = Number(process.env.MOCK_AI_PORT || 8787);

const LATEX = {
  auto: '\\frac{1}{2}x^2 - 3x + 2 = 0',
  formula: '\\frac{1}{2}x^2 - 3x + 2 = 0',
  solve: 'x_1 = 2,\\ x_2 = 3',
  shape: 'a^2+b^2=c^2',
  chat: 'E = mc^2'
};

function scriptFor(mode) {
  const events = [];
  const token = (text) => events.push({ name: 'token', data: { type: 'token', text } });
  const formula = (latex) => events.push({ name: 'formula', data: { type: 'formula', latex } });
  const step = (index, text, latex) => events.push({ name: 'step', data: { type: 'step', index, text, latex } });
  const done = () => events.push({ name: 'done', data: { type: 'done' } });

  if (mode === 'formula') {
    token('已识别到手写公式。\n\n');
    formula(LATEX.formula);
    token('\n\n对照原迹：二次项系数为 ');
    token('$\\frac{1}{2}$，一次项为 $-3x$，常数项为 $2$。\n');
    step(1, '两边乘以 2，消去分式系数', 'x^2 - 6x + 4 = 0');
    step(2, '移项后配方', '(x-3)^2 = 5');
    token('\n可继续追问该式的判别式或图像。');
  } else if (mode === 'solve') {
    token('下面开始求解。\n\n');
    step(1, '原方程为', 'x^2 - 5x + 6 = 0');
    step(2, '因式分解', '(x-2)(x-3) = 0');
    step(3, '因此', 'x_1 = 2,\\ x_2 = 3');
    formula(LATEX.solve);
    token('\n\n检验：把 $x=2$ 与 $x=3$ 代回原式均成立。');
  } else if (mode === 'shape') {
    token('识别到直角三角形。\n\n');
    events.push({
      name: 'shape_annotation',
      data: { type: 'shape_annotation', title: '直角三角形', points: [[120, 80], [280, 80], [120, 240], [120, 80]] }
    });
    token('已知两条直角边分别为 3 与 4，斜边为 5。\n\n');
    token('可推导性质：两锐角互余，且满足 $a^2+b^2=c^2$。');
  } else {
    token('这是框选内容的要点：\n\n');
    token('1. 先识别手写公式的结构与符号；\n');
    token('2. 再给出可验证的推导步骤；\n');
    token('3. 最后给出一句话结论。\n\n');
    formula(LATEX.chat);
    token('如需更精确的识别，可重新框选更清晰的区域。');
  }
  done();
  return events;
}

function send(res, name, data) {
  res.write('event: ' + name + '\n');
  res.write('data: ' + JSON.stringify(data) + '\n\n');
}

createServer((req, res) => {
  if (req.method === 'GET' && req.url === '/health') {
    res.writeHead(200, { 'Content-Type': 'text/plain' });
    res.end('ok');
    return;
  }
  if (req.method === 'OPTIONS') {
    res.writeHead(204, {
      'Access-Control-Allow-Origin': '*',
      'Access-Control-Allow-Headers': 'Content-Type, Accept',
      'Access-Control-Allow-Methods': 'POST, OPTIONS'
    });
    res.end();
    return;
  }

  let body = '';
  req.on('data', (chunk) => {
    body += chunk;
  });
  req.on('end', () => {
    let mode = 'auto';
    try {
      const payload = JSON.parse(body);
      if (typeof payload.mode === 'string') {
        mode = payload.mode;
      }
    } catch (error) {
      void error;
    }

    // 错误态联调：mode=error 或请求头 x-mock-fail。
    if (mode === 'error' || req.headers['x-mock-fail'] === '1') {
      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        Connection: 'keep-alive',
        'Access-Control-Allow-Origin': '*'
      });
      send(res, 'error', { type: 'error', text: 'mock 服务故障（用于错误态自测）' });
      res.end();
      return;
    }

    res.writeHead(200, {
      'Content-Type': 'text/event-stream',
      'Cache-Control': 'no-cache',
      Connection: 'keep-alive',
      'Access-Control-Allow-Origin': '*'
    });
    const events = scriptFor(mode);
    let index = 0;
    let closed = false;
    res.on('close', () => {
      closed = true;
    });
    const tick = () => {
      if (closed) {
        return;
      }
      if (index >= events.length) {
        res.end();
        return;
      }
      const event = events[index];
      index += 1;
      send(res, event.name, event.data);
      setTimeout(tick, 60);
    };
    tick();
  });
}).listen(PORT, '127.0.0.1', () => {
  console.log('mock-ai-sse listening on http://127.0.0.1:' + PORT);
  console.log('POST /v1/chat/completions  (mode: auto|formula|solve|shape|chat|error)');
});