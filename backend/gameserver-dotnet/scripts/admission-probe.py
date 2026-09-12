#!/usr/bin/env python3
"""Live probe for the game server's admission hardening (workspace audit F03/F04).

Exercises a RUNNING game server over real sockets, no build tooling, no third-party
modules -- python3 stdlib only. Prints PASS/FAIL per check and exits non-zero on any
FAIL. See docs/RUNBOOK.md, "Probing admission hardening".

Checks:
  1. idle       an idle connection is closed at GAMESERVER_HANDSHAKE_TIMEOUT_MS
  2. pool       N+1 idle connections above GAMESERVER_MAX_PENDING_HANDSHAKES: the
                surplus is closed at accept; the pending gauge and the pool_full
                counter on /metrics move
  3. malformed  a partial length prefix is closed at the deadline (timeout), a
                complete-but-undecodable frame is closed at once (malformed), and
                the pending gauge returns to its baseline
  4. flood      one authenticated connection floods MsgInput; the drop and coalesce
                counters move and a second authenticated player's input is still
                acknowledged (ack_tick) in its snapshot stream

The server must have been started with the SAME values you pass here for
--max-pending and --timeout-ms, and its join-token secret / server id must match
--secret / --server-id (check 4 mints join tokens itself, HS256).
"""

import argparse
import base64
import hashlib
import hmac
import json
import select
import socket
import struct
import sys
import time
import urllib.request
import uuid

MSG_JOIN_TOKEN = 5
MSG_JOIN_TOKEN_RESP = 6
MSG_INPUT = 7
MSG_SNAPSHOT = 8

FAILURES = []


def report(name, ok, detail):
    tag = "PASS" if ok else "FAIL"
    print(f"[{tag}] {name}: {detail}")
    if not ok:
        FAILURES.append(name)


# ── wire helpers (legacy JSON envelope: 4-byte BE length + {"type":N,"payload":{}}) ──

def frame(msg_type, payload):
    body = json.dumps({"type": msg_type, "payload": payload}, separators=(",", ":")).encode()
    return struct.pack(">I", len(body)) + body


def read_exact(sock, n, deadline):
    buf = b""
    while len(buf) < n:
        sock.settimeout(max(0.01, deadline - time.monotonic()))
        chunk = sock.recv(n - len(buf))
        if not chunk:
            return None  # EOF
        buf += chunk
    return buf


def read_frame(sock, timeout):
    deadline = time.monotonic() + timeout
    hdr = read_exact(sock, 4, deadline)
    if hdr is None:
        return None
    (length,) = struct.unpack(">I", hdr)
    body = read_exact(sock, length, deadline)
    if body is None:
        return None
    return json.loads(body)


def connect(host, port):
    s = socket.create_connection((host, port), timeout=10)
    s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    return s


def wait_eof(sock, budget):
    """Seconds until the peer closed, or None if it did not within budget."""
    start = time.monotonic()
    deadline = start + budget
    try:
        while True:
            sock.settimeout(max(0.01, deadline - time.monotonic()))
            data = sock.recv(4096)
            if not data:
                return time.monotonic() - start
            # a reply frame before the close is fine; keep reading
    except (socket.timeout, TimeoutError):
        return None
    except (ConnectionResetError, BrokenPipeError, OSError):
        return time.monotonic() - start


# ── /metrics helpers ─────────────────────────────────────────────────────────────

def scrape(metrics_url):
    with urllib.request.urlopen(metrics_url.rstrip("/") + "/metrics", timeout=5) as r:
        return r.read().decode()


def metric(text, name, label_contains=None):
    """Sum of all samples of `name` (optionally only those whose label set contains a substring)."""
    total = 0.0
    found = False
    for line in text.splitlines():
        if not line.startswith(name):
            continue
        rest = line[len(name):]
        if rest and rest[0] not in "{ ":
            continue  # a longer name with the same prefix
        labels = ""
        if rest.startswith("{"):
            end = rest.index("}")
            labels, rest = rest[1:end], rest[end + 1:]
        if label_contains and label_contains not in labels:
            continue
        try:
            total += float(rest.split()[0])
            found = True
        except (ValueError, IndexError):
            pass
    return total if found else 0.0


def wait_metric(metrics_url, name, predicate, budget=10.0, label_contains=None):
    deadline = time.monotonic() + budget
    val = None
    while time.monotonic() < deadline:
        val = metric(scrape(metrics_url), name, label_contains)
        if predicate(val):
            return val
        time.sleep(0.2)
    return val


# ── join token ──────────────────────────────────────────────────────────────────

def b64url(b):
    return base64.urlsafe_b64encode(b).rstrip(b"=").decode()


def mint_join_token(user_id, server_id, secret):
    header = b64url(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
    now = int(time.time())
    claims = {"sub": user_id, "sid": server_id, "jti": uuid.uuid4().hex, "iat": now, "exp": now + 3600}
    payload = b64url(json.dumps(claims, separators=(",", ":")).encode())
    sig = hmac.new(secret.encode(), f"{header}.{payload}".encode(), hashlib.sha256).digest()
    return f"{header}.{payload}.{b64url(sig)}"


def join(host, port, user_id, server_id, secret):
    s = connect(host, port)
    s.sendall(frame(MSG_JOIN_TOKEN, {"token": mint_join_token(user_id, server_id, secret)}))
    resp = read_frame(s, 10)
    if resp is None or resp.get("type") != MSG_JOIN_TOKEN_RESP:
        raise RuntimeError(f"join {user_id}: unexpected reply {resp!r}")
    if not resp["payload"].get("ok"):
        raise RuntimeError(f"join {user_id} refused: {resp['payload'].get('error')}")
    return s


# ── checks ──────────────────────────────────────────────────────────────────────

def check_idle(a):
    expected = a.timeout_ms / 1000.0
    s = connect(a.host, a.port)
    elapsed = wait_eof(s, expected + 5)
    s.close()
    if elapsed is None:
        report("idle", False, f"not closed within {expected + 5:.1f}s (deadline {expected:.1f}s)")
        return
    ok = 0.5 * expected <= elapsed <= expected + 3
    report("idle", ok, f"closed after {elapsed:.2f}s (deadline {expected:.1f}s)")


def check_pool(a):
    before = scrape(a.metrics)
    pool_before = metric(before, "gameserver_handshakes_rejected_total", 'reason="pool_full"')
    n = a.max_pending
    socks = [connect(a.host, a.port) for _ in range(n + 1)]
    try:
        # Read the gauge FIRST, right after the connects: with the production
        # defaults (256 slots, 5 s deadline) a sequential per-socket EOF scan takes
        # longer than the deadline, so the accepted sockets had already timed out
        # and the gauge read 0 by the time it was sampled (2026-09-07, first run
        # against the merged develop). The scan below only has to find the
        # surplus that was closed at accept, and does it with select() in one go.
        time.sleep(0.3)
        after = scrape(a.metrics)
        pending = metric(after, "gameserver_handshakes_pending")
        pool_after = metric(after, "gameserver_handshakes_rejected_total", 'reason="pool_full"')
        rejected_fast = 0
        readable, _, _ = select.select(socks, [], [], 0.5)
        for s in readable:
            try:
                if s.recv(1) == b"":
                    rejected_fast += 1
            except OSError:
                rejected_fast += 1
        ok = rejected_fast >= 1 and pending == n and pool_after - pool_before >= 1
        report("pool", ok,
               f"{n + 1} idle conns: {rejected_fast} closed at accept, pending gauge={pending:.0f} "
               f"(want {n}), pool_full counter +{pool_after - pool_before:.0f}")
    finally:
        for s in socks:
            s.close()
    # Closed by the client: the server's reads hit EOF and the gauge must drain.
    pending = wait_metric(a.metrics, "gameserver_handshakes_pending", lambda v: v == 0, budget=a.timeout_ms / 1000 + 5)
    report("pool-drain", pending == 0, f"pending gauge back to {pending:.0f}")


def check_malformed(a):
    expected = a.timeout_ms / 1000.0
    base = scrape(a.metrics)
    base_pending = metric(base, "gameserver_handshakes_pending")
    to_before = metric(base, "gameserver_handshakes_rejected_total", 'reason="timeout"')
    mal_before = metric(base, "gameserver_handshakes_rejected_total", 'reason="malformed"')

    # partial length prefix: two of four bytes, then silence -> closed at the deadline
    s = connect(a.host, a.port)
    s.sendall(b"\x00\x00")
    elapsed = wait_eof(s, expected + 5)
    s.close()
    ok = elapsed is not None and 0.5 * expected <= elapsed <= expected + 3
    report("partial-prefix", ok, f"closed after {elapsed if elapsed is None else round(elapsed, 2)}s (deadline {expected:.1f}s)")

    # complete frame, garbage body -> closed immediately
    s = connect(a.host, a.port)
    s.sendall(struct.pack(">I", 8) + b"\xff" * 8)
    elapsed = wait_eof(s, expected + 5)
    s.close()
    ok = elapsed is not None and elapsed < max(1.0, 0.5 * expected)
    report("malformed-frame", ok, f"closed after {elapsed if elapsed is None else round(elapsed, 3)}s (must be well under {expected:.1f}s)")

    after = wait_metric(a.metrics, "gameserver_handshakes_pending", lambda v: v == base_pending, budget=5)
    txt = scrape(a.metrics)
    to_after = metric(txt, "gameserver_handshakes_rejected_total", 'reason="timeout"')
    mal_after = metric(txt, "gameserver_handshakes_rejected_total", 'reason="malformed"')
    ok = after == base_pending and to_after - to_before >= 1 and mal_after - mal_before >= 1
    report("malformed-counters", ok,
           f"pending back to {after:.0f} (baseline {base_pending:.0f}), timeout +{to_after - to_before:.0f}, "
           f"malformed +{mal_after - mal_before:.0f}")


def check_flood(a):
    if not a.secret:
        report("flood", False, "skipped: --secret (JOIN_TOKEN_SECRET) not given")
        return
    before = scrape(a.metrics)
    drop_before = metric(before, "gameserver_inputs_dropped_total")
    coal_before = metric(before, "gameserver_inputs_coalesced_total")

    flooder = join(a.host, a.port, "probe-flooder", a.server_id, a.secret)
    honest = join(a.host, a.port, "probe-honest", a.server_id, a.secret)
    try:
        packets = a.flood_packets
        buf = bytearray()
        for i in range(1, packets + 1):
            if i % 2:
                buf += frame(MSG_INPUT, {"tick": i, "move_x": 0.0, "move_y": 0.0, "attack_target_id": "nobody"})
            else:
                buf += frame(MSG_INPUT, {"tick": i, "move_x": 1.0, "move_y": 0.0})
        t0 = time.monotonic()
        flooder.sendall(bytes(buf))
        sent_in = time.monotonic() - t0

        honest_tick = 5
        honest.sendall(frame(MSG_INPUT, {"tick": honest_tick, "move_x": 0.0, "move_y": 1.0}))
        deadline = time.monotonic() + 15
        last_ack = 0
        frames = 0
        while last_ack < honest_tick and time.monotonic() < deadline:
            env = read_frame(honest, max(0.1, deadline - time.monotonic()))
            if env is None:
                break
            frames += 1
            if env.get("type") == MSG_SNAPSHOT:
                last_ack = int(env["payload"].get("ack_tick", 0))
        report("flood-honest-acked", last_ack >= honest_tick,
               f"second player's input tick {honest_tick} acked as ack_tick={last_ack} after {frames} frames, "
               f"while {packets} inputs were flooded in {sent_in:.2f}s")

        # Poll rather than scrape once: the exporter caches the scrape response briefly,
        # and the server drains the flood on its own tick cadence.
        drop_after = wait_metric(a.metrics, "gameserver_inputs_dropped_total", lambda v: v > drop_before, budget=10)
        coal_after = wait_metric(a.metrics, "gameserver_inputs_coalesced_total", lambda v: v > coal_before, budget=10)
        ok = drop_after > drop_before and coal_after > coal_before
        report("flood-counters", ok,
               f"inputs_dropped_total +{drop_after - drop_before:.0f}, inputs_coalesced_total +{coal_after - coal_before:.0f}")
    finally:
        flooder.close()
        honest.close()


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=9000, help="game traffic port (GAMESERVER_ADDR)")
    p.add_argument("--metrics", default="http://127.0.0.1:9101", help="metrics endpoint base URL (METRICS_ADDR)")
    p.add_argument("--max-pending", type=int, default=256, help="the server's GAMESERVER_MAX_PENDING_HANDSHAKES")
    p.add_argument("--timeout-ms", type=int, default=5000, help="the server's GAMESERVER_HANDSHAKE_TIMEOUT_MS")
    p.add_argument("--secret", default="", help="the server's JOIN_TOKEN_SECRET (needed for the flood check)")
    p.add_argument("--server-id", default="", help="the server's GAMESERVER_ID (join tokens carry it as sid)")
    p.add_argument("--flood-packets", type=int, default=6000)
    p.add_argument("--only", choices=["idle", "pool", "malformed", "flood"], action="append",
                   help="run only these checks (repeatable)")
    a = p.parse_args()

    checks = {"idle": check_idle, "pool": check_pool, "malformed": check_malformed, "flood": check_flood}
    for name, fn in checks.items():
        if a.only and name not in a.only:
            continue
        try:
            fn(a)
        except Exception as e:  # a probe must never die silently
            report(name, False, f"exception: {e!r}")

    if FAILURES:
        print(f"\n{len(FAILURES)} check(s) FAILED: {', '.join(FAILURES)}")
        sys.exit(1)
    print("\nall checks PASSED")


if __name__ == "__main__":
    main()
