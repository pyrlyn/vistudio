"""Acceptance-test recorder. Forwards real LSP messages unchanged; implements no language features."""
import argparse
import json
import pathlib
import subprocess
import sys
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--log", required=True)
    parser.add_argument("server", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.server[1:] if args.server[:1] == ["--"] else args.server
    if not command:
        parser.error("specify the server command after --")
    log = pathlib.Path(args.log)
    log.parent.mkdir(parents=True, exist_ok=True)
    lock = threading.Lock()
    process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=sys.stderr)

    def copy_frames(source, destination, direction):
        try:
            while True:
                headers = []
                length = None
                while True:
                    line = source.readline()
                    if not line:
                        return
                    headers.append(line)
                    if line == b"\r\n":
                        break
                    if line.lower().startswith(b"content-length:"):
                        length = int(line.split(b":", 1)[1])
                if length is None:
                    raise ValueError("Missing Content-Length from " + direction)
                body = bytearray()
                while len(body) < length:
                    chunk = source.read(length - len(body))
                    if not chunk:
                        raise EOFError("Truncated LSP frame")
                    body.extend(chunk)
                with lock, log.open("a", encoding="utf-8") as stream:
                    stream.write(json.dumps({"time": time.time(), "direction": direction, "message": json.loads(body)}) + "\n")
                destination.write(b"".join(headers) + body)
                destination.flush()
        except (BrokenPipeError, OSError):
            return
        except Exception as error:
            print("LSP recorder: " + str(error), file=sys.stderr, flush=True)
            process.terminate()

    reader = threading.Thread(target=copy_frames, args=(sys.stdin.buffer, process.stdin, "client"), daemon=True)
    reader.start()
    copy_frames(process.stdout, sys.stdout.buffer, "server")
    return process.wait()


if __name__ == "__main__":
    sys.exit(main())
