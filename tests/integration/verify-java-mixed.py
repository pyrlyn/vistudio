# Run from the repository root. Requires the separately installed official Kotlin LSP
# (path taken from Vistudio settings or KOTLIN_LSP) and the sample JDK in JAVA_HOME.
# Checks Java language features and one Kotlin+Java JVM debug session on samples/kotlin-java-jvm.
import json, os, pathlib, queue, re, socket, subprocess, sys, threading, time
pathlib.Path('artifacts').mkdir(exist_ok=True)
root = pathlib.Path('samples/kotlin-java-jvm').resolve()
settings = pathlib.Path(os.environ['LOCALAPPDATA']) / 'Vistudio/settings.xml'
server = os.environ.get('KOTLIN_LSP') or re.search(r'<LanguageServerPath>(.*?)</LanguageServerPath>', settings.read_text(encoding='utf-8-sig')).group(1)
log = open('artifacts/java-mixed-probe.jsonl', 'w', encoding='utf8')
lock = threading.Lock()
checks = []

def check(name, condition, detail=''):
    checks.append((name, bool(condition)))
    print(('PASS ' if condition else 'FAIL ') + name + ('' if condition else ' :: ' + str(detail)[:1500]), flush=True)

class Peer:
    def __init__(self, reader, writer, name):
        self.reader, self.writer, self.name = reader, writer, name
        self.q = queue.Queue(); self.seq = 0; self.pending = []
        threading.Thread(target=self.read, daemon=True).start()
    def read(self):
        try:
            while True:
                headers = {}
                while True:
                    line = self.reader.readline()
                    if not line: raise EOFError()
                    if line == b'\r\n': break
                    k, v = line.decode().split(':', 1); headers[k.lower()] = v.strip()
                msg = json.loads(self.reader.read(int(headers['content-length'])))
                with lock: log.write(json.dumps({'peer': self.name, 'in': msg}) + '\n'); log.flush()
                if self.name == 'lsp' and 'method' in msg and 'id' in msg:
                    result = [None for _ in msg['params']['items']] if msg['method'] == 'workspace/configuration' else None
                    self.send({'jsonrpc': '2.0', 'id': msg['id'], 'result': result})
                else: self.q.put(msg)
        except Exception as e: self.q.put({'read_error': str(e)})
    def send(self, msg):
        data = json.dumps(msg).encode()
        with lock:
            log.write(json.dumps({'peer': self.name, 'out': msg}) + '\n'); log.flush()
            self.writer.write(b'Content-Length: ' + str(len(data)).encode() + b'\r\n\r\n' + data); self.writer.flush()
    def request(self, method, args):
        self.seq += 1
        self.send({'jsonrpc': '2.0', 'id': self.seq, 'method': method, 'params': args} if self.name == 'lsp'
                  else {'seq': self.seq, 'type': 'request', 'command': method, 'arguments': args})
        return self.seq
    def wait(self, predicate, timeout=120):
        end = time.monotonic() + timeout
        while True:
            for i, m in enumerate(self.pending):
                if predicate(m): return self.pending.pop(i)
            m = self.q.get(timeout=max(0.1, end - time.monotonic()))
            if 'read_error' in m: raise RuntimeError(m)
            self.pending.append(m)
    def response(self, seq, timeout=120):
        result = self.wait(lambda m: m.get('id' if self.name == 'lsp' else 'request_seq') == seq, timeout)
        if 'error' in result or result.get('success') is False: raise RuntimeError(result)
        return result.get('result') if self.name == 'lsp' else result.get('body', {})
    def call(self, method, args, timeout=120): return self.response(self.request(method, args), timeout)

def position(path, line, needle, offset):
    text = path.read_text().splitlines()[line - 1]
    return {'line': line - 1, 'character': text.index(needle) + offset}

def labels(result):
    items = result.get('items', []) if isinstance(result, dict) else (result or [])
    return {re.sub(r'\(.*', '', x.get('filterText') or x['label']).strip() for x in items}

def targets(result):
    result = result if isinstance(result, list) else [result] if result else []
    return [pathlib.Path((x.get('uri') or x.get('targetUri'))).name for x in result]

kotlin_main = root / 'src/main/kotlin/mixed/MixedMain.kt'
ledger = root / 'src/main/java/mixed/Ledger.java'
java_entry = root / 'src/main/java/mixed/JavaEntry.java'
env = os.environ.copy(); env['IJ_JAVA_OPTIONS'] = '-Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NUL'
process = subprocess.Popen([server, '--stdio', '--system-path=' + os.environ['LOCALAPPDATA'] + '/Vistudio/kotlin-lsp-java-probe'],
                           stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open('artifacts/java-mixed-probe.stderr', 'w'), env=env)

def debug_session(lsp, entry, main_class, breakpoints):
    # Server 271+: no resolution request; the launch names the main class and its file, and the server finds the module.
    tool = lsp.call('workspace/executeCommand', {'command': 'intellij.java.buildTool', 'arguments': [{'uri': entry.as_uri()}]})
    check('build tool of ' + entry.name + ' is gradle', tool and tool.get('tool') == 'gradle', tool)
    port = lsp.call('workspace/executeCommand', {'command': 'start_debug_server', 'arguments': [root.as_uri()]})
    sock = socket.create_connection(('127.0.0.1', port)); sock.settimeout(None)
    dap = Peer(sock.makefile('rb'), sock.makefile('wb'), 'dap')
    dap.call('initialize', {'clientID': 'vistudio-java-test', 'adapterID': 'intellij_gradle', 'linesStartAt1': True, 'columnsStartAt1': True, 'pathFormat': 'path'})
    launch = dap.request('launch', {'type': 'intellij_gradle', 'tool': 'gradle', 'request': 'launch', 'name': main_class, 'mainClass': main_class, 'file': str(entry),
                                    'cwd': str(root), 'console': 'internalConsole', 'env': tool.get('env') or {}, 'toolArgs': ['--no-daemon', '--console=plain']})
    dap.wait(lambda m: m.get('event') == 'initialized')
    for file, lines in breakpoints.items():
        result = dap.call('setBreakpoints', {'source': {'path': str(file)}, 'breakpoints': [{'line': l} for l in lines]})
        check('breakpoints accepted in ' + file.name, len(result.get('breakpoints', [])) == len(lines), result)
    dap.call('configurationDone', {}); dap.response(launch, 300)
    return dap, sock

def stop(dap):
    stopped = dap.wait(lambda m: m.get('event') in ('stopped', 'exited', 'terminated'), 300)
    if stopped.get('event') != 'stopped': raise RuntimeError(stopped)
    thread = stopped['body']['threadId']
    frames = dap.call('stackTrace', {'threadId': thread})['stackFrames']
    return thread, frames

def top(frames): return (pathlib.Path(frames[0]['source']['path']).name if frames[0].get('source') else None, frames[0]['line'])

def locals_of(dap, frame):
    scopes = dap.call('scopes', {'frameId': frame['id']})['scopes']
    names = {}
    for scope in scopes[:1]:
        for v in dap.call('variables', {'variablesReference': scope['variablesReference']})['variables']: names[v['name']] = v['value']
    return names

def finish(dap, sock):
    exited = dap.wait(lambda m: m.get('event') == 'exited', 300)
    check('debuggee exits with 0', exited['body']['exitCode'] == 0, exited)
    try: dap.call('disconnect', {'terminateDebuggee': False}, 10)
    except Exception: pass
    sock.close()

try:
    lsp = Peer(process.stdout, process.stdin, 'lsp')
    init = lsp.call('initialize', {'processId': os.getpid(), 'rootUri': root.as_uri(), 'workspaceFolders': [{'uri': root.as_uri(), 'name': root.name}],
                                   'capabilities': {'workspace': {'configuration': True}, 'window': {'workDoneProgress': True},
                                                    'textDocument': {'completion': {'completionItem': {'snippetSupport': False}}}}})
    lsp.send({'jsonrpc': '2.0', 'method': 'initialized', 'params': {}})
    for path, language in ((kotlin_main, 'kotlin'), (ledger, 'java'), (java_entry, 'java')):
        lsp.send({'jsonrpc': '2.0', 'method': 'textDocument/didOpen', 'params': {'textDocument': {'uri': path.as_uri(), 'languageId': language, 'version': 1, 'text': path.read_text()}}})
    lsp.wait(lambda m: m.get('method') == 'intellij/importLog' and m.get('params', {}).get('succeeded'), 300)

    def settled(get, budget=20):
        # Java analysis can finish indexing after the Gradle import notification; poll a bounded time.
        end = time.monotonic() + budget
        while True:
            result = get()
            if result or time.monotonic() > end: return result
            time.sleep(2)
    def complete(path, line, needle, offset):
        return settled(lambda: labels(lsp.call('textDocument/completion', {'textDocument': {'uri': path.as_uri()}, 'position': position(path, line, needle, offset)})))
    def definition(path, line, needle, offset):
        return settled(lambda: targets(lsp.call('textDocument/definition', {'textDocument': {'uri': path.as_uri()}, 'position': position(path, line, needle, offset)})))

    names = complete(java_entry, 8, 'getOwner', 3)
    print('INFO Java completion in .java:', sorted(names)[:10], flush=True)
    names = complete(ledger, 25, 'doubleIt', 3)
    print('INFO Java->Kotlin completion in .java:', sorted(names)[:10], flush=True)
    names = complete(kotlin_main, 5, 'add', 1)
    check('Kotlin completion lists Java members (ledger.a|)', {'add', 'applyKotlinRule'} <= names, sorted(names)[:40])
    check('Kotlin -> Java definition', 'Ledger.java' in definition(kotlin_main, 4, 'Ledger', 1), definition(kotlin_main, 4, 'Ledger', 1))
    print('INFO Java -> Kotlin definition:', definition(ledger, 25, 'doubleIt', 1), flush=True)
    print('INFO Java -> Java definition:', definition(java_entry, 8, 'getOwner', 1), flush=True)

    # Kotlin entry: breakpoints in both languages, stepping across the boundary, Java-frame inspection.
    dap, sock = debug_session(lsp, kotlin_main, 'mixed.MixedMainKt', {kotlin_main: [6], ledger: [21]})
    thread, frames = stop(dap)
    check('first stop in Java (Ledger.java:21) called from Kotlin', top(frames) == ('Ledger.java', 21) and any(f.get('source', {}).get('path', '').endswith('MixedMain.kt') for f in frames), frames[:3])
    values = locals_of(dap, frames[0])
    check('Java frame locals', values.get('before') == '0' and values.get('amount') == '3', values)
    value = dap.call('evaluate', {'expression': 'before + amount', 'frameId': frames[0]['id'], 'context': 'watch'})
    check('Java expression in Java frame', value.get('result') == '3', value)
    dap.call('continue', {'threadId': thread})
    thread, frames = stop(dap)
    check('second stop in Kotlin (MixedMain.kt:6)', top(frames) == ('MixedMain.kt', 6), frames[:2])
    value = dap.call('evaluate', {'expression': 'ledger.total + 1', 'frameId': frames[0]['id'], 'context': 'watch'})
    check('Kotlin expression reads Java property', value.get('result') == '4', value)
    dap.call('stepIn', {'threadId': thread})
    thread, frames = stop(dap)
    check('step into Kotlin -> Java (Ledger.applyKotlinRule)', top(frames)[0] == 'Ledger.java' and top(frames)[1] == 25, frames[:2])
    dap.call('stepIn', {'threadId': thread})
    thread, frames = stop(dap)
    check('step into Java -> Kotlin (doubleIt)', top(frames)[0] == 'MixedMain.kt' and top(frames)[1] == 12, frames[:3])
    check('mixed call stack Kotlin/Java/Kotlin', [pathlib.Path(f['source']['path']).name for f in frames[:3] if f.get('source')] == ['MixedMain.kt', 'Ledger.java', 'MixedMain.kt'], frames[:3])
    dap.call('stepOut', {'threadId': thread})
    thread, frames = stop(dap)
    check('step out Kotlin -> Java', top(frames)[0] == 'Ledger.java', frames[:2])
    dap.call('continue', {'threadId': thread})
    finish(dap, sock)

    # Java entry point calling Kotlin.
    dap, sock = debug_session(lsp, java_entry, 'mixed.JavaEntry', {java_entry: [14], kotlin_main: [13]})
    thread, frames = stop(dap)
    check('Java main stops at JavaEntry.java:14', top(frames) == ('JavaEntry.java', 14), frames[:2])
    dap.call('continue', {'threadId': thread})
    thread, frames = stop(dap)
    check('Kotlin breakpoint from Java main (MixedMain.kt:13)', top(frames) == ('MixedMain.kt', 13), frames[:3])
    check('Kotlin locals from Java main', locals_of(dap, frames[0]).get('doubled') == '10', locals_of(dap, frames[0]))
    dap.call('continue', {'threadId': thread})
    finish(dap, sock)
except Exception:
    import traceback; traceback.print_exc(); checks.append(('no exception', False))
finally:
    subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
    failed = [n for n, ok in checks if not ok]
    print('SUMMARY', len(checks) - len(failed), 'passed,', len(failed), 'failed', flush=True)
    sys.exit(1 if failed or not checks else 0)
