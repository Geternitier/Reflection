"""Runs the real WPF app against deterministic loopback HTTP fixtures."""
import http.server, json, os, pathlib, subprocess, sys, threading, time, uuid
root = pathlib.Path(__file__).resolve().parents[1]
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def handle(self):
        try: super().handle()
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError): pass
    def do_GET(self):
        payload = {"models": [{"name": "fixture-model"}]} if self.path == '/api/tags' else {"data": [{"id": "fixture-model"}]}
        body = json.dumps(payload).encode()
        self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(body)
    def do_POST(self):
        if self.path == '/api/show':
            length = int(self.headers.get('Content-Length') or 0)
            if length: self.rfile.read(length)
            body = json.dumps({"system": "fixture", "capabilities": ["completion"], "parameters": "temperature 0.7\nnum_predict 2048\n"}).encode()
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(body)
            return
        if self.headers.get('Transfer-Encoding') == 'chunked':
            chunks = []
            while True:
                size = int(self.rfile.readline().strip(), 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size)); self.rfile.read(2)
            payload = json.loads(b''.join(chunks))
        else:
            payload = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        assert payload['messages'][-1]['role'] == 'user'
        if payload['messages'][-1]['content'] == '请从刚才停下的地方继续写，不要重复已经写过的内容，也不要另起说明。':
            assert any(m['role'] == 'assistant' and '你好' in m['content'] for m in payload['messages']), 'Continuation lost original answer' 
        if payload['messages'][-1]['content'] == 'character update check':
            assert 'updated rules' in payload['messages'][0]['content'] and 'updated setting' in payload['messages'][0]['content']
            assert payload['messages'][1]['role'] == 'assistant' and payload['messages'][1]['content'] == 'original opening'
            assert not any(m['content'] == 'replacement opening' for m in payload['messages'])
        self.send_response(200); self.send_header('Content-Type', 'application/x-ndjson' if self.path == '/api/chat' else 'text/event-stream'); self.end_headers()
        if isinstance(payload['messages'][-1]['content'], str) and payload['messages'][-1]['content'].startswith('streaming lifecycle'):
            for thinking, content in [('正在分析问题。', ''), ('检查推导过程。', ''), ('', '# 实时标题\n\n'), ('', '这是 **即时粗体** 和*斜体*。\n\n'), ('', '- 列表项\n\n'), ('', '完成。')]:
                if self.path == '/api/chat': line = json.dumps({'message': {'content': content, 'thinking': thinking}, 'done': False}, ensure_ascii=False) + '\n'
                else: line = 'data: ' + json.dumps({'choices': [{'delta': {'content': content, 'reasoning_content': thinking}}]}, ensure_ascii=False) + '\n\n'
                self.wfile.write(line.encode()); self.wfile.flush(); time.sleep(.25)
            self.wfile.write(b'{"done":true}\n' if self.path == '/api/chat' else b'data: [DONE]\n\n'); self.wfile.flush()
            return
        for word in ['你好！', '我可以帮助你**梳理思路**、解释知识、编写代码，', '也可以和你一起打磨故事。\n\n', '### 从一个小例子开始\n```python\nprint("Hello, Reflection!")\n```\n告诉我你正在思考的问题，我们一起从第一步开始。']:
            if self.path == '/api/chat': line = json.dumps({'message': {'content': word}, 'done': False}, ensure_ascii=False) + '\n'
            else: line = 'data: ' + json.dumps({'choices': [{'delta': {'content': word}}]}, ensure_ascii=False) + '\n\n'
            self.wfile.write(line.encode()); self.wfile.flush(); time.sleep(.6 if isinstance(payload['messages'][-1]['content'], str) and 'navigation slow' in payload['messages'][-1]['content'] else .06)
        self.wfile.write(b'{"done":true}\n' if self.path == '/api/chat' else b'data: [DONE]\n\n'); self.wfile.flush()
server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()

def run_tray():
    for disabled in (False, True):
        run = root / 'artifacts' / ('smoke-tray-' + str(uuid.uuid4())[:8])
        data = run / 'data'
        data.mkdir(parents=True)
        settings = {'services': [{'id': str(uuid.uuid4()), 'name': 'Tray fixture', 'baseUrl': f'http://127.0.0.1:{server.server_port}', 'protocol': 0, 'enabled': True}]}
        (data / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
        if disabled:
            (data / 'application.json').write_text('{"closeToTray": false}', encoding='utf-8')
        env = dict(os.environ, REFLECTION_DATA_DIR=str(data), REFLECTION_SMOKE_DIR=str(run))
        app = root / 'src/Reflection.Desktop/bin/Release/net10.0-windows/Reflection.Desktop.exe'
        args = ['--smoke', '--tray-only'] + (['--tray-disabled'] if disabled else [])
        result = subprocess.run([str(app), *args], env=env, timeout=45, creationflags=subprocess.CREATE_NO_WINDOW)
        error = run / 'smoke-error.txt'
        if result.returncode or error.exists():
            raise RuntimeError(error.read_text(encoding='utf-8') if error.exists() else str(result.returncode))
        if not disabled:
            chats = [json.loads(p.read_text(encoding='utf-8')) for p in (data / 'chats').glob('*.json')]
            assert any(c.get('draft') == 'preserve draft on tray exit failure' for c in chats), 'Tray exit retry did not persist the draft'
        print('tray', (run / 'smoke-result.txt').read_text(encoding='utf-8'), '\nResults:', run)

try:
    if '--tray-only' in sys.argv:
        run_tray()
        sys.exit(0)
    for name, protocol in [('ollama', 0), ('openai', 1), ('offline', 0), ('closing', 0)]:
        if '--workspace-only' in sys.argv:
            continue
        run = root / 'artifacts' / ('smoke-' + name + '-' + str(uuid.uuid4())[:8]); data = run / 'data'; data.mkdir(parents=True)
        settings = {'services': [{'id': str(uuid.uuid4()), 'name': 'Fixture ' + name, 'baseUrl': f'http://127.0.0.1:{server.server_port}', 'protocol': protocol, 'enabled': True}]}
        if name == 'offline': settings['services'] = []
        (data / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
        env = dict(os.environ, REFLECTION_DATA_DIR=str(data), REFLECTION_SMOKE_DIR=str(run))
        app = root / 'src/Reflection.Desktop/bin/Release/net10.0-windows/Reflection.Desktop.exe'
        result = subprocess.run([str(app), '--smoke'] + (['--smoke-chat'] if name != 'offline' else []) + (['--smoke-close'] if name == 'closing' else []), env=env, timeout=75, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode or (run / 'smoke-error.txt').exists():
            raise RuntimeError((run / 'smoke-error.txt').read_text(encoding='utf-8') if (run / 'smoke-error.txt').exists() else str(result.returncode))
        if name == 'closing':
            chats = [json.loads(p.read_text(encoding='utf-8')) for p in (data / 'chats').glob('*.json')]
            closing = next(c for c in chats if any(m['content'] == 'close during generation' for m in c['messages']))
            assert closing['messages'][-1]['stopped'] and not closing['messages'][-1]['incomplete'], 'Close did not persist stopped reply'
            background = next(c for c in chats if any(m['content'] == 'navigation slow background close' for m in c['messages']))
            assert background['messages'][-1]['stopped'] and not background['messages'][-1]['incomplete'], 'Close did not stop and persist background generation'
        print(name, (run / 'smoke-result.txt').read_text(encoding='utf-8'), '\nScreenshots:', run)
    run = root / 'artifacts' / ('smoke-workspace-' + str(uuid.uuid4())[:8]); data = run / 'data'; data.mkdir(parents=True)
    class SlowDiscovery(Handler):
        def do_GET(self):
            time.sleep(2)
            body = b'{"models":[]}'
            self.send_response(200); self.send_header('Content-Type', 'application/json'); self.end_headers(); self.wfile.write(body)
    slow = http.server.ThreadingHTTPServer(('127.0.0.1', 0), SlowDiscovery)
    threading.Thread(target=slow.serve_forever, daemon=True).start()
    settings = {'services': [{'id': str(uuid.uuid4()), 'name': 'Slow discovery', 'baseUrl': f'http://127.0.0.1:{slow.server_port}', 'protocol': 0, 'enabled': True}]}
    (data / 'settings.json').write_text(json.dumps(settings), encoding='utf-8')
    env = dict(os.environ, REFLECTION_DATA_DIR=str(data), REFLECTION_SMOKE_DIR=str(run))
    try:
        app = root / 'src/Reflection.Desktop/bin/Release/net10.0-windows/Reflection.Desktop.exe'
        result = subprocess.run([str(app), '--smoke', '--workspace-only', '--slow-discovery'], env=env, timeout=75, creationflags=subprocess.CREATE_NO_WINDOW)
    finally:
        slow.shutdown()
    if result.returncode or (run / 'smoke-error.txt').exists():
        raise RuntimeError((run / 'smoke-error.txt').read_text(encoding='utf-8') if (run / 'smoke-error.txt').exists() else str(result.returncode))
    print('workspace', (run / 'workspace-result.txt').read_text(encoding='utf-8'), '\nScreenshots:', run)
    if '--workspace-only' not in sys.argv:
        run_tray()
finally:
    server.shutdown()
