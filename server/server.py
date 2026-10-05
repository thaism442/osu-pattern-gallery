"""osu! Pattern Gallery - bot server.

Runs the osu! chat bot (one bot account for everybody) and connects it to the users' programs.

  osu! chat  <-- IRC -->  this server  <-- WebSocket -->  PatternGallery.exe on each user's computer

* A program connects with its device id + secret token and gets a 6 digit link code.
* The user sends "!link <code>" to the bot in osu! -> that osu! account is linked to that program.
* Every other "!..." message from a linked user is passed to their program, which does the work
  (insert / list / undo / lang ...) and sends back the answer, which the bot writes in chat.

Settings (environment variables, see install.sh):
  IRC_USER   bot account name          IRC_PASS   bot account IRC password
  PORT       websocket port (default 8765, behind Caddy for https)
"""
import asyncio, hashlib, json, logging, os, secrets, time
from websockets.asyncio.server import serve

IRC_USER = os.environ.get("IRC_USER", "").strip()
IRC_PASS = os.environ.get("IRC_PASS", "").strip()
PORT = int(os.environ.get("PORT", "8765"))
DATA = os.environ.get("DATA_FILE", "/var/lib/patterngallery/data.json")

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(message)s")
log = logging.getLogger("pg")

# messages the server says itself (the program sends its own translations for linked users)
EN = {
    "notLinked": "Hi! To use me, open osu! Pattern Gallery, click Bot and send me: !link <code>",
    "badCode": "Wrong or old code. Send the code shown in the program's Bot window.",
    "linked": "Your account is linked! Type !help for commands.",
    "offline": "Your Pattern Gallery program is not running (or not connected). Open it and try again.",
    "busy": "The program did not answer in time. Is the osu! editor open?",
    "slow": "Slow down a little please :)",
}


def norm(name):
    return (name or "").strip().replace(" ", "_").lower()


# ------------------------------------------------------------------ storage
class Store:
    def __init__(self, path):
        self.path = path
        self.devices = {}   # device id -> {"token": sha256, "strings": {...}}
        self.links = {}     # osu name (normalized) -> device id
        try:
            with open(path, encoding="utf-8") as f:
                d = json.load(f)
            self.devices, self.links = d.get("devices", {}), d.get("links", {})
        except FileNotFoundError:
            pass

    def save(self):
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        tmp = self.path + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump({"devices": self.devices, "links": self.links}, f)
        os.replace(tmp, self.path)

    def users_of(self, device):
        return [u for u, d in self.links.items() if d == device]


store = Store(DATA)
online = {}        # device id -> websocket
codes = {}         # link code -> (device id, expires)
pending = {}       # request id -> future
irc_out = asyncio.Queue()
last_cmd = {}      # osu name -> time (simple flood protection)
bad_tries = {}     # osu name -> (count, first try time): stops guessing link codes


def h(token):
    return hashlib.sha256(token.encode()).hexdigest()


def new_code(device):
    for c in [c for c, (d, _) in codes.items() if d == device]:
        codes.pop(c, None)
    while True:
        c = str(secrets.randbelow(900000) + 100000)
        if c not in codes:
            codes[c] = (device, time.time() + 3600)
            return c


def text_for(device, key):
    s = (store.devices.get(device) or {}).get("strings") or {}
    return s.get(key) or EN[key]


async def send_ws(device, payload):
    ws = online.get(device)
    if ws:
        try:
            await ws.send(json.dumps(payload))
            return True
        except Exception:
            pass
    return False


# ------------------------------------------------------------------ websocket (programs)
async def ws_handler(ws):
    device = None
    try:
        hello = json.loads(await asyncio.wait_for(ws.recv(), 15))
        if hello.get("type") != "hello":
            return
        device, token = str(hello.get("device", ""))[:64], str(hello.get("token", ""))
        if len(device) < 8 or len(token) < 16:
            return
        known = store.devices.get(device)
        if known and known.get("token") != h(token):
            await ws.send(json.dumps({"type": "error", "text": "bad token"}))
            return
        if not known:
            store.devices[device] = {"token": h(token)}
        store.devices[device]["strings"] = {k: str(v)[:300] for k, v in (hello.get("strings") or {}).items() if k in EN}
        store.save()

        old = online.get(device)
        if old:
            await old.close()
        online[device] = ws
        await ws.send(json.dumps({"type": "welcome", "code": new_code(device), "linked": store.users_of(device), "bot": IRC_USER}))
        log.info("device online %s (%d online)", device[:8], len(online))

        async for raw in ws:
            m = json.loads(raw)
            t = m.get("type")
            if t == "reply":
                fut = pending.pop(m.get("id"), None)
                if fut and not fut.done():
                    fut.set_result(str(m.get("text", ""))[:400])
            elif t == "newcode":
                await ws.send(json.dumps({"type": "code", "code": new_code(device)}))
            elif t == "unlink":
                u = norm(m.get("user"))
                if store.links.get(u) == device:
                    store.links.pop(u)
                    store.save()
                await ws.send(json.dumps({"type": "linked", "linked": store.users_of(device)}))
    except Exception as e:
        log.info("ws closed: %s", e)
    finally:
        if device and online.get(device) is ws:
            online.pop(device, None)


# ------------------------------------------------------------------ chat commands
async def handle_chat(sender, text):
    if not text.startswith("!"):
        return
    user = norm(sender)
    now = time.time()
    if now - last_cmd.get(user, 0) < 1.0:
        return
    last_cmd[user] = now

    word, _, arg = text[1:].partition(" ")
    word = word.lower()

    if word == "link":
        n, since = bad_tries.get(user, (0, now))
        if now - since > 600:
            n, since = 0, now
        if n >= 5:
            await irc_out.put((sender, EN["slow"]))
            return
        entry = codes.get(arg.strip())
        if not entry or entry[1] < now:
            bad_tries[user] = (n + 1, since)
            await irc_out.put((sender, EN["badCode"]))
            return
        bad_tries.pop(user, None)
        device = entry[0]
        codes.pop(arg.strip(), None)
        store.links[user] = device
        store.save()
        await irc_out.put((sender, text_for(device, "linked")))
        await send_ws(device, {"type": "linked", "linked": store.users_of(device)})
        await send_ws(device, {"type": "code", "code": new_code(device)})
        return

    device = store.links.get(user)
    if not device:
        await irc_out.put((sender, EN["notLinked"]))
        return
    if device not in online:
        await irc_out.put((sender, text_for(device, "offline")))
        return

    rid = secrets.token_hex(6)
    fut = asyncio.get_running_loop().create_future()
    pending[rid] = fut
    await send_ws(device, {"type": "cmd", "id": rid, "from": sender, "text": text})
    try:
        answer = await asyncio.wait_for(fut, 25)
    except asyncio.TimeoutError:
        pending.pop(rid, None)
        answer = text_for(device, "busy")
    if answer:
        await irc_out.put((sender, answer))


# ------------------------------------------------------------------ IRC (osu! chat)
async def irc_loop():
    delay = 5
    while True:
        try:
            log.info("connecting to irc.ppy.sh as %s", IRC_USER)
            reader, writer = await asyncio.open_connection("irc.ppy.sh", 6667)
            nick = IRC_USER.replace(" ", "_")
            for line in (f"PASS {IRC_PASS}", f"NICK {nick}", f"USER {nick} 0 * :{nick}"):
                writer.write((line + "\r\n").encode())
            await writer.drain()

            async def sender_task():
                while True:
                    to, msg = await irc_out.get()
                    writer.write(f"PRIVMSG {to.replace(' ', '_')} :{msg}\r\n".encode())
                    await writer.drain()
                    await asyncio.sleep(1.2)       # osu! chat rate limit

            st = asyncio.create_task(sender_task())
            try:
                while True:
                    raw = await reader.readline()
                    if not raw:
                        raise ConnectionError("closed by server")
                    line = raw.decode("utf-8", "replace").rstrip("\r\n")
                    if line.startswith("PING"):
                        writer.write(("PONG" + line[4:] + "\r\n").encode())
                        await writer.drain()
                        continue
                    parts = line.split(" ", 3)
                    if len(parts) < 2:
                        continue
                    if parts[1] == "001":
                        delay = 5
                        log.info("bot online")
                    elif parts[1] == "464":
                        log.error("bad IRC login - check IRC_USER / IRC_PASS")
                        await asyncio.sleep(300)
                        raise ConnectionError("bad login")
                    elif parts[1] == "PRIVMSG" and len(parts) == 4 and not parts[2].startswith("#"):
                        sender = parts[0][1:].split("!")[0]
                        text = parts[3][1:] if parts[3].startswith(":") else parts[3]
                        if text and text[0] != "\x01":
                            asyncio.create_task(handle_chat(sender, text.strip()))
            finally:
                st.cancel()
                writer.close()
        except Exception as e:
            log.info("irc error: %s - retry in %ds", e, delay)
        await asyncio.sleep(delay)
        delay = min(delay * 2, 120)


async def health(connection, request):
    if request.path != "/ws":
        return connection.respond(200, "osu! Pattern Gallery server is running.\n")


async def main():
    if not IRC_USER or not IRC_PASS:
        log.error("IRC_USER / IRC_PASS are not set")
    async with serve(ws_handler, "127.0.0.1", PORT, process_request=health, max_size=2**20):
        await irc_loop()


if __name__ == "__main__":
    asyncio.run(main())
