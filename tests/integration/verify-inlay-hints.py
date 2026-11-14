# Run from the repository root. Requires the separately installed official Kotlin LSP and Eclipse JDT LS
# (paths taken from Vistudio settings). Prints and checks the inlay hints both servers return for Kotlin and
# Java code on samples/kotlin-java-jvm. Hint-worthy code is sent as unsaved edits; nothing is written to disk.
import json, os, pathlib, queue, re, subprocess, sys, tempfile, threading, time
pathlib.Path('artifacts').mkdir(exist_ok=True)
root = pathlib.Path('samples/kotlin-java-jvm').resolve()
settings_xml = (pathlib.Path(os.environ['LOCALAPPDATA']) / 'Vistudio/settings.xml').read_text(encoding='utf-8-sig')
def setting(name): m = re.search('<' + name + '>(.*?)</' + name + '>', settings_xml); return m.group(1) if m else ''
log = open('artifacts/inlay-hints-probe-' + '-'.join(sys.argv[1:] or ['all']) + '.jsonl', 'w', encoding='utf8')
checks = []

def check(name, condition, detail=''):
    checks.append((name, bool(condition)))
    print(('PASS ' if condition else 'FAIL ') + name + ('' if condition else ' :: ' + str(detail)[:1500]), flush=True)

# The same settings JavaLanguageClient sends (initializationOptions and workspace/configuration "java").
JAVA_HINTS = {'parameterNames': {'enabled': 'all'}, 'variableTypes': {'enabled': True}, 'parameterTypes': {'enabled': True}, 'formatParameters': {'enabled': True}}
# Kotlin LSP 271 option ids (IntelliJ InlayInfoOption names plus the provider-level ids), all on. The VS Code
# extension 0.0.12 still names some of them hints.settings.*, which build 271 ignores.
KOTLIN_OPTIONS = ['hints.type.property', 'hints.type.variable', 'hints.type.function.return', 'hints.type.function.parameter',
                  'hints.lambda.return', 'hints.lambda.receivers.parameters', 'hints.value.ranges', 'hints.value.kotlin.time',
                  'hints.parameters', 'hints.parameters.context', 'hints.parameters.compiled', 'hints.parameters.excluded', 'hints.call.chains']
def nested(keys):
    tree = {}
    for key in keys:
        node, parts = tree, key.split('.')
        for part in parts[:-1]:
            if not isinstance(node.get(part), dict): node[part] = {} if part not in node else {'': node[part]}
            node = node[part]
        if isinstance(node.get(parts[-1]), dict): node[parts[-1]][''] = True
        else: node[parts[-1]] = True
    return tree
KOTLIN_FORMATS = {'flat': {k: True for k in KOTLIN_OPTIONS}, 'nested': nested(KOTLIN_OPTIONS)}
SECTIONS = {}  # workspace/configuration answers by section

class Lsp:
    def __init__(self, process, name):
        self.p, self.name = process, name
        self.q = queue.Queue(); self.seq = 0; self.pending = []; self.lock = threading.Lock()
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
                log.write(json.dumps({'peer': self.name, 'in': msg}) + '\n'); log.flush()
                if 'method' in msg and 'id' in msg:
                    result = None
                    if msg['method'] == 'workspace/configuration':
                        result = [SECTIONS.get(i.get('section')) for i in msg['params']['items']]
                    self.send({'jsonrpc': '2.0', 'id': msg['id'], 'result': result})
                else: self.q.put(msg)
        except Exception as e: self.q.put({'read_error': str(e)})
    def send(self, msg):
        data = json.dumps(msg).encode()
        with self.lock:
            log.write(json.dumps({'peer': self.name, 'out': msg}) + '\n'); log.flush()
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
    def open(self, path, language, text):
        self.send({'jsonrpc': '2.0', 'method': 'textDocument/didOpen', 'params': {'textDocument': {'uri': path.as_uri(), 'languageId': language, 'version': 1, 'text': text}}})
    def hints(self, path, text, resolve=False):
        lines = text.splitlines()
        result = self.call('textDocument/inlayHint', {'textDocument': {'uri': path.as_uri()}, 'range': {'start': {'line': 0, 'character': 0}, 'end': {'line': len(lines), 'character': 0}}}) or []
        if resolve: result = [self.call('inlayHint/resolve', h) for h in result]
        return result

def label(hint):
    value = hint['label']
    return value if isinstance(value, str) else ''.join(part['value'] for part in value)

def show(title, hints, text):
    lines = text.splitlines()
    print('INFO', title, len(hints), 'hints', flush=True)
    for h in hints:
        p = h['position']
        print('   line %3d col %3d kind %s  %-28r | %s' % (p['line'] + 1, p['character'], h.get('kind'), label(h), lines[p['line']].strip()[:70]), flush=True)

def has(hints, text, line_needle, label_needle):
    lines = text.splitlines()
    return any(line_needle in lines[h['position']['line']] and label_needle in label(h) for h in hints)

KOTLIN_EXTRA = '''
fun hintCases(values: List<Int>) {
    val doubled = values.map { it * 2 }
    val pairs = values.zip(doubled)
    val sum = values.fold(0) { acc, v -> acc + v }
    val span = 0..10
    val text = values.filter { it > 0 }
        .map { it.toString() }
        .joinToString(", ")
    val ledger = Ledger("x")
    val described = describe(42, true)
    with(ledger) { add(1) }
    println("$pairs $sum $span $text $described")
    val squares = values.map {
        val square = it * it
        square + 1
    }
    with(ledger) {
        add(squares.size)
    }
    for (i in 0 until 10) println(i)
    for (j in 10 downTo 1 step 2) println(j)
    val window = 1..<5
    val lazyValue by lazy { emptyList<String>() }
    println("$window $lazyValue")
}

val topLevel = listOf("a", "b").associateWith { it.length }

class Holder(val seed: Int) {
    val derived = mapOf(seed to "x")
    fun twice() = seed * 2
}

fun describe(count: Int, flag: Boolean) = "$count $flag"
'''

JAVA_EXTRA = '''
    public static String hintCases(java.util.List<Integer> values) {
        var names = new java.util.ArrayList<String>();
        var size = values.size();
        String joined = String.join(", ", names);
        int larger = Math.max(size, 3);
        values.forEach(v -> names.add(String.valueOf(v)));
        java.util.function.BiFunction<Integer, Integer, Integer> plus = (a, b) -> a + b;
        return joined + larger + plus.apply(1, 2);
    }
}'''

kotlin_main = root / 'src/main/kotlin/mixed/MixedMain.kt'
ledger = root / 'src/main/java/mixed/Ledger.java'
kotlin_text = kotlin_main.read_text() + KOTLIN_EXTRA
java_text = ledger.read_text().rstrip()
java_text = java_text[:java_text.rstrip().rfind('}')] + JAVA_EXTRA + '\n'
processes = []

def kotlin_probe():
    server = setting('LanguageServerPath')
    if not server: print('SKIP Kotlin: no language server configured'); return
    env = os.environ.copy(); env['IJ_JAVA_OPTIONS'] = '-Djavax.net.ssl.trustStoreType=Windows-ROOT -Djavax.net.ssl.trustStore=NUL'
    process = subprocess.Popen([server, '--stdio', '--system-path=' + os.environ['LOCALAPPDATA'] + '/Vistudio/kotlin-lsp-inlay-probe'],
                               stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open('artifacts/inlay-hints-kotlin.stderr', 'w'), env=env)
    processes.append(process)
    lsp = Lsp(process, 'kotlin')
    init = lsp.call('initialize', {'processId': os.getpid(), 'rootUri': root.as_uri(), 'workspaceFolders': [{'uri': root.as_uri(), 'name': root.name}],
                                   'capabilities': {'workspace': {'configuration': True, 'inlayHint': {'refreshSupport': True}}, 'window': {'workDoneProgress': True},
                                                    'textDocument': {'inlayHint': {'dynamicRegistration': False, 'resolveSupport': {'properties': ['tooltip', 'textEdits', 'label.tooltip', 'label.location', 'label.command']}}}}})
    print('INFO Kotlin inlayHintProvider:', init['capabilities'].get('inlayHintProvider'), flush=True)
    lsp.send({'jsonrpc': '2.0', 'method': 'initialized', 'params': {}})
    lsp.open(kotlin_main, 'kotlin', kotlin_text)
    lsp.wait(lambda m: m.get('method') == 'intellij/importLog' and m.get('params', {}).get('succeeded'), 300)
    # The server asks for section "jetbrains.kotlin" before each request; try each answer format.
    found = {}
    for name in os.environ.get('KOTLIN_FORMATS', 'flat').split(','):
        SECTIONS['jetbrains.kotlin'] = KOTLIN_FORMATS[name]
        for _ in range(15):
            found[name] = lsp.hints(kotlin_main, kotlin_text)
            if found[name]: break
            time.sleep(2)
        print('INFO Kotlin format', name, '->', len(found[name]), 'hints', flush=True)
    hints = max(found.values(), key=len)
    show('Kotlin', hints, kotlin_text)
    if hints:
        resolved = lsp.call('inlayHint/resolve', hints[0])
        print('INFO Kotlin resolved sample:', json.dumps(resolved)[:600], flush=True)
    check('Kotlin: local variable type (val doubled: List<Int>)', has(hints, kotlin_text, 'val doubled', 'List<Int>'), hints[:5])
    check('Kotlin: parameter name (describe(count = 42))', has(hints, kotlin_text, 'describe(42', 'count'), hints[:5])
    # Implicit it/this, lambda returns, ranges and call chains appear only where IntelliJ shows them (multi-line lambdas etc.).
    check('Kotlin: implicit lambda parameter (it: Int)', has(hints, kotlin_text, 'val squares', 'it: Int'), hints[:5])
    check('Kotlin: implicit receiver (this: Ledger)', has(hints, kotlin_text, 'with(ledger) {', 'this: Ledger'), hints[:5])
    check('Kotlin: property type (topLevel: Map<String, Int>)', has(hints, kotlin_text, 'val topLevel', 'Map<String, Int>'), hints[:5])
    check('Kotlin: function return type (twice(): Int)', has(hints, kotlin_text, 'fun twice()', 'Int'), hints[:5])
    check('Kotlin: lambda parameter types (acc: Int)', has(hints, kotlin_text, 'acc, v ->', ': Int'), hints[:5])
    check('Kotlin: lambda return expression (^map)', has(hints, kotlin_text, 'square + 1', '^map'), hints[:5])
    check('Kotlin: ranges (0 until 10)', has(hints, kotlin_text, '0 until 10', '<'), hints[:5])
    check('Kotlin: call chain types', has(hints, kotlin_text, '.map { it.toString() }', 'List<String>'), hints[:5])
    check('Kotlin: whitespace-only hints are present for the client to fold into padding', any(not label(h).strip() for h in hints), '')

def java_probe():
    install, java_home = setting('JavaLanguageServerPath'), setting('JavaLanguageServerJavaHome')
    if not install: print('SKIP Java: no JDT LS configured'); return
    install, java_home = pathlib.Path(install), pathlib.Path(java_home)
    gradle = {'enabled': True, 'wrapper': {'enabled': True}, 'arguments': '--init-script ' + str(pathlib.Path('src/Vistudio.Vsix/Build/JavaKotlinInterop.gradle').resolve())}
    if setting('GradleJavaHome'): gradle['java'] = {'home': setting('GradleJavaHome')}
    java = {'import': {'gradle': gradle, 'maven': {'enabled': False}, 'generatesMetadataFilesAtProjectRoot': False}, 'autobuild': {'enabled': False},
            'completion': {'enabled': True}, 'inlayHints': JAVA_HINTS}
    launcher = sorted((install / 'plugins').glob('org.eclipse.equinox.launcher_*.jar'))[-1]
    command = [str(java_home / 'bin/java.exe'), '-Declipse.application=org.eclipse.jdt.ls.core.id1', '-Dosgi.bundles.defaultStartLevel=4',
               '-Declipse.product=org.eclipse.jdt.ls.core.product', '-Dosgi.checkConfiguration=true', '-Dosgi.sharedConfiguration.area=' + str(install / 'config_win'),
               '-Dosgi.sharedConfiguration.area.readOnly=true', '-Dosgi.configuration.cascaded=true', '-Djava.import.generatesMetadataFilesAtProjectRoot=false', '-Xms256m', '-Xmx2G',
               '--add-modules=ALL-SYSTEM', '--add-opens', 'java.base/java.util=ALL-UNNAMED', '--add-opens', 'java.base/java.lang=ALL-UNNAMED', '-jar', str(launcher), '-data', tempfile.mkdtemp(prefix='vistudio-jdtls-inlay-')]
    env = os.environ.copy(); env['JAVA_HOME'] = str(java_home)
    process = subprocess.Popen(command, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=open('artifacts/inlay-hints-java.stderr', 'w'), env=env)
    processes.append(process)
    SECTIONS['java'] = java
    lsp = Lsp(process, 'java')
    init = lsp.call('initialize', {'processId': os.getpid(), 'rootUri': root.as_uri(), 'workspaceFolders': [{'uri': root.as_uri(), 'name': root.name}],
                                   'initializationOptions': {'settings': {'java': java}, 'extendedClientCapabilities': {'classFileContentsSupport': False}},
                                   'capabilities': {'workspace': {'configuration': True, 'workspaceFolders': True, 'inlayHint': {'refreshSupport': True}},
                                                    'textDocument': {'inlayHint': {'dynamicRegistration': False}}}})
    print('INFO Java inlayHintProvider:', init['capabilities'].get('inlayHintProvider'), flush=True)
    lsp.send({'jsonrpc': '2.0', 'method': 'initialized', 'params': {}})
    lsp.open(ledger, 'java', java_text)
    lsp.wait(lambda m: m.get('method') == 'language/status' and m['params'].get('type') == 'ServiceReady', 600)
    hints = []
    for _ in range(20):
        hints = lsp.hints(ledger, java_text)
        if has(hints, java_text, 'var size', 'int'): break
        time.sleep(3)
    show('Java', hints, java_text)
    # JDT LS omits the type when the initializer spells it (var names = new ArrayList<String>()).
    check('Java: var type (var size: int)', has(hints, java_text, 'var size', 'int'), hints[:5])
    check('Java: parameter name (Math.max(a: size))', has(hints, java_text, 'Math.max', 'a'), hints[:5])
    check('Java: lambda parameter type (v -> Integer)', has(hints, java_text, 'forEach(v', 'Integer'), hints[:5])
    try: lsp.call('shutdown', None, 20); lsp.send({'jsonrpc': '2.0', 'method': 'exit'})
    except Exception: pass

try:
    which = sys.argv[1:] or ['kotlin', 'java']
    if 'kotlin' in which: kotlin_probe()
    if 'java' in which: java_probe()
except Exception:
    import traceback; traceback.print_exc(); checks.append(('no exception', False))
finally:
    time.sleep(1)
    for process in processes: subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True)
    failed = [n for n, ok in checks if not ok]
    print('SUMMARY', len(checks) - len(failed), 'passed,', len(failed), 'failed', flush=True)
    sys.exit(1 if failed or not checks else 0)
