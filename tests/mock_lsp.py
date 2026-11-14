"""Protocol fixture only. This is not a Kotlin language server."""
import json
import pathlib
import sys

log_path = pathlib.Path(sys.argv[1])
while True:
    headers = {}
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            sys.exit(0)
        if line == b"\r\n":
            break
        key, value = line.decode("ascii").split(":", 1)
        headers[key.lower()] = value.strip()
    message = json.loads(sys.stdin.buffer.read(int(headers["content-length"])))
    with log_path.open("a", encoding="utf-8") as log:
        log.write(json.dumps(message) + "\n")
    method = message.get("method")
    if method == "exit":
        break
    if "id" not in message:
        continue
    result = None
    if method == "initialize":
        result = {"capabilities": {"textDocumentSync": 1,
                  "completionProvider": {"triggerCharacters": ["."]},
                  "hoverProvider": True}}
    elif method == "textDocument/hover":
        result = {"contents": {"kind": "plaintext", "value": "Vistudio LSP transport fixture"}}
    elif method == "textDocument/completion":
        result = [{"label": "vistudioFixture", "kind": 3}]
    payload = json.dumps({"jsonrpc": "2.0", "id": message["id"], "result": result}).encode("utf-8")
    sys.stdout.buffer.write(f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii") + payload)
    sys.stdout.buffer.flush()
