"""Disposable pinned-aria2 behavior probe. Never uses application state or ports."""

import argparse
import hashlib
import http.server
import json
import os
import pathlib
import re
import socket
import subprocess
import tempfile
import threading
import time
import urllib.request


ROOT = pathlib.Path(__file__).resolve().parents[1]
ENGINE = ROOT / "vendor/aria2/win-x64/aria2c.exe"
PAYLOAD = bytes((i * 73 + i // 251) % 256 for i in range(8 * 1024 * 1024))
ETAG = '"m0-synthetic-v1"'
REQUESTS = []
LOCK = threading.Lock()


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


class Origin(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_args):
        pass

    def handle(self):
        try:
            super().handle()
        except (ConnectionAbortedError, ConnectionResetError):
            pass

    def do_GET(self):
        requested = self.headers.get("Range")
        match = re.fullmatch(r"bytes=(\d+)-(\d*)", requested or "")
        if requested and not match:
            self.send_error(416)
            return
        start = int(match.group(1)) if match else 0
        end = min(int(match.group(2)), len(PAYLOAD) - 1) if match and match.group(2) else len(PAYLOAD) - 1
        if start > end or start >= len(PAYLOAD):
            self.send_error(416)
            return
        row = {"range": requested, "start": start, "end": end, "status": 206 if match else 200,
               "started": time.monotonic(), "bytes_sent": 0, "completed": False}
        with LOCK:
            REQUESTS.append(row)
        try:
            self.send_response(row["status"])
            self.send_header("Content-Length", str(end - start + 1))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("ETag", ETAG)
            if match:
                self.send_header("Content-Range", f"bytes {start}-{end}/{len(PAYLOAD)}")
            self.end_headers()
            for offset in range(start, end + 1, 16384):
                chunk = PAYLOAD[offset:min(offset + 16384, end + 1)]
                self.wfile.write(chunk)
                self.wfile.flush()
                row["bytes_sent"] += len(chunk)
                time.sleep(0.006)
            row["completed"] = True
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        finally:
            row["finished"] = time.monotonic()


def rpc(port, secret, method, *params):
    request = json.dumps({"jsonrpc": "2.0", "id": "m0", "method": "aria2." + method,
                          "params": ["token:" + secret, *params]}).encode()
    last_error = None
    for _ in range(30):
        try:
            with urllib.request.urlopen(urllib.request.Request(f"http://127.0.0.1:{port}/jsonrpc", request,
                                                                {"Content-Type": "application/json"}), timeout=2) as response:
                result = json.load(response)
            if "error" in result:
                raise RuntimeError(f"RPC {method}: {result['error']}")
            return result["result"]
        except (OSError, TimeoutError) as exc:
            last_error = repr(exc)
            time.sleep(0.1)
    raise RuntimeError(f"RPC {method} unavailable: {last_error}")


def wait_status(port, secret, gid, predicate, timeout=20):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = rpc(port, secret, "tellStatus", gid, ["gid", "status", "completedLength", "totalLength", "errorCode"])
        if predicate(state):
            return state
        time.sleep(0.08)
    raise TimeoutError(f"GID {gid} did not reach requested state; last={state}")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None


def snapshot(directory):
    return {p.name: {"length": p.stat().st_size, "sha256": digest(p)} for p in directory.iterdir()
            if p.is_file() and p.name != "engine.log"}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    port = free_port()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Origin)
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()
    result = {"engine_sha256": digest(ENGINE), "payload_sha256": hashlib.sha256(PAYLOAD).hexdigest(),
              "cases": {}, "requests": []}
    try:
        with tempfile.TemporaryDirectory(prefix="aria2-m0-", dir=args.output.parent) as temp:
            base = pathlib.Path(temp)
            rpc_port = free_port()
            secret = os.urandom(24).hex()
            engine_log = (base / "engine.log").open("wb")
            process = subprocess.Popen([str(ENGINE), "--enable-rpc=true", "--rpc-listen-all=false",
                                        f"--rpc-listen-port={rpc_port}", f"--rpc-secret={secret}",
                                        "--check-certificate=true", "--allow-overwrite=false", "--auto-file-renaming=false",
                                        "--continue=true", "--max-tries=2", "--retry-wait=1", "--save-session-interval=0",
                                        "--enable-dht=false", "--enable-dht6=false", "--enable-peer-exchange=false",
                                        "--bt-enable-lpd=false", "--file-allocation=none", "--summary-interval=0",
                                        "--console-log-level=warn", f"--dir={base}"],
                                       cwd=base, stdin=subprocess.DEVNULL, stdout=engine_log,
                                       stderr=subprocess.STDOUT, creationflags=subprocess.CREATE_NO_WINDOW)
            try:
                time.sleep(0.3)
                if process.poll() is not None:
                    engine_log.flush()
                    raise RuntimeError(f"Engine exited {process.returncode}: {(base / 'engine.log').read_text(errors='replace')}")
                result["version"] = rpc(rpc_port, secret, "getVersion")
                result["required_methods"] = {}
                for method, parameters in [("getGlobalOption", ()), ("tellActive", ()), ("tellWaiting", (0, 1)),
                                           ("tellStopped", (0, 1))]:
                    result["required_methods"][method] = isinstance(rpc(rpc_port, secret, method, *parameters), (dict, list))
                url = f"http://127.0.0.1:{port}/payload"
                options = {"dir": str(base), "out": "parallel.bin", "split": "4", "max-connection-per-server": "4",
                           "min-split-size": "1M", "max-download-limit": "512K"}
                gid = rpc(rpc_port, secret, "addUri", [url], options)
                wait_status(rpc_port, secret, gid, lambda s: int(s["completedLength"]) >= 131072)
                split_request_index = len(REQUESTS)
                before = rpc(rpc_port, secret, "tellStatus", gid, ["gid", "status", "completedLength"])
                rpc(rpc_port, secret, "changeOption", gid, {"split": "2", "max-connection-per-server": "2"})
                after = rpc(rpc_port, secret, "tellStatus", gid, ["gid", "status", "completedLength"])
                result["cases"]["active_split_change"] = {"gid": gid, "before": before, "after": after,
                    "request_index_at_change": split_request_index,
                    "option_after": {k: rpc(rpc_port, secret, "getOption", gid)[k] for k in ("split", "max-connection-per-server")}}
                rpc(rpc_port, secret, "changeOption", gid, {"max-download-limit": "256K"})
                rate_before = int(rpc(rpc_port, secret, "tellStatus", gid, ["completedLength"])["completedLength"])
                time.sleep(1)
                rate_after = int(rpc(rpc_port, secret, "tellStatus", gid, ["completedLength"])["completedLength"])
                result["cases"]["active_rate_change"] = {"gid": gid,
                    "effective": rpc(rpc_port, secret, "getOption", gid)["max-download-limit"],
                    "one_second_delta": rate_after - rate_before}
                paused = rpc(rpc_port, secret, "pause", gid)
                wait_status(rpc_port, secret, gid, lambda s: s["status"] == "paused")
                first = snapshot(base)
                time.sleep(0.25)
                second = snapshot(base)
                result["cases"]["paused_snapshot"] = {"pause_gid": paused, "first": first, "second": second,
                    "byte_exact_stable": first == second, "file_length": first.get("parallel.bin", {}).get("length"),
                    "downloaded": rpc(rpc_port, secret, "tellStatus", gid, ["completedLength"])["completedLength"]}
                rpc(rpc_port, secret, "unpause", gid)
                final = wait_status(rpc_port, secret, gid, lambda s: s["status"] in ("complete", "error"), 70)
                result["cases"]["parallel_completion"] = {"state": final, "payload_sha256": digest(base / "parallel.bin"),
                    "byte_exact": (base / "parallel.bin").read_bytes() == PAYLOAD}
                # Issue duplicate bounded requests directly against the disposable origin to expose response semantics.
                def bounded():
                    request = urllib.request.Request(url, headers={"Range": "bytes=0-65535"})
                    with urllib.request.urlopen(request, timeout=5) as response:
                        return response.status, response.headers.get("Content-Range"), hashlib.sha256(response.read()).hexdigest()
                from concurrent.futures import ThreadPoolExecutor
                with ThreadPoolExecutor(max_workers=2) as pool:
                    responses = list(pool.map(lambda _: bounded(), range(2)))
                result["cases"]["duplicate_bounded_zero"] = {"responses": responses,
                    "expected_sha256": hashlib.sha256(PAYLOAD[:65536]).hexdigest()}
            finally:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
                engine_log.close()
    finally:
        server.shutdown()
        result["requests"] = [{**{k: v for k, v in row.items() if k not in ("started", "finished")},
                               "start_ms": round(1000 * (row["started"] - REQUESTS[0]["started"])),
                               "finish_ms": round(1000 * (row.get("finished", time.monotonic()) - REQUESTS[0]["started"]))}
                              for row in REQUESTS]
        cases = result["cases"]
        engine_ranges = result["requests"][:cases["active_split_change"]["request_index_at_change"]]
        checks = {"engine_bounded_zero_206": any(r["start"] == 0 and r["end"] < len(PAYLOAD) - 1
                                                   and r["status"] == 206 for r in engine_ranges),
                  "engine_concurrent_ranges": any(a["start_ms"] < b["finish_ms"] and b["start_ms"] < a["finish_ms"]
                                                   and a["start"] != b["start"] for a in engine_ranges for b in engine_ranges),
                  "sparse_file": cases["paused_snapshot"]["file_length"] > int(cases["paused_snapshot"]["downloaded"]),
                  "paused_bytes_stable": cases["paused_snapshot"]["byte_exact_stable"],
                  "same_gid": cases["active_split_change"]["before"]["gid"] == cases["active_split_change"]["after"]["gid"],
                  "rate_option_effective": cases["active_rate_change"]["effective"] == "262144",
                  "final_byte_exact": cases["parallel_completion"]["byte_exact"],
                  "duplicate_zero_exact": all(r[0] == 206 and r[2] == cases["duplicate_bounded_zero"]["expected_sha256"]
                                              for r in cases["duplicate_bounded_zero"]["responses"])}
        result["checks"] = checks
        args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
        required = {key: value for key, value in checks.items() if key != "engine_bounded_zero_206"}
        if not all(required.values()):
            raise AssertionError(f"M0 probe failed: {checks}")
    print(args.output)


if __name__ == "__main__":
    main()
