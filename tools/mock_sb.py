#!/usr/bin/env python3
"""Mock Streamer.bot WebSocket server for testing the StreamLink client.

Speaks just enough of Streamer.bot's protocol: Hello (with authentication), Authenticate,
Subscribe, and DoAction. The game (or tools/StreamLinkHarness) connects to it as it would
to the real Streamer.bot.

Modes:
  script (default)  Send a scripted set of commands (both message formats, duplicates, bad
                    params), check every ack, then drop the connection and check that the
                    client reconnects and re-authenticates. Exits 0 on success. Assumes the
                    game is in a song on five-fret guitar/bass and not paused.
  soak              Send a random command every --interval seconds for --duration seconds
                    and log the acks. For the in-game bot test.

Requires: pip install websockets
"""

import argparse
import asyncio
import base64
import hashlib
import json
import random
import sys
import time
import uuid

import websockets

SALT = base64.b64encode(b"mock-salt").decode()


def expected_auth(password: str, salt: str, challenge: str) -> str:
    secret = base64.b64encode(hashlib.sha256((password + salt).encode()).digest()).decode()
    return base64.b64encode(hashlib.sha256((secret + challenge).encode()).digest()).decode()


def command(mod=None, cmd="apply", params=None, duration_ms=None, cmd_id=None, user="MockViewer"):
    obj = {
        "v": 1,
        "id": cmd_id or str(uuid.uuid4()),
        "cmd": cmd,
        "source": {"platform": "test", "user": user, "trigger": "mock", "amount": 0,
                   "rewardId": "", "redemptionId": "mock-" + (cmd_id or "r")},
    }
    if mod is not None:
        obj["mod"] = mod
    if params is not None:
        obj["params"] = params
    if duration_ms is not None:
        obj["durationMs"] = duration_ms
    return obj


def wrap(obj, as_string=False):
    return {"event": {"source": "General", "type": "Custom"},
            "data": json.dumps(obj) if as_string else obj}


class Session:
    """One client connection: handshake, then a queue of incoming acks."""

    def __init__(self, ws, password):
        self.ws = ws
        self.password = password
        self.acks = asyncio.Queue()
        self.subscribed = asyncio.Event()
        self.auth_ok = None
        self.reader = None

    async def handshake(self):
        challenge = base64.b64encode(uuid.uuid4().bytes).decode()
        await self.ws.send(json.dumps({
            "request": "Hello",
            "info": {"instanceId": "mock", "name": "Streamer.bot (mock)", "version": "1.0.0"},
            "authentication": {"challenge": challenge, "salt": SALT},
            "timestamp": time.strftime("%Y-%m-%dT%H:%M:%S"),
        }))
        self.challenge = challenge
        self.reader = asyncio.create_task(self._read())

    async def _read(self):
        try:
            async for raw in self.ws:
                msg = json.loads(raw)
                req = msg.get("request")
                if req == "Authenticate":
                    ok = msg.get("authentication") == expected_auth(self.password, SALT, self.challenge)
                    self.auth_ok = ok
                    await self.ws.send(json.dumps(
                        {"id": msg.get("id"), "status": "ok"} if ok else
                        {"id": msg.get("id"), "status": "error", "error": "Authentication failed"}))
                    if not ok:
                        await self.ws.close()
                        return
                elif req == "Subscribe":
                    if not self.auth_ok:
                        await self.ws.send(json.dumps(
                            {"id": msg.get("id"), "status": "error", "error": "Not authenticated"}))
                        continue
                    events = msg.get("events", {})
                    if "Custom" not in events.get("General", []):
                        print(f"  ! unexpected Subscribe events: {events}")
                    await self.ws.send(json.dumps({"id": msg.get("id"), "status": "ok", "events": events}))
                    self.subscribed.set()
                elif req == "DoAction":
                    await self.ws.send(json.dumps({"id": msg.get("id"), "status": "ok"}))
                    if msg.get("action", {}).get("name") != "StreamLink Ack":
                        print(f"  ! DoAction for unexpected action: {msg.get('action')}")
                    await self.acks.put(msg.get("args", {}))
                else:
                    print(f"  ? unhandled message: {raw}")
        except websockets.ConnectionClosed:
            pass

    async def send(self, obj):
        await self.ws.send(json.dumps(obj))

    async def next_ack(self, timeout=3.0):
        return await asyncio.wait_for(self.acks.get(), timeout)

    async def ack_for(self, cmd_id, timeout=3.0):
        """Waits for the applied/rejected ack of a command, skipping 'expired' notices."""
        deadline = time.monotonic() + timeout
        while True:
            ack = await self.next_ack(max(0.01, deadline - time.monotonic()))
            if ack.get("status") != "expired" and ack.get("cmdId") == cmd_id:
                return ack
            if ack.get("status") != "expired":
                print(f"  ! out-of-order ack {ack}")


class MockServer:
    def __init__(self, password):
        self.password = password
        self.sessions = asyncio.Queue()

    async def handler(self, ws):
        session = Session(ws, self.password)
        await session.handshake()
        await self.sessions.put(session)
        await ws.wait_closed()

    async def next_session(self, timeout):
        session = await asyncio.wait_for(self.sessions.get(), timeout)
        await asyncio.wait_for(session.subscribed.wait(), timeout)
        return session


async def run_script(server: MockServer, connect_timeout: float) -> bool:
    failures = 0

    def check(name, cond, detail=""):
        nonlocal failures
        print(f"  {'PASS' if cond else 'FAIL'}  {name}{('  ' + detail) if detail and not cond else ''}")
        if not cond:
            failures += 1

    print(f"Waiting up to {connect_timeout:.0f}s for the client to connect...")
    session = await server.next_session(connect_timeout)
    check("client authenticated", session.auth_ok is True)
    check("client subscribed to General.Custom", session.subscribed.is_set())

    async def expect(name, message, cmd_id, status, reason=""):
        await session.send(message)
        try:
            ack = await session.ack_for(cmd_id)
        except asyncio.TimeoutError:
            check(name, False, "no ack")
            return None
        ok = ack.get("status") == status and ack.get("reason", "") == reason
        check(name, ok, f"got {ack.get('status')}/{ack.get('reason')}, want {status}/{reason}")
        return ack

    ping = command(cmd="ping")
    await expect("raw ping is acked", ping, ping["id"], "applied")

    mirror = command("mirror", duration_ms=5000)
    ack = await expect("wrapped (object) mirror applies", wrap(mirror), mirror["id"], "applied")
    if ack:
        check("ack echoes user and redemptionId",
              ack.get("user") == "MockViewer" and ack.get("redemptionId") == mirror["source"]["redemptionId"])
        check("ack reports endsInMs", ack.get("endsInMs") == 5000, str(ack.get("endsInMs")))

    taps = command("allTaps", duration_ms=5000)
    await expect("wrapped (string) allTaps applies", wrap(taps, as_string=True), taps["id"], "applied")
    await expect("duplicate id is rejected", taps, taps["id"], "rejected", "duplicate")

    bad_speed = command("speed", params={})
    await expect("speed without value is invalid", bad_speed, bad_speed["id"], "rejected", "invalid_params")
    unknown = command("doubleNotes")
    await expect("unknown mod is invalid", unknown, unknown["id"], "rejected", "invalid_params")

    speed = command("speed", params={"value": 1.25}, duration_ms=5000)
    await expect("speed 1.25 applies", speed, speed["id"], "applied")
    speed2 = command("speed", params={"value": 0.8})
    await expect("second speed within 3 s is rate limited", speed2, speed2["id"], "rejected", "rate_limited")

    shuffle = command("shuffle", params={"seed": 42}, duration_ms=5000)
    await expect("shuffle with seed applies", shuffle, shuffle["id"], "applied")

    clear = command(cmd="clear")
    await expect("clear applies", clear, clear["id"], "applied")

    # Other traffic on the socket must be ignored without an ack
    await session.send({"event": {"source": "Twitch", "type": "ChatMessage"}, "data": {"message": "hi"}})
    await session.send({"event": {"source": "General", "type": "Custom"}, "data": {"unrelated": True}})
    probe = command(cmd="ping")
    ack = await expect("unrelated events are ignored", probe, probe["id"], "applied")

    # Expiry notice: a short effect should report 'expired' after its duration
    short = command("allHopos", duration_ms=5000)
    await expect("allHopos applies", short, short["id"], "applied")
    try:
        deadline = time.monotonic() + 8
        expired = None
        while expired is None:
            a = await session.next_ack(max(0.01, deadline - time.monotonic()))
            if a.get("status") == "expired" and a.get("cmdId") == short["id"]:
                expired = a
        check("expired notice after duration", True)
    except asyncio.TimeoutError:
        check("expired notice after duration", False, "none within 8 s")

    print("Dropping the connection to test reconnect...")
    started = time.monotonic()
    await session.ws.close()
    try:
        session = await server.next_session(15)
        elapsed = time.monotonic() - started
        check("client reconnected and re-authenticated within 10 s", elapsed <= 10, f"{elapsed:.1f}s")
        check("re-authenticated", session.auth_ok is True)
        after = command(cmd="ping")
        await expect("commands work after reconnect", after, after["id"], "applied")
    except asyncio.TimeoutError:
        check("client reconnected within 15 s", False)

    print(f"\n{'OK' if failures == 0 else f'{failures} FAILURE(S)'}")
    return failures == 0


async def run_soak(server: MockServer, duration: float, interval: float, connect_timeout: float):
    session = await server.next_session(connect_timeout)
    print("Connected. Sending random commands; Ctrl+C to stop.")
    choices = [
        lambda: command("speed", params={"value": round(random.uniform(0.6, 1.5), 2)}),
        lambda: command("allTaps"), lambda: command("allHopos"), lambda: command("allStrums"),
        lambda: command("shuffle"), lambda: command("mirror"),
    ]
    counts = {}
    end = time.monotonic() + duration
    while time.monotonic() < end:
        cmd = random.choice(choices)()
        cmd["durationMs"] = random.randint(5, 20) * 1000
        await session.send(wrap(cmd) if random.random() < 0.5 else cmd)
        await asyncio.sleep(interval)
        while not session.acks.empty():
            ack = session.acks.get_nowait()
            key = f"{ack.get('status')}/{ack.get('reason') or '-'}"
            counts[key] = counts.get(key, 0) + 1
            print(f"  {ack.get('mod'):>9}  {key}")
    await session.send(command(cmd="clear"))
    print("Totals:", counts)


async def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--password", default="mock-password")
    parser.add_argument("--mode", choices=["script", "soak"], default="script")
    parser.add_argument("--duration", type=float, default=300, help="soak: seconds to run")
    parser.add_argument("--interval", type=float, default=3, help="soak: seconds between commands")
    parser.add_argument("--connect-timeout", type=float, default=60)
    args = parser.parse_args()

    server = MockServer(args.password)
    async with websockets.serve(server.handler, args.host, args.port):
        print(f"Mock Streamer.bot listening on ws://{args.host}:{args.port}/ (password: {args.password})")
        if args.mode == "script":
            ok = await run_script(server, args.connect_timeout)
            sys.exit(0 if ok else 1)
        else:
            await run_soak(server, args.duration, args.interval, args.connect_timeout)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
