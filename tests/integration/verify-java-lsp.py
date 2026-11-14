# Run from the repository root. Starts a user-installed Eclipse JDT LS exactly like JavaLanguageServer.Create
# and checks Java editing features on samples/kotlin-java-jvm. Arguments: <jdtls folder> <java 21+ home> [gradle jdk]
import json, os, pathlib, queue, re, subprocess, sys, tempfile, threading, time
pathlib.Path('artifacts').mkdir(exist_ok=True)
install, java_home = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
gradle_jdk = sys.argv[3] if len(sys.argv) > 3 else os.environ.get('JAVA_HOME', '')
root = pathlib.Path('samples/kotlin-java-jvm').resolve()
log = open('artifacts/java-lsp-probe.jsonl', 'w', encoding='utf8')
checks = []

def check(name, condition, detail=''):
    checks.append((name, bool(condition)))
    print(('PASS ' if condition else 'FAIL ') + name + ('' if condition else ' :: ' + str(detail)[:1500]), flush=True)

class Lsp:
    def __init__(self, process):
        self.p = process; self.q = queue.Queue(); self.seq = 0; self.pending = []; self.lock = threading.Lock()
        threading.Thread(target=self.read, daemon=True).start()
    def read(self):
        try:
            while True:
                headers = {}
                while True:
                    line = self.p.stdout.readline()
                    if not line: raise EOFError()
                    if line == b'\r\n': break
                    k, v = line.decode().split(':', 1); headers[k.lower()] = v.strip()
                msg = json.loads(self.p.stdout.read(int(headers['content-length'])))
                log.write(json.dumps({'in': msg}) + '\n'); log.flush()
                if 'method' in msg and 'id' in msg:
                    result = [settings['java'] if i.get('section') == 'java' else None for i in msg['params']['items']] if msg['method'] == 'workspace/configuration' else None
                    self.send({'jsonrpc': '2.0', 'id': msg['id'], 'result': result})
                else: self.q.put(msg)
        except Exception as e: self.q.put({'read_error': str(e)})
    def send(self, msg):
        data = json.dumps(msg).encode()
        with self.lock:
            log.write(json.dumps({'out': msg}) + '\n'); log.flush()
            self.p.stdin.write(b'Content-Length: ' + str(len(data)).encode() + b'\r\n\r\n' + data); self.p.stdin.flush()
    def call(self, method, params, timeout=180):
        self.seq += 1; seq = self.seq
        self.send({'jsonrpc': '2.0', 'id': seq, 'method': method, 'params': params})
        m = self.wait(lambda m: m.get('id') == seq, timeout)
        if 'error' in m: raise RuntimeError(m)
        return m.get('result')
    def wait(self, predicate, timeout=180):
        end = time.monotonic() + timeout
        while True:
            for i, m in enumerate(self.pending):
                if predicate(m): return self.pending.pop(i)
            m = self.q.get(timeout=max(0.1, end - time.monotonic()))
            if 'read_error' in m: raise RuntimeError(m)
            self.pending.append(m)

gradle = {'enabled': True, 'wrapper': {'enabled': True}, 'arguments': '--init-script ' + str(pathlib.Path('src/Vistudio.Vsix/Build/JavaKotlinInterop.gradle').resolve())}
if gradle_jdk: gradle['java'] = {'home': gradle_jdk}
settings = {'java': {'import': {'gradle': gradle, 'maven': {'enabled': False}, 'generatesMetadataFilesAtProjectRoot': False}, 'autobuild': {'enabled': False}, 'completion': {'enabled': True}}}
launcher = sorted((install / 'plugins').glob('org.eclipse.equinox.launcher_*.jar'))[-1]
data = tempfile.mkdtemp(prefix='vistudio-jdtls-')
command = [str(java_home / 'bin/java.exe'), '-Declipse.application=org.eclipse.jdt.ls.core.id1', '-Dosgi.bundles.defaultStartLevel=4',
           '-Declipse.product=org.eclipse.jdt.ls.core.product', '-Dosgi.checkConfiguration=true', '-Dosgi.sharedConfiguration.area=' + str(install / 'config_win'),
           '-Dosgi.sharedConfiguration.area.readOnly=true', '-Dosgi.configuration.cascaded=true', '-Djava.import.generatesMetadataFilesAtProjectRoot=false', '-Dlog.level=ALL', '-Xms256m', '-Xmx2G',
           '--add-modules=ALL-SYSTEM', '--add-opens', 'java.base/java.util=ALL-UNNAMED', '--add-opens', 'java.base/java.lang=ALL-UNNAMED', '-jar', str(launcher), '-data', data]
env = os.environ.copy(); env['JAVA_HOME'] = str(java_home)
process = subprocess.Popen(command, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open('artifacts/java-lsp-probe.stderr', 'w'), env=env)

def position(path, line, needle, offset):
    text = path.read_text().splitlines()[line - 1]
    return {'line': line - 1, 'character': text.index(needle) + offset}
def labels(result):
    items = result.get('items', []) if isinstance(result, dict) else (result or [])
    return {re.sub(r'\(.*', '', x.get('filterText') or x['label']).strip() for x in items}
def targets(result):
    result = result if isinstance(result, list) else [result] if result else []
    return [pathlib.Path(x.get('uri') or x.get('targetUri')).name for x in result]

ledger = root / 'src/main/java/mixed/Ledger.java'
java_entry = root / 'src/main/java/mixed/JavaEntry.java'
try:
    lsp = Lsp(process)
    started = time.monotonic()
    lsp.call('initialize', {'processId': os.getpid(), 'rootUri': root.as_uri(), 'workspaceFolders': [{'uri': root.as_uri(), 'name': root.name}],
                            'initializationOptions': {'settings': settings, 'extendedClientCapabilities': {'classFileContentsSupport': False}},
                            'capabilities': {'workspace': {'configuration': True, 'workspaceFolders': True},
                                             'textDocument': {'completion': {'completionItem': {'snippetSupport': False}}, 'publishDiagnostics': {}}}})
    lsp.send({'jsonrpc': '2.0', 'method': 'initialized', 'params': {}})
    for path in (ledger, java_entry):
        lsp.send({'jsonrpc': '2.0', 'method': 'textDocument/didOpen', 'params': {'textDocument': {'uri': path.as_uri(), 'languageId': 'java', 'version': 1, 'text': path.read_text()}}})
    lsp.wait(lambda m: m.get('method') == 'language/status' and m['params'].get('type') == 'ServiceReady', 600)
    print('INFO ServiceReady after', round(time.monotonic() - started, 1), 's', flush=True)
    names = labels(lsp.call('textDocument/completion', {'textDocument': {'uri': java_entry.as_uri()}, 'position': position(java_entry, 8, 'getOwner', 3)}))
    check('Java completion of Java members (ledger.get|)', {'getOwner', 'getTotal'} <= names, sorted(names)[:30])
    names = labels(lsp.call('textDocument/completion', {'textDocument': {'uri': ledger.as_uri()}, 'position': position(ledger, 26, 'total', 2)}))
    check('Java completion of fields and locals (to|)', {'total'} <= names, sorted(names)[:30])
    found = targets(lsp.call('textDocument/definition', {'textDocument': {'uri': java_entry.as_uri()}, 'position': position(java_entry, 8, 'getOwner', 1)}))
    check('Java -> Java definition', 'Ledger.java' in found, found)
    hover = lsp.call('textDocument/hover', {'textDocument': {'uri': java_entry.as_uri()}, 'position': position(java_entry, 8, 'getOwner', 1)})
    check('Java hover', hover and 'getOwner' in json.dumps(hover), hover)
    # The Gradle classpath (including Kotlin class output) can settle after ServiceReady; poll a bounded time.
    deadline = time.monotonic() + 120
    while True:
        names = labels(lsp.call('textDocument/completion', {'textDocument': {'uri': ledger.as_uri()}, 'position': position(ledger, 25, 'doubleIt', 3)}))
        if 'doubleIt' in names or time.monotonic() > deadline: break
        time.sleep(3)
    print('INFO Kotlin symbols visible after', round(time.monotonic() - started, 1), 's', flush=True)
    check('Java completion of Kotlin top-level function (MixedMainKt.dou|)', 'doubleIt' in names, sorted(names)[:10])
    found = targets(lsp.call('textDocument/definition', {'textDocument': {'uri': ledger.as_uri()}, 'position': position(ledger, 25, 'doubleIt', 1)}))
    print('INFO Java -> Kotlin definition:', found, flush=True)
    # Diagnostics flow: an edit calling a missing Kotlin function reports exactly that (the Kotlin class itself
    # resolves), and restoring the text clears it. Unchanged clean files may publish nothing.
    def diagnostics_after(text, version):
        lsp.send({'jsonrpc': '2.0', 'method': 'textDocument/didChange', 'params': {'textDocument': {'uri': ledger.as_uri(), 'version': version}, 'contentChanges': [{'text': text}]}})
        m = lsp.wait(lambda m: m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'].endswith('Ledger.java') and m['params'].get('version', version) == version, 60)
        return [d['message'] for d in m['params']['diagnostics']]
    original = ledger.read_text()
    broken = diagnostics_after(original.replace('MixedMainKt.doubleIt(', 'MixedMainKt.doubleItMissing('), 2)
    check('Java diagnostics name the missing Kotlin member, not the Kotlin class', any('doubleItMissing' in d for d in broken) and not any('MixedMainKt cannot be resolved' in d for d in broken), broken)
    restored = diagnostics_after(original, 3)
    check('No false errors for Kotlin symbols used from Java', not restored, restored)
    stale = [m['params'] for m in lsp.pending if m.get('method') == 'textDocument/publishDiagnostics' and m['params']['uri'].endswith('JavaEntry.java') and m['params']['diagnostics']]
    check('No Java errors in Java-only file', not stale, stale)
    written = [n for n in ('.classpath', '.project', '.settings', 'bin') if (root / n).exists()]
    check('No Eclipse metadata written into the project folder', not written, written)
    try: lsp.call('shutdown', None, 20); lsp.send({'jsonrpc': '2.0', 'method': 'exit'})
    except Exception: pass
except Exception:
    import traceback; traceback.print_exc(); checks.append(('no exception', False))
finally:
    time.sleep(1)
    subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
    failed = [n for n, ok in checks if not ok]
    print('SUMMARY', len(checks) - len(failed), 'passed,', len(failed), 'failed', flush=True)
    sys.exit(1 if failed or not checks else 0)
