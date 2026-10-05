// osu! Pattern Gallery - a small pattern library for the osu! stable editor.
// The window is a web page (ui\index.html) shown with WebView2; this file connects it to Core.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

static class Program
{
    [STAThread]
    static void Main()
    {
        Lang.Load();
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}

class MainForm : Form
{
    static readonly string TopmostFile = Path.Combine(AppContext.BaseDirectory, "topmost.txt");

    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(28, 26, 34), AllowExternalDrop = true };
    readonly Timer mapTimer = new Timer { Interval = 2000 };
    readonly Dictionary<string, (DateTime time, Preview.PatternData data)> cache =
        new Dictionary<string, (DateTime time, Preview.PatternData data)>(StringComparer.OrdinalIgnoreCase);
    string lastMap = null;
    readonly OsuBot bot = new OsuBot();
    // The bot is on for everybody (it uses the shared bot on our server). Put an empty file named "bot.disabled" next to the exe to hide it.
    static readonly bool BotEnabled = !File.Exists(Path.Combine(AppContext.BaseDirectory, "bot.disabled"));
    Process osuProc;
    IntPtr osuWindow;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int InternalGetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);


    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainForm()
    {
        Text = "osu! Pattern Gallery";
        Size = new Size(600, 800);
        MinimumSize = new Size(470, 540);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(28, 26, 34);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // the window starts normal (not pinned); "Always on top" can be turned on with the pin button
        TopMost = false;

        Controls.Add(web);
        Load += async (s, e) => await InitWeb();
        mapTimer.Tick += (s, e) => SendMap(false);
    }

    // ---------------------------------------------------------------- hotkey (default F11)
    // Pressing the hotkey in the editor opens a small window to save the selected objects as a pattern.
    // The key is caught with a keyboard hook (KeyHook below), so it works with any key (even F12)
    // and only while the osu! editor is the active window; everywhere else the key works normally.
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    static readonly string HotkeyFile = Path.Combine(AppContext.BaseDirectory, "hotkey.txt");
    uint hkMods = 0, hkVk = 0x7A;      // F11 (the only F key osu! does not use on its own)
    string hkLabel = "F11";
    bool hkActive;
    static readonly string ReplaceFile = Path.Combine(AppContext.BaseDirectory, "replace.txt");
    bool replaceOverlapping = true;
    static readonly string AdaptFile = Path.Combine(AppContext.BaseDirectory, "adapt.txt");
    bool adaptRhythm = true;
    QuickWindow quick;
    CoreWebView2Environment webEnv;
    string lastQuickCategory = "";

    void LoadHotkey()
    {
        try
        {
            if (!File.Exists(HotkeyFile)) return;
            var p = File.ReadAllText(HotkeyFile).Split('|');
            if (p.Length >= 3) { hkMods = uint.Parse(p[0]); hkVk = uint.Parse(p[1]); hkLabel = p[2]; }
        }
        catch { }
    }

    void RegisterHotkey()
    {
        KeyHook.Set(hkVk, (hkMods & 2) != 0, (hkMods & 1) != 0, (hkMods & 4) != 0);
        KeyHook.Enabled = true;
        KeyHook.OsuWindow = FindOsuWindow();
        KeyHook.Pressed = () => BeginInvoke(new Action(OnHotkey));
        hkActive = KeyHook.Start();
        if (!hkActive) SendStatus("hotkey", false, Lang.T("hotkeyFail", hkLabel));
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    // Hotkey pressed in the editor: opens the small "Save pattern / Insert pattern" window.
    // Objects selected in the editor -> "Save" tab, nothing selected -> "Insert" tab. Pressing it again closes it.
    void OnHotkey()
    {
        if (quick == null || !quick.Ready) return;
        if (quick.Visible) { CloseQuick(null); return; }

        IntPtr osuWnd = FindOsuWindow();
        var sb = new System.Text.StringBuilder(512);
        if (osuWnd != IntPtr.Zero) InternalGetWindowText(osuWnd, sb, sb.Capacity);
        if (!sb.ToString().EndsWith(".osu"))
        {
            SendStatus("hotkey", false, Lang.T("editorNotOpen"));
            return;
        }
        OpenQuick(Core.SelectedCount() > 0 ? "save" : "insert", osuWnd);
    }

    // also used by the main window's "Insert pattern" / "Save pattern" buttons
    void OpenQuick(string mode, IntPtr returnTo)
    {
        if (quick == null || !quick.Ready) return;
        Directory.CreateDirectory(Core.PatternDir);
        string root = Path.GetFullPath(Core.PatternDir);
        var cats = Directory.GetDirectories(Core.PatternDir).Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var list = new List<(string path, string cat, string name, Preview.PatternData data)>();
        foreach (var f in Directory.GetFiles(Core.PatternDir, "*.osu", SearchOption.AllDirectories))
        {
            var parts = Path.GetRelativePath(root, Path.GetFullPath(f)).Split(Path.DirectorySeparatorChar);
            list.Add((f, parts.Length > 1 ? parts[0] : "", Path.GetFileNameWithoutExtension(f), GetData(f)));
        }
        // numbers inside each category, the same as the bot ("!jumps 1")
        var patterns = new List<object>();
        foreach (var g in list.GroupBy(x => x.cat, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key == "" ? "~" : g.Key, StringComparer.OrdinalIgnoreCase))
        {
            int n = 1;
            foreach (var x in g.OrderBy(x => x.name, StringComparer.OrdinalIgnoreCase))
                patterns.Add(new { path = x.path, cat = x.cat, name = x.name, index = n++, count = x.data.count, bpm = x.data.bpm, objs = x.data.objs });
        }

        quick.ReturnTo = returnTo;
        quick.Post(new { type = "quick", mode, lang = Lang.Current, categories = cats, patterns, lastCat = lastQuickCategory });
        quick.ShowOver(returnTo);
        KeyHook.QuickWindow = quick.Handle;
    }

    // closes the small window and goes back to osu! once Esc / F11 is released (so osu! never gets that key)
    void CloseQuick(string insertPath)
    {
        if (quick == null || !quick.Visible) return;
        quick.Hide();
        IntPtr back = quick.ReturnTo;
        Task.Run(async () =>
        {
            for (int i = 0; i < 50 && ((GetAsyncKeyState(0x1B) & 0x8000) != 0 || (GetAsyncKeyState((int)hkVk) & 0x8000) != 0); i++)
                await Task.Delay(20);
            BeginInvoke(new Action(() =>
            {
                if (back != IntPtr.Zero) SetForegroundWindow(back);
                if (insertPath != null) RunAction("insert", () => Core.Insert(insertPath, replaceOverlapping, adaptRhythm));
            }));
        });
    }

    void OnQuickMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (Str(m, "cmd"))
            {
                case "qclose":
                    CloseQuick(null);
                    break;
                case "qinsert":
                    string p = SafePath(Str(m, "path"));
                    if (p != null) CloseQuick(p);
                    break;
                case "qsave":
                    string err = QuickSave(Str(m, "category"), Str(m, "name"));
                    if (err == null) CloseQuick(null);
                    else quick.Post(new { type = "qresult", ok = false, text = err });
                    break;
            }
        }
        catch { }
    }

    // Returns null when saved, or an error text.
    string QuickSave(string category, string name)
    {
        category = Clean(category);
        name = Clean(name);
        if (name.Length == 0) return Lang.T("needName");
        string path = Path.Combine(Core.PatternDir, category, name + ".osu");
        if (File.Exists(path) &&
            MessageBox.Show(quick, Lang.T("overwriteQ", name), Lang.T("overwriteT"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return "";
        try
        {
            string msg = Core.SaveSelection(path);
            lastQuickCategory = category;
            SendStatus("save", true, msg);
            SendState();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Dark window title bar (Windows 10/11)
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }
    }

    async Task InitWeb()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions("--disable-gpu --disable-gpu-compositing --disable-smooth-scrolling --renderer-process-limit=1");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppContext.BaseDirectory, "WebViewData"), options);
            webEnv = env;
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "Microsoft Edge WebView2 Runtime is needed / gerekli:\nhttps://developer.microsoft.com/microsoft-edge/webview2/\n\n" + ex.Message,
                "osu! Pattern Gallery", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var cw = web.CoreWebView2;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "ui"),
            CoreWebView2HostResourceAccessKind.Allow);
        cw.WebMessageReceived += (s, e) =>
        {
            string json;
            try { json = e.WebMessageAsJson; } catch { return; }
            // files dropped on the window come with the message
            var dropped = new List<string>();
            try
            {
                if (e.AdditionalObjects != null)
                    foreach (var o in e.AdditionalObjects)
                        if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path)) dropped.Add(f.Path);
            }
            catch { }
            BeginInvoke(new Action(() => OnMessage(json, dropped)));   // leave the event first, then do the work
        };
        cw.Navigate("https://app.local/index.html");

        // the small hotkey window is prepared in the background so it opens instantly
        try
        {
            quick = new QuickWindow();
            quick.Message += json => BeginInvoke(new Action(() => OnQuickMessage(json)));
            await quick.Init(webEnv);
        }
        catch { quick = null; }
    }

    // ---------------------------------------------------------------- messages from the page
    void OnMessage(string json, List<string> dropped)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            switch (Str(m, "cmd"))
            {
                case "ready":
                    SetupBot();
                    LoadHotkey();
                    try { if (File.Exists(ReplaceFile)) replaceOverlapping = File.ReadAllText(ReplaceFile).Trim() != "0"; } catch { }
                    try { if (File.Exists(AdaptFile)) adaptRhythm = File.ReadAllText(AdaptFile).Trim() != "0"; } catch { }
                    RegisterHotkey();
                    SendState();
                    SendMap(true);
                    mapTimer.Start();
                    break;
                case "hotkeyCapture":   // while the user picks a new key, the old one is switched off
                    KeyHook.Enabled = !(m.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True);
                    break;
                case "hotkey":
                    hkVk = (uint)m.GetProperty("vk").GetInt32();
                    hkMods = (m.GetProperty("alt").GetBoolean() ? 1u : 0) | (m.GetProperty("ctrl").GetBoolean() ? 2u : 0) | (m.GetProperty("shift").GetBoolean() ? 4u : 0);
                    hkLabel = Str(m, "label");
                    try { File.WriteAllText(HotkeyFile, $"{hkMods}|{hkVk}|{hkLabel}"); } catch { }
                    RegisterHotkey();
                    if (hkActive) SendStatus("hotkey", true, Lang.T("hotkeySet", hkLabel));
                    SendState();
                    break;
                case "replace":
                    replaceOverlapping = !(m.TryGetProperty("value", out var rv) && rv.ValueKind == JsonValueKind.False);
                    try { File.WriteAllText(ReplaceFile, replaceOverlapping ? "1" : "0"); } catch { }
                    break;
                case "adapt":
                    adaptRhythm = !(m.TryGetProperty("value", out var av) && av.ValueKind == JsonValueKind.False);
                    try { File.WriteAllText(AdaptFile, adaptRhythm ? "1" : "0"); } catch { }
                    break;
                case "rename":
                    Rename(SafePath(Str(m, "path")), Str(m, "name"), Str(m, "category"));
                    break;
                case "export":
                    var list = new List<string>();
                    if (m.TryGetProperty("paths", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var x in arr.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString());
                    Export(list);
                    break;
                case "botSave":
                    bot.Settings.Username = Str(m, "username").Trim();
                    if (Str(m, "password").Length > 0) bot.Settings.SetPassword(Str(m, "password"));
                    bot.Settings.AutoStart = m.TryGetProperty("autostart", out var asv) && asv.ValueKind == JsonValueKind.True;
                    if (m.TryGetProperty("own", out var ownv)) bot.Settings.OwnAccount = ownv.ValueKind == JsonValueKind.True;
                    bot.Settings.Save();
                    SendBot();
                    break;
                case "botTest":
                    SendStatus("bot", bot.SendTest(Str(m, "to")), bot.State == "on" ? Lang.T("botTestSent", Str(m, "to")) : Lang.T("botNotOn"));
                    break;
                case "botStart":
                    if (BotEnabled) bot.Start();
                    break;
                case "botNewCode":
                    bot.NewLinkCode();
                    break;
                case "botStop":
                    bot.Stop();
                    break;
                case "botUnlink":
                    bot.Unlink(Str(m, "name"));
                    SendBot();
                    break;
                case "openQuick":
                    OpenQuick(Str(m, "mode") == "save" ? "save" : "insert", FindOsuWindow());
                    break;
                case "share":
                    Share(SafePath(Str(m, "path")));
                    break;
                case "packs":
                    LoadPacks();
                    break;
                case "installPack":
                    InstallPack(Str(m, "file"));
                    break;
                case "import":
                    Import(Str(m, "category"), dropped);
                    break;
                case "save":
                    Save(Str(m, "category"), Str(m, "name"));
                    break;
                case "insert":
                    string ip = SafePath(Str(m, "path"));
                    bool replace = !(m.TryGetProperty("replace", out var rp) && rp.ValueKind == JsonValueKind.False);
                    bool adapt = !(m.TryGetProperty("adapt", out var ap) && ap.ValueKind == JsonValueKind.False);
                    if (ip != null) RunAction("insert", () => Core.Insert(ip, replace, adapt));
                    break;
                case "undo":
                    RunAction("undo", Core.Undo);
                    break;
                case "delete":
                    Delete(SafePath(Str(m, "path")));
                    break;
                case "newCategory":
                    string cat = Clean(Str(m, "name"));
                    if (cat.Length > 0) Directory.CreateDirectory(Path.Combine(Core.PatternDir, cat));
                    SendState();
                    break;
                case "topmost":
                    TopMost = m.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
                    SendState();
                    break;
                case "lang":
                    Lang.Set(Str(m, "value"));
                    SendState();
                    SendMap(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            SendStatus("error", false, ex.Message);
        }
    }

    static string Str(JsonElement m, string key) =>
        m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    // Only allow files inside the Patterns folder
    static string SafePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(Core.PatternDir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    void Save(string category, string name)
    {
        category = Clean(category);
        name = Clean(name);
        if (name.Length == 0) { SendStatus("save", false, Lang.T("needName")); return; }

        string path = Path.Combine(Core.PatternDir, category, name + ".osu");
        if (File.Exists(path) &&
            MessageBox.Show(this, Lang.T("overwriteQ", name), Lang.T("overwriteT"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        RunAction("save", () => Core.SaveSelection(path));
        SendState();
    }

    // Copies dropped .osu files, folders with .osu files, or pattern packs (.zip) into the library.
    // Files inside a folder of a .zip keep that folder as their category.
    void Import(string category, List<string> paths)
    {
        category = Clean(category);
        string root = Path.GetFullPath(Core.PatternDir);
        var items = new List<(string file, string cat)>();
        var temps = new List<string>();

        foreach (var p in paths)
        {
            try
            {
                if (Directory.Exists(p))
                    foreach (var f in Directory.GetFiles(p, "*.osu", SearchOption.AllDirectories)) items.Add((f, category));
                else if (File.Exists(p) && p.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                    items.Add((p, category));
                else if (File.Exists(p) && p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "PatternGallery_" + Guid.NewGuid().ToString("N"));
                    ZipFile.ExtractToDirectory(p, tmp);
                    temps.Add(tmp);
                    foreach (var f in Directory.GetFiles(tmp, "*.osu", SearchOption.AllDirectories))
                    {
                        var parts = Path.GetRelativePath(tmp, f).Split(Path.DirectorySeparatorChar);
                        items.Add((f, parts.Length > 1 ? Clean(parts[0]) : category));
                    }
                }
            }
            catch { }
        }

        int added = 0, skipped = 0;
        foreach (var (f, cat) in items)
        {
            try
            {
                if (Path.GetFullPath(f).StartsWith(root, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }   // already in the library
                if (Core.GetSection(File.ReadAllLines(f), "HitObjects").Count == 0) { skipped++; continue; }           // not a pattern
                if (new FileInfo(f).Length > 1_000_000) { skipped++; continue; }                                        // a whole beatmap, not a pattern
                string dir = Path.Combine(Core.PatternDir, cat);
                Directory.CreateDirectory(dir);
                string name = Clean(Path.GetFileNameWithoutExtension(f));
                string dest = Path.Combine(dir, name + ".osu");
                if (File.Exists(dest) && File.ReadAllBytes(dest).AsSpan().SequenceEqual(File.ReadAllBytes(f))) { skipped++; continue; }   // same pattern already there
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{name} ({i}).osu");   // never overwrite
                File.Copy(f, dest);
                added++;
            }
            catch { skipped++; }
        }
        foreach (var t in temps) { try { Directory.Delete(t, true); } catch { } }

        if (added == 0) SendStatus("import", false, Lang.T("importNone"));
        else SendStatus("import", true, Lang.T("imported", added) + (skipped > 0 ? " " + Lang.T("skipped", skipped) : ""));
        SendState();
    }

    // ---------------------------------------------------------------- osu! chat bot (test mode)
    bool botReady;
    void SetupBot()
    {
        if (botReady) { SendBot(); return; }
        botReady = true;
        bot.OnUi = a => { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); };
        bot.Changed = SendBot;
        bot.Log = msg => bot.OnUi(() => SendStatus("bot", true, msg));   // the bot runs on another thread: talk to the window on the UI thread
        bot.InsertPattern = path =>
        {
            string r = Core.Insert(path, replaceOverlapping, adaptRhythm);
            SendStatus("insert", true, r);
            return r;
        };
        bot.Undo = () => { string r = Core.Undo(); SendStatus("undo", true, r); return r; };
        bot.ListPatterns = () =>
        {
            Directory.CreateDirectory(Core.PatternDir);
            string root = Path.GetFullPath(Core.PatternDir);
            return Directory.GetFiles(Core.PatternDir, "*.osu", SearchOption.AllDirectories).Select(f =>
            {
                var parts = Path.GetRelativePath(root, Path.GetFullPath(f)).Split(Path.DirectorySeparatorChar);
                return (path: f, cat: parts.Length > 1 ? parts[0] : "", name: Path.GetFileNameWithoutExtension(f));
            }).ToList();
        };
        FormClosing += (s, e) => bot.Stop();
        SendBot();
        if (BotEnabled && bot.Settings.AutoStart) bot.Start();
    }

    void SendBot() => Post(new
    {
        type = "bot",
        state = bot.State,
        error = bot.LastError,
        username = bot.Settings.Username,
        hasPassword = bot.Settings.GetPassword().Length > 0,
        linked = bot.LinkedNames,
        code = bot.LinkCode,
        autostart = bot.Settings.AutoStart,
        own = !bot.UseServer,
        ownAllowed = File.Exists(Path.Combine(AppContext.BaseDirectory, "bot.own")),
        botName = bot.UseServer ? bot.BotName : bot.Settings.Username
    });

    // ---------------------------------------------------------------- share to the community pack
    // Opens a pre-filled GitHub issue with the pattern. When the owner approves it, it is added to the "Community" pack.
    const string RepoUrl = "https://github.com/thaism442/osu-pattern-gallery";

    void Share(string path)
    {
        if (path == null || !File.Exists(path)) return;
        string root = Path.GetFullPath(Core.PatternDir);
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        string category = parts.Length > 1 ? parts[0] : "";
        string name = Path.GetFileNameWithoutExtension(path);
        string content = File.ReadAllText(path).Replace("\r\n", "\n").Trim();

        string Body(string code) =>
            "Category: " + category + "\n\n" + Lang.T("shareBody") + "\n\n```\n" + code + "\n```\n";
        string url = RepoUrl + "/issues/new?title=" + Uri.EscapeDataString("[Pattern] " + name)
                   + "&body=" + Uri.EscapeDataString(Body(content));

        if (url.Length > 7000)   // too long for a link: copy the pattern and let the user paste it
        {
            try { Clipboard.SetText(content); } catch { }
            url = RepoUrl + "/issues/new?title=" + Uri.EscapeDataString("[Pattern] " + name)
                + "&body=" + Uri.EscapeDataString(Body(Lang.T("sharePaste")));
            SendStatus("share", true, Lang.T("shareCopied"));
        }
        else SendStatus("share", true, Lang.T("shareOpened"));

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { SendStatus("share", false, ex.Message); }
    }

    // ---------------------------------------------------------------- ready-made packs from GitHub
    const string PacksUrl = "https://raw.githubusercontent.com/thaism442/osu-pattern-gallery/main/packs/";
    static readonly System.Net.Http.HttpClient http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    async void LoadPacks()
    {
        try
        {
            if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.ParseAdd("osu-pattern-gallery");
            string json = await http.GetStringAsync(PacksUrl + "index.json?t=" + DateTime.UtcNow.Ticks);
            using var doc = JsonDocument.Parse(json);
            Post(new { type = "packs", ok = true, packs = doc.RootElement.GetProperty("packs").Clone() });
        }
        catch (Exception ex)
        {
            Post(new { type = "packs", ok = false, error = Lang.T("packsFail", ex.Message) });
        }
    }

    async void InstallPack(string file)
    {
        // only simple file names like "jumps.zip"
        if (string.IsNullOrEmpty(file) || file.Contains('/') || file.Contains('\\') || file.Contains("..") || !file.EndsWith(".zip"))
            return;
        string tmp = Path.Combine(Path.GetTempPath(), "PatternGallery_" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            SendStatus("pack", true, Lang.T("packDownloading"));
            var bytes = await http.GetByteArrayAsync(PacksUrl + Uri.EscapeDataString(file) + "?t=" + DateTime.UtcNow.Ticks);
            await File.WriteAllBytesAsync(tmp, bytes);
            Import("", new List<string> { tmp });   // folders inside the pack become categories
        }
        catch (Exception ex)
        {
            SendStatus("pack", false, Lang.T("packsFail", ex.Message));
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // Saves the chosen patterns as normal .osu files: one pattern with "Save as", several into a chosen folder
    void Export(List<string> paths)
    {
        paths = paths.Select(SafePath).Where(p => p != null && File.Exists(p)).Distinct().ToList();
        if (paths.Count == 0) { SendStatus("export", false, Lang.T("needSelect")); return; }
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        if (paths.Count == 1)
        {
            using var dlg = new SaveFileDialog
            {
                InitialDirectory = desktop,
                Filter = "osu! pattern (*.osu)|*.osu",
                FileName = Path.GetFileName(paths[0]),
                OverwritePrompt = true,
                Title = Lang.T("exportTitle")
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.Copy(paths[0], dlg.FileName, true);
            try { Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\""); } catch { }   // show the file
            SendStatus("export", true, Lang.T("exported", 1, dlg.FileName));
            return;
        }

        using var folder = new FolderBrowserDialog
        {
            InitialDirectory = desktop,
            Description = Lang.T("exportFolder", paths.Count),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        string dir = folder.SelectedPath;

        foreach (var p in paths)
        {
            string name = Path.GetFileNameWithoutExtension(p);
            string dest = Path.Combine(dir, name + ".osu");
            for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{name} ({i}).osu");   // never overwrite
            File.Copy(p, dest);
        }
        try { Process.Start("explorer.exe", $"\"{dir}\""); } catch { }   // open the folder
        SendStatus("export", true, Lang.T("exported", paths.Count, dir));
    }

    // Renames a pattern and/or moves it to another category
    void Rename(string path, string name, string category)
    {
        if (path == null || !File.Exists(path)) return;
        name = Clean(name);
        if (name.Length == 0) { SendStatus("rename", false, Lang.T("needName")); return; }
        string dir = Path.Combine(Core.PatternDir, Clean(category));
        string dest = Path.Combine(dir, name + ".osu");
        if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(dest) == Path.GetFileName(path))
            return;   // nothing changed
        if (File.Exists(dest) && !string.Equals(Path.GetFullPath(dest), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        { SendStatus("rename", false, Lang.T("nameTaken", name)); return; }
        Directory.CreateDirectory(dir);
        File.Move(path, dest, true);   // "true" also allows changing only upper/lower case
        SendStatus("rename", true, Lang.T("renamed", name));
        SendState();
    }

    void Delete(string path)
    {
        if (path == null || !File.Exists(path)) return;
        string name = Path.GetFileNameWithoutExtension(path);
        if (MessageBox.Show(this, Lang.T("deleteQ", name), Lang.T("deleteT"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        File.Delete(path);
        SendStatus("delete", true, Lang.T("deleted", name));
        SendState();
    }

    void RunAction(string action, Func<string> work)
    {
        UseWaitCursor = true;
        try { SendStatus(action, true, work()); }
        catch (Exception ex) { SendStatus(action, false, ex.Message); }
        finally { UseWaitCursor = false; }
    }

    // ---------------------------------------------------------------- messages to the page
    void Post(object payload)
    {
        if (InvokeRequired) { try { BeginInvoke(new Action(() => Post(payload))); } catch { } return; }   // always on the UI thread
        if (web.CoreWebView2 == null) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    void SendStatus(string action, bool ok, string text) =>
        Post(new { type = "status", action, ok, text });

    void SendState()
    {
        Directory.CreateDirectory(Core.PatternDir);

        var patterns = new List<object>();
        foreach (var f in Directory.GetFiles(Core.PatternDir, "*.osu", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Core.PatternDir, f);
            int slash = rel.IndexOf(Path.DirectorySeparatorChar);
            string cat = slash >= 0 ? rel.Substring(0, slash) : "";
            string name = Path.ChangeExtension(slash >= 0 ? rel.Substring(slash + 1) : rel, null).Replace('\\', '/');
            var data = GetData(f);
            patterns.Add(new { path = f, cat, name, count = data.count, bpm = data.bpm, objs = data.objs, time = File.GetLastWriteTimeUtc(f).Ticks });
        }

        var categories = Directory.GetDirectories(Core.PatternDir)
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        Post(new { type = "state", lang = Lang.Current, topmost = TopMost, botEnabled = BotEnabled, hotkey = hkLabel, replace = replaceOverlapping, adapt = adaptRhythm, categories, patterns });
    }

    Preview.PatternData GetData(string path)
    {
        DateTime time = File.GetLastWriteTimeUtc(path);
        if (cache.TryGetValue(path, out var c) && c.time == time) return c.data;
        Preview.PatternData data;
        try { data = Preview.Build(path); }
        catch { data = new Preview.PatternData(); }
        cache[path] = (time, data);
        return data;
    }

    // Finds the osu! window (the process is looked up only when needed)
    IntPtr FindOsuWindow()
    {
        try
        {
            if (osuProc == null || osuProc.HasExited)
            {
                osuProc?.Dispose();
                osuProc = Process.GetProcessesByName("osu!").FirstOrDefault();
                osuWindow = IntPtr.Zero;
            }
            if (osuProc == null) return IntPtr.Zero;
            if (osuWindow == IntPtr.Zero || !IsWindow(osuWindow))
            {
                osuProc.Refresh();
                osuWindow = osuProc.MainWindowHandle;
            }
            return osuWindow;
        }
        catch { osuProc = null; return IntPtr.Zero; }
    }

    // Which beatmap is open in the editor (read from the osu! window title)
    void SendMap(bool force)
    {
        KeyHook.OsuWindow = FindOsuWindow();
        string title = null;
        try
        {
            // InternalGetWindowText does not send a message to osu!, so the game is never interrupted.
            IntPtr w = FindOsuWindow();
            if (w != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(512);
                InternalGetWindowText(w, sb, sb.Capacity);
                string t = sb.ToString();
                if (t.EndsWith(".osu")) title = t;
            }
        }
        catch { }

        if (!force && title == lastMap) return;
        lastMap = title;

        if (title == null) { Post(new { type = "map", open = false, title = "", diff = "" }); return; }

        int dash = title.IndexOf(" - ");
        string map = dash >= 0 ? title.Substring(dash + 3) : title;
        if (map.EndsWith(".osu")) map = map.Substring(0, map.Length - 4);
        string diff = "";
        int lb = map.LastIndexOf('[');
        if (lb >= 0 && map.EndsWith("]"))
        {
            diff = map.Substring(lb + 1, map.Length - lb - 2);
            map = map.Substring(0, lb).TrimEnd();
        }
        Post(new { type = "map", open = true, title = map, diff });
    }

    static string Clean(string s)
    {
        s = (s ?? "").Trim();
        foreach (char c in Path.GetInvalidFileNameChars())
            s = s.Replace(c.ToString(), "");
        return s;
    }
}

// The small "Save pattern / Insert pattern" window (ui\quick.html). It is created once and only shown / hidden.
class QuickWindow : Form
{
    readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(28, 26, 34) };
    public bool Ready;
    public IntPtr ReturnTo;
    public event Action<string> Message;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    public QuickWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(28, 26, 34);
        Size = new Size(560, 600);
        Controls.Add(web);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    public async Task Init(CoreWebView2Environment env)
    {
        CreateControl();
        var h = Handle;   // make sure the window exists before WebView2 starts
        await web.EnsureCoreWebView2Async(env);
        var cw = web.CoreWebView2;
        cw.Settings.AreDefaultContextMenusEnabled = false;
        cw.Settings.IsStatusBarEnabled = false;
        cw.Settings.IsZoomControlEnabled = false;
        cw.SetVirtualHostNameToFolderMapping("app.local", Path.Combine(AppContext.BaseDirectory, "ui"),
            CoreWebView2HostResourceAccessKind.Allow);
        cw.WebMessageReceived += (s, e) =>
        {
            string json;
            try { json = e.WebMessageAsJson; } catch { return; }
            if (json.Contains("\"qready\"")) { Ready = true; return; }
            Message?.Invoke(json);
        };
        cw.Navigate("https://app.local/quick.html");
    }

    public void Post(object payload)
    {
        if (web.CoreWebView2 == null) return;
        web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    // shows the window in the middle of the screen osu! is on, in front, with the keyboard focus
    public void ShowOver(IntPtr owner)
    {
        var screen = owner != IntPtr.Zero ? Screen.FromHandle(owner) : Screen.PrimaryScreen;
        var area = screen.WorkingArea;
        int w = Math.Min(560, area.Width - 40), hgt = Math.Min(600, area.Height - 40);
        Bounds = new Rectangle(area.Left + (area.Width - w) / 2, area.Top + (area.Height - hgt) / 2, w, hgt);
        Show();
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(Handle);
        if (attached) AttachThreadInput(me, fgThread, false);
        Activate();
        web.Focus();
    }
}

// Global keyboard hook running on its own thread, so it never slows down typing in other programs.
static class KeyHook
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int InternalGetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    static volatile uint vk = 0x7A;
    static volatile bool ctrl, alt, shift;
    public static volatile bool Enabled = true;
    public static IntPtr OsuWindow;
    public static IntPtr QuickWindow;
    public static Action Pressed;

    static HookProc proc;          // kept here so it is not garbage collected
    static IntPtr hook;
    static System.Threading.Thread thread;

    public static void Set(uint key, bool c, bool a, bool s) { vk = key; ctrl = c; alt = a; shift = s; }

    public static bool Start()
    {
        if (thread != null) return hook != IntPtr.Zero;
        var ready = new System.Threading.ManualResetEventSlim();
        thread = new System.Threading.Thread(() =>
        {
            proc = Callback;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }   // keeps the hook alive
        }) { IsBackground = true, Name = "KeyHook" };
        thread.Start();
        ready.Wait(2000);
        return hook != IntPtr.Zero;
    }

    static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && Enabled && ((int)wParam == WM_KEYDOWN || (int)wParam == WM_SYSKEYDOWN)
                && (uint)Marshal.ReadInt32(lParam) == vk
                && Down(0x11) == ctrl && Down(0x12) == alt && Down(0x10) == shift
                && EditorIsActive())
            {
                Pressed?.Invoke();
                return (IntPtr)1;   // the key does not go to osu!
            }
        }
        catch { }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    static bool EditorIsActive()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg == QuickWindow) return true;
        if (fg == IntPtr.Zero || fg != OsuWindow) return false;
        var sb = new System.Text.StringBuilder(512);
        InternalGetWindowText(fg, sb, sb.Capacity);
        return sb.ToString().EndsWith(".osu");
    }
}
