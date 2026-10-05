// osu! chat bot. Normal mode: the shared bot runs on our server and passes the commands here (WebSocket).
// Test mode (OwnAccount): the program itself connects to osu! chat (IRC) with a bot account.
// Only linked osu! users can use it. Commands are sent as private messages to the bot:
//   !help                  list of commands
//   !link 123456            link your osu! account (the code is shown in the program)
//   !<category> <n|name>   insert a pattern, e.g. "!tech 1" or "!slider kare"
//   !p <name>              insert a pattern by name (any category)
//   !list [category]       show patterns
//   !undo                  undo the last insert

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

class BotSettings
{
    public string Username { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";     // encrypted for this Windows user (DPAPI)
    public List<string> Linked { get; set; } = new List<string>();
    public bool AutoStart { get; set; }
    public Dictionary<string, string> UserLang { get; set; } = new Dictionary<string, string>();   // osu! name -> language code
    public bool OwnAccount { get; set; }                     // false = use the shared bot on our server (normal), true = own bot account (test)
    public string DeviceId { get; set; } = "";               // this program's id on the server
    public string DeviceToken { get; set; } = "";            // secret that proves it is this program
    public string ServerUrl { get; set; } = "";              // empty = read from GitHub (packs/server.json)

    static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "bot.json");

    public static BotSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<BotSettings>(File.ReadAllText(FilePath)) ?? new BotSettings(); }
        catch { }
        return new BotSettings();
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    public void SetPassword(string password)
    {
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(password ?? ""), null, DataProtectionScope.CurrentUser);
        ProtectedPassword = Convert.ToBase64String(data);
    }

    public string GetPassword()
    {
        try
        {
            if (string.IsNullOrEmpty(ProtectedPassword)) return "";
            var data = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch { return ""; }
    }
}

class OsuBot
{
    public BotSettings Settings = BotSettings.Load();
    public string LinkCode { get; private set; } = NewCode();
    public string State { get; private set; } = "off";        // off, connecting, on, error
    public string LastError { get; private set; } = "";
    public string BotName { get; private set; } = "";          // server mode: name of the shared bot account
    public List<string> ServerLinked { get; private set; } = new List<string>();
    // own bot account (old test mode) only works with an empty "bot.own" file next to the exe; normally everybody uses the server
    static readonly bool OwnAllowed = File.Exists(Path.Combine(AppContext.BaseDirectory, "bot.own"));
    public bool UseServer => !(OwnAllowed && Settings.OwnAccount);
    public List<string> LinkedNames => UseServer ? ServerLinked : Settings.Linked;

    // what the bot needs from the program (all called on the UI thread)
    public Func<string, string> InsertPattern;                 // path -> result text (throws on error)
    public Func<string> Undo;
    public Func<List<(string path, string cat, string name)>> ListPatterns;
    public Action<Action> OnUi;                                // run on the UI thread
    public Action Changed;                                     // state changed -> refresh the window
    public Action<string> Log;

    TcpClient client;
    ClientWebSocket socket;
    readonly SemaphoreSlim wsLock = new SemaphoreSlim(1, 1);
    StreamWriter writer;
    CancellationTokenSource cts;
    readonly object sendLock = new object();
    DateTime lastSend = DateTime.MinValue;

    static string NewCode() => RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
    static string Norm(string name) => (name ?? "").Trim().Replace(' ', '_').ToLowerInvariant();

    // each user gets answers in their own language (set with !lang), otherwise in the program's language
    string LangOf(string nick)
    {
        foreach (var kv in Settings.UserLang)
            if (Norm(kv.Key) == Norm(nick)) return kv.Value;
        return Lang.Current;
    }

    string L(string nick, string key, params object[] args) => Lang.TFor(LangOf(nick), key, args);

    public bool IsLinked(string nick) => Settings.Linked.Any(l => Norm(l) == Norm(nick));

    public void Start()
    {
        Stop();
        if (UseServer)
        {
            cts = new CancellationTokenSource();
            var st = cts.Token;
            SetState("connecting", "");
            Task.Run(() => ServerRun(st));
            return;
        }
        if (string.IsNullOrWhiteSpace(Settings.Username) || string.IsNullOrEmpty(Settings.GetPassword()))
        {
            SetState("error", Lang.T("botNeedLogin"));
            return;
        }
        cts = new CancellationTokenSource();
        var token = cts.Token;
        SetState("connecting", "");
        Task.Run(() => Run(token));
    }

    public void Stop()
    {
        try { cts?.Cancel(); } catch { }
        try { client?.Close(); } catch { }
        try { socket?.Abort(); } catch { }
        client = null; writer = null; socket = null;
        if (State != "off") SetState("off", "");
    }

    void SetState(string state, string error)
    {
        State = state; LastError = error;
        OnUi?.Invoke(() => Changed?.Invoke());
    }

    static readonly string LogFile = Path.Combine(AppContext.BaseDirectory, "botlog.txt");

    // keeps a small log of what the server said (never the password), to find connection problems
    static void FileLog(string text)
    {
        try
        {
            File.AppendAllText(LogFile, DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            var fi = new FileInfo(LogFile);
            if (fi.Length > 200_000) File.WriteAllLines(LogFile, File.ReadAllLines(LogFile).TakeLast(500));
        }
        catch { }
    }

    async Task Run(CancellationToken token)
    {
        int delay = 5;
        string lastError = "";
        while (!token.IsCancellationRequested)
        {
            bool welcomed = false;
            try
            {
                FileLog("connecting to irc.ppy.sh:6667 as " + Settings.Username);
                client = new TcpClient();
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                    try { await client.ConnectAsync("irc.ppy.sh", 6667, connectTimeout.Token); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    { throw new Exception(Lang.T("botNoServer")); }
                }
                var stream = client.GetStream();
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

                string nick = Settings.Username.Trim().Replace(' ', '_');
                Raw("PASS " + Settings.GetPassword());
                Raw("NICK " + nick);
                Raw("USER " + nick + " 0 * :" + nick);
                FileLog("login sent");

                while (!token.IsCancellationRequested)
                {
                    string line;
                    if (!welcomed)
                    {
                        // the server must answer the login within 20 seconds
                        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                        wait.CancelAfter(TimeSpan.FromSeconds(20));
                        try { line = await reader.ReadLineAsync(wait.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        { throw new Exception(Lang.T("botNoAnswer")); }
                    }
                    else line = await reader.ReadLineAsync(token);

                    if (line == null)
                        throw new Exception(welcomed ? Lang.T("botClosed") : Lang.T("botBadLogin"));
                    if (line.StartsWith("PING")) { Raw("PONG" + line.Substring(4)); continue; }
                    var parts = line.Split(' ', 4);
                    if (parts.Length < 2) continue;
                    // log the login and anything unusual, but not the flood of JOIN / QUIT / channel messages
                    bool noise = parts[1] == "QUIT" || parts[1] == "JOIN" || parts[1] == "PART" || parts[1] == "MODE" || parts[1] == "372"
                                 || (parts[1] == "PRIVMSG" && parts.Length > 2 && parts[2].StartsWith("#"));
                    if (!noise) FileLog("< " + (line.Length > 200 ? line.Substring(0, 200) : line));

                    if (parts[1] == "001")
                    {
                        welcomed = true; delay = 5; lastError = "";
                        SetState("on", ""); Log?.Invoke(Lang.T("botOnline"));
                    }
                    else if (parts[1] == "464") { FileLog("bad login"); SetState("error", Lang.T("botBadLogin")); return; }   // wrong password: do not retry
                    else if (parts[1] == "PRIVMSG" && parts.Length == 4 && parts[0].StartsWith(":"))
                    {
                        string from = parts[0].Substring(1).Split('!')[0];
                        string target = parts[2];
                        string text = parts[3].StartsWith(":") ? parts[3].Substring(1) : parts[3];
                        if (target.StartsWith("#")) continue;            // only private messages
                        if (text.Length > 0 && text[0] == '\u0001') continue;   // /me actions etc. (char check: StartsWith("\u0001") is always true on .NET)
                        FileLog("message from " + from + ": " + text);
                        { string to = from; Handle(from, text.Trim(), msg => Reply(to, msg), false); }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;
                lastError = ex.Message;
                FileLog("error: " + ex.Message);
            }
            finally
            {
                try { client?.Close(); } catch { }
            }
            if (token.IsCancellationRequested) return;
            SetState("connecting", Lang.T("botRetry", lastError, delay));
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch { return; }
            delay = Math.Min(delay * 2, 120);
        }
    }

    // ---------------------------------------------------------------- server mode (normal)
    // The shared bot runs on our server. This program connects to it with a WebSocket; the server passes
    // the chat commands of the linked osu! accounts here, and sends our answers back to the chat.
    const string ServerInfoUrl = "https://raw.githubusercontent.com/thaism442/osu-pattern-gallery/main/packs/server.json";

    async Task<string> GetServerUrl(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(Settings.ServerUrl)) return Settings.ServerUrl.Trim();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PatternGallery");
        string json;
        try { json = await http.GetStringAsync(ServerInfoUrl + "?t=" + DateTime.UtcNow.Ticks, token); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { throw new Exception(Lang.T("srvNone")); }
        catch (HttpRequestException) { throw new Exception(Lang.T("srvNoConn")); }
        using var doc = JsonDocument.Parse(json);
        string url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url)) throw new Exception(Lang.T("srvNone"));
        return url;
    }

    async Task ServerRun(CancellationToken token)
    {
        if (string.IsNullOrEmpty(Settings.DeviceId) || string.IsNullOrEmpty(Settings.DeviceToken))
        {
            Settings.DeviceId = Guid.NewGuid().ToString("N");
            Settings.DeviceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            Settings.Save();
        }
        int delay = 3;
        while (!token.IsCancellationRequested)
        {
            string error = "";
            try
            {
                string url = await GetServerUrl(token);
                FileLog("connecting to server " + url);
                var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket = ws;
                using (var t = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    t.CancelAfter(TimeSpan.FromSeconds(15));
                    try { await ws.ConnectAsync(new Uri(url), t.Token); }
                    catch (Exception) when (!token.IsCancellationRequested) { throw new Exception(Lang.T("srvNoConn")); }
                }
                await WsSend(new
                {
                    type = "hello",
                    device = Settings.DeviceId,
                    token = Settings.DeviceToken,
                    strings = new { linked = Lang.T("botLinked"), offline = Lang.T("botOffline"), busy = Lang.T("botBusy") }
                });
                while (!token.IsCancellationRequested)
                {
                    string text = await WsReceive(ws, token);
                    if (text == null) throw new Exception(Lang.T("srvClosed"));
                    using var doc = JsonDocument.Parse(text);
                    var m = doc.RootElement;
                    string type = m.TryGetProperty("type", out var tv) ? tv.GetString() : "";
                    switch (type)
                    {
                        case "welcome":
                            LinkCode = m.GetProperty("code").GetString();
                            BotName = m.TryGetProperty("bot", out var bn) ? bn.GetString() : "";
                            ServerLinked = Names(m, "linked");
                            delay = 3;
                            FileLog("server: online, " + ServerLinked.Count + " linked");
                            SetState("on", "");
                            break;
                        case "code":
                            LinkCode = m.GetProperty("code").GetString();
                            OnUi?.Invoke(() => Changed?.Invoke());
                            break;
                        case "linked":
                            ServerLinked = Names(m, "linked");
                            OnUi?.Invoke(() => Changed?.Invoke());
                            break;
                        case "cmd":
                        {
                            string id = m.GetProperty("id").GetString();
                            string from = m.GetProperty("from").GetString();
                            string cmdText = m.GetProperty("text").GetString() ?? "";
                            FileLog("message from " + from + ": " + cmdText);
                            Handle(from, cmdText.Trim(), msg => _ = WsSend(new { type = "reply", id, text = msg }), true);
                            break;
                        }
                        case "error":
                            throw new Exception(m.TryGetProperty("text", out var et) ? et.GetString() : "?");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex) { error = ex.Message; FileLog("server error: " + ex.Message); }
            finally { try { socket?.Abort(); socket?.Dispose(); } catch { } socket = null; }
            if (token.IsCancellationRequested) return;
            SetState("connecting", Lang.T("botRetry", error, delay));
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch { return; }
            delay = Math.Min(delay * 2, 60);
        }
    }

    static List<string> Names(JsonElement m, string prop)
    {
        var list = new List<string>();
        if (m.TryGetProperty(prop, out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString());
        return list;
    }

    async Task WsSend(object payload)
    {
        var ws = socket;
        if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await wsLock.WaitAsync();
        try { await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
        finally { wsLock.Release(); }
    }

    static async Task<string> WsReceive(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > 1_000_000) return null;
            if (r.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    // server mode: remove a linked osu! account
    public void Unlink(string name)
    {
        if (UseServer) { _ = WsSend(new { type = "unlink", user = name }); return; }
        Settings.Linked.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        OnUi?.Invoke(() => Changed?.Invoke());
    }

    // server mode: ask for a new link code
    public void NewLinkCode()
    {
        if (UseServer) _ = WsSend(new { type = "newcode" });
        else { LinkCode = NewCode(); OnUi?.Invoke(() => Changed?.Invoke()); }
    }

    // sends a test message so we can see if messages get through
    public bool SendTest(string to)
    {
        if (UseServer || State != "on" || string.IsNullOrWhiteSpace(to)) return false;
        string target = to.Trim();
        FileLog("> test message to " + target);
        Task.Run(() => Reply(target, L(target, "botTestMsg", LinkCode)));
        return true;
    }

    void Raw(string text)
    {
        lock (sendLock) { writer?.WriteLine(text); }
    }

    // osu! chat allows only a few messages per second: keep at least 1.2 s between replies
    void Reply(string to, string text)
    {
        lock (sendLock)
        {
            var wait = lastSend.AddMilliseconds(1200) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            if (text.Length > 400) text = text.Substring(0, 397) + "...";
            writer?.WriteLine("PRIVMSG " + to.Replace(' ', '_') + " :" + text);
            lastSend = DateTime.UtcNow;
        }
    }

    void Handle(string from, string text, Action<string> reply, bool trusted)
    {
        if (text.Length == 0 || text[0] != '!') return;
        var words = text.Substring(1).Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) { if (trusted) reply(L(from, "botHelp")); return; }
        string cmd = words[0].ToLowerInvariant();
        string arg = words.Length > 1 ? words[1].Trim() : "";

        if (cmd == "lang" || cmd == "language" || cmd == "dil")
        {
            string code = Lang.Normalize(arg);
            if (code == null) { reply(L(from, "botLangList", string.Join(", ", Lang.Codes))); return; }
            Settings.UserLang[from] = code;
            Settings.Save();
            reply(L(from, "botLangSet"));
            return;
        }

        if (cmd == "link" && !trusted)
        {
            if (arg == LinkCode)
            {
                if (!IsLinked(from)) Settings.Linked.Add(from);
                Settings.Save();
                LinkCode = NewCode();                      // a code works only once
                OnUi?.Invoke(() => Changed?.Invoke());
                reply(L(from, "botLinked"));
            }
            else reply(L(from, "botBadCode"));
            return;
        }

        if (!trusted && !IsLinked(from)) { reply(L(from, "botNotLinked")); return; }

        switch (cmd)
        {
            case "help":
                reply(L(from, "botHelp"));
                return;
            case "undo":
                RunOnUi(from, reply, () => Undo());
                return;
            case "list":
                OnUi?.Invoke(() =>
                {
                    var all = ListPatterns();
                    var cats = all.Select(p => p.cat).Where(c => c != "").Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList();
                    string msg;
                    if (arg == "")
                        msg = L(from, "botCats", cats.Count == 0 ? "-" : string.Join(", ", cats));
                    else
                    {
                        var inCat = all.Where(p => string.Equals(p.cat, arg, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.name).ToList();
                        msg = inCat.Count == 0 ? L(from, "botNoCat", arg)
                            : arg + ": " + string.Join(", ", inCat.Select((p, i) => (i + 1) + ") " + p.name));
                    }
                    Task.Run(() => reply(msg));
                });
                return;
            case "p":
            case "pattern":
                InsertBy(from, reply, null, arg);
                return;
            default:
                InsertBy(from, reply, cmd, arg);       // "!tech 1", "!slider kare"
                return;
        }
    }

    void InsertBy(string from, Action<string> reply, string category, string arg)
    {
        OnUi?.Invoke(() =>
        {
            var all = ListPatterns();
            var pool = category == null ? all
                : all.Where(p => string.Equals(p.cat, category, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.name).ToList();
            if (category != null && pool.Count == 0) { Task.Run(() => reply(L(from, "botUnknown", category))); return; }

            (string path, string cat, string name) pick = default;
            if (int.TryParse(arg, out int n) && n >= 1 && n <= pool.Count) pick = pool[n - 1];
            else if (arg != "")
                pick = pool.FirstOrDefault(p => string.Equals(p.name, arg, StringComparison.OrdinalIgnoreCase));
            if (arg != "" && pick.path == null)
                pick = pool.FirstOrDefault(p => p.name.Contains(arg, StringComparison.OrdinalIgnoreCase));
            if (pick.path == null) { Task.Run(() => reply(L(from, "botNotFound", arg))); return; }

            string result;
            try { result = Lang.With(LangOf(from), () => { Core.LastChatText = null; string r = InsertPattern(pick.path); return Core.LastChatText ?? r; }); }
            catch (Exception ex) { result = L(from, "botError", ex.Message); }
            Log?.Invoke("[" + from + "] " + result);
            Task.Run(() => reply(result));
        });
    }

    void RunOnUi(string from, Action<string> reply, Func<string> work)
    {
        OnUi?.Invoke(() =>
        {
            string result;
            try { result = Lang.With(LangOf(from), work); }
            catch (Exception ex) { result = L(from, "botError", ex.Message); }
            Task.Run(() => reply(result));
        });
    }
}
