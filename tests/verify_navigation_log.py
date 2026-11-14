"""Check real VS-to-server navigation traffic recorded with lsp_proxy.py."""
import json
import pathlib
import sys
import urllib.parse


def path(location):
    return urllib.parse.unquote(urllib.parse.urlparse(location["uri"]).path).replace("\\", "/").lower()


records = [json.loads(line) for line in pathlib.Path(sys.argv[1]).read_text(encoding="utf-8").splitlines()]
# Request IDs restart when the client reconnects. Validate only the latest session.
start = max(i for i, entry in enumerate(records) if entry["direction"] == "client" and entry["message"].get("method") == "initialize")
records = records[start:]
messages = [entry["message"] for entry in records if entry["direction"] == "server"]
initialize = records[0]["message"]
response = next(item for item in messages if item.get("id") == initialize["id"] and "result" in item)
assert response["result"]["capabilities"]["definitionProvider"]
assert response["result"]["capabilities"]["referencesProvider"]


def results(method):
    request = next(entry["message"] for entry in records if entry["direction"] == "client" and entry["message"].get("method") == method)
    reply = next(item for item in messages if item.get("id") == request["id"] and ("result" in item or "error" in item))
    assert "error" not in reply, reply
    found = list(reply.get("result") or [])
    token = request["params"].get("partialResultToken")
    if token is not None:
        for item in messages:
            if item.get("method") == "$/progress" and item["params"]["token"] == token:
                found.extend(item["params"]["value"])
    return found


definition = results("textDocument/definition")
assert len(definition) == 1 and path(definition[0]).endswith("/greeting.kt")
assert definition[0]["range"]["start"] == {"line": 3, "character": 8}
references = results("textDocument/references")
actual = {(path(item).rsplit("/", 1)[-1], item["range"]["start"]["line"], item["range"]["start"]["character"]) for item in references}
assert actual == {("greeting.kt", 3, 8), ("main.kt", 4, 21), ("preview.kt", 2, 44)}, actual
print("PASS: real LSP definition and streamed references match the sample; unrelated message() excluded.")
