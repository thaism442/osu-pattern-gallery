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

        // "Always on top" is on unless the user turned it off
        TopMost = true;
        try { if (File.Exists(TopmostFile)) TopMost = File.ReadAllText(TopmostFile).Trim() != "0"; } catch { }

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
    QuickSaveForm quickSave;
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

    string pendingInsert;

    // Hotkey pressed in the editor: objects selected -> "Save" tab, nothing selected -> "Insert" tab
    void OnHotkey()
    {
        if (quickSave != null) { quickSave.Activate(); return; }

        IntPtr osuWnd = FindOsuWindow();
        var sb = new System.Text.StringBuilder(512);
        if (osuWnd != IntPtr.Zero) InternalGetWindowText(osuWnd, sb, sb.Capacity);
        if (!sb.ToString().EndsWith(".osu"))
        {
            SendStatus("hotkey", false, Lang.T("editorNotOpen"));
            return;
        }

        Directory.CreateDirectory(Core.PatternDir);
        var cats = Directory.GetDirectories(Core.PatternDir).Select(Path.GetFileName)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var items = Directory.GetFiles(Core.PatternDir, "*.osu", SearchOption.AllDirectories)
            .Select(f =>
            {
                string rel = Path.ChangeExtension(Path.GetRelativePath(Core.PatternDir, f), null).Replace('\\', '/');
                var d = GetData(f);
                string info = d.count + " " + Lang.T("notesShort") + (d.bpm > 0 ? " · " + d.bpm + " BPM" : "");
                return new QuickItem { Path = f, Name = rel, Info = info };
            })
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        bool startOnSave = Core.SelectedCount() > 0;
        pendingInsert = null;
        quickSave = new QuickSaveForm(cats, lastQuickCategory, QuickSave, items, path => { pendingInsert = path; return null; }, startOnSave);
        try
        {
            quickSave.ShowDialog();
        }
        finally
        {
            quickSave.Dispose();
            quickSave = null;
            if (osuWnd != IntPtr.Zero) SetForegroundWindow(osuWnd);   // back to the editor
        }

        if (pendingInsert != null)
        {
            string toInsert = pendingInsert;
            pendingInsert = null;
            RunAction("insert", () => Core.Insert(toInsert, replaceOverlapping, adaptRhythm));
        }
    }

    // Called by the small window. Returns null when saved, or an error text.
    string QuickSave(string category, string name)
    {
        category = Clean(category);
        name = Clean(name);
        if (name.Length == 0) return Lang.T("needName");
        string path = Path.Combine(Core.PatternDir, category, name + ".osu");
        if (File.Exists(path) &&
            MessageBox.Show(quickSave, Lang.T("overwriteQ", name), Lang.T("overwriteT"),
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
                    try { File.WriteAllText(TopmostFile, TopMost ? "1" : "0"); } catch { }
                    SendState();
                    break;
                case "lang":
                    Lang.Set(Str(m, "value") == "en" ? "en" : "tr");
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

        Post(new { type = "state", lang = Lang.Current, topmost = TopMost, hotkey = hkLabel, replace = replaceOverlapping, adapt = adaptRhythm, categories, patterns });
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

class QuickItem
{
    public string Path, Name, Info;
    public override string ToString() => Name;
}

// The small window that opens with the hotkey:
// "Save" tab (save the selected objects) and "Insert" tab (search a pattern and insert it)
class QuickSaveForm : Form
{
    static readonly Color Bg = Color.FromArgb(28, 26, 34), Field = Color.FromArgb(40, 37, 47),
        Accent = Color.FromArgb(255, 102, 170), Muted = Color.FromArgb(140, 133, 151), Err = Color.FromArgb(255, 107, 122);

    readonly Func<string, string, string> save;
    readonly Func<string, string> insert;
    readonly List<QuickItem> all;

    readonly Button saveTab, insertTab;
    readonly Panel savePanel = new Panel(), insertPanel = new Panel();
    readonly ComboBox cat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    readonly TextBox name = new TextBox { BorderStyle = BorderStyle.FixedSingle };
    readonly Label error = new Label { AutoSize = false };
    readonly TextBox search = new TextBox { BorderStyle = BorderStyle.FixedSingle };
    readonly ListBox list = new ListBox { BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 26, IntegralHeight = false };
    readonly Label hint = new Label { AutoSize = false };
    bool onSave;

    public QuickSaveForm(List<string> categories, string lastCategory, Func<string, string, string> save,
                         List<QuickItem> patterns, Func<string, string> insert, bool startOnSave)
    {
        this.save = save;
        this.insert = insert;
        all = patterns;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Bg;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(380, 300);
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // tabs
        saveTab = MakeButton(Lang.T("tabSave"), Field, new Rectangle(16, 14, 110, 32));
        insertTab = MakeButton(Lang.T("tabInsert"), Field, new Rectangle(132, 14, 110, 32));
        saveTab.Click += (s, e) => ShowTab(true);
        insertTab.Click += (s, e) => ShowTab(false);
        var close = new Label { Text = "Esc", ForeColor = Muted, AutoSize = true, Location = new Point(334, 22), Font = new Font("Segoe UI", 8.5f) };

        // save tab
        savePanel.SetBounds(0, 56, 380, 244);
        var catLabel = new Label { Text = Lang.T("hkCategory"), ForeColor = Muted, Location = new Point(16, 4), AutoSize = true };
        cat.SetBounds(16, 26, 348, 28);
        cat.BackColor = Field; cat.ForeColor = Color.White;
        cat.Items.AddRange(categories.ToArray());
        cat.Text = lastCategory ?? "";
        var nameLabel = new Label { Text = Lang.T("hkName"), ForeColor = Muted, Location = new Point(16, 64), AutoSize = true };
        name.SetBounds(16, 86, 348, 28);
        name.BackColor = Field; name.ForeColor = Color.White;
        error.SetBounds(16, 120, 348, 40);
        error.ForeColor = Err;
        error.Font = new Font("Segoe UI", 9f);
        var cancel = MakeButton(Lang.T("cancel"), Field, new Rectangle(184, 196, 85, 32));
        var ok = MakeButton(Lang.T("ok"), Accent, new Rectangle(279, 196, 85, 32));
        cancel.Click += (s, e) => Close();
        ok.Click += (s, e) => TrySave();
        savePanel.Controls.AddRange(new Control[] { catLabel, cat, nameLabel, name, error, cancel, ok });

        // insert tab
        insertPanel.SetBounds(0, 56, 380, 244);
        search.SetBounds(16, 4, 348, 28);
        search.BackColor = Field; search.ForeColor = Color.White;
        search.PlaceholderText = Lang.T("searchPh");
        list.SetBounds(16, 38, 348, 166);
        list.BackColor = Field; list.ForeColor = Color.White;
        list.DrawItem += DrawItem;
        list.DoubleClick += (s, e) => TryInsert();
        hint.SetBounds(16, 210, 348, 26);
        hint.ForeColor = Muted;
        hint.Font = new Font("Segoe UI", 8.5f);
        search.TextChanged += (s, e) => Filter();
        insertPanel.Controls.AddRange(new Control[] { search, list, hint });

        Controls.AddRange(new Control[] { saveTab, insertTab, close, savePanel, insertPanel });

        KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            else if (e.KeyCode == Keys.Enter && !cat.DroppedDown) { e.SuppressKeyPress = true; if (onSave) TrySave(); else TryInsert(); }
            else if (e.Control && e.KeyCode == Keys.Tab) { e.SuppressKeyPress = true; ShowTab(!onSave); }
            else if (!onSave && (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) && list.Items.Count > 0)
            {
                e.SuppressKeyPress = true;
                int i = list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
                list.SelectedIndex = Math.Max(0, Math.Min(list.Items.Count - 1, i));
            }
        };

        Filter();
        ShowTab(startOnSave);
        Shown += (s, e) =>
        {
            ForceForeground(Handle);
            Activate();
            (onSave ? (Control)name : search).Focus();
        };
    }

    void ShowTab(bool toSave)
    {
        onSave = toSave;
        savePanel.Visible = toSave;
        insertPanel.Visible = !toSave;
        saveTab.BackColor = toSave ? Accent : Field;
        insertTab.BackColor = toSave ? Field : Accent;
        (toSave ? (Control)name : search).Focus();
    }

    void Filter()
    {
        string q = search.Text.Trim();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var it in all.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            list.Items.Add(it);
        list.EndUpdate();
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        hint.Text = all.Count == 0 ? Lang.T("noPatterns") : Lang.T("insertHint");
    }

    void DrawItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var it = (QuickItem)list.Items[e.Index];
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? Accent : Field)) e.Graphics.FillRectangle(b, e.Bounds);
        TextRenderer.DrawText(e.Graphics, it.Name, Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 124, e.Bounds.Height),
            Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using var small = new Font("Segoe UI", 8.5f);
        TextRenderer.DrawText(e.Graphics, it.Info, small, new Rectangle(e.Bounds.Right - 116, e.Bounds.Y, 108, e.Bounds.Height),
            sel ? Color.White : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    void TryInsert()
    {
        if (list.SelectedItem is QuickItem it)
        {
            insert(it.Path);
            Close();
        }
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    static void ForceForeground(IntPtr h)
    {
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(h);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    static Button MakeButton(string text, Color back, Rectangle r)
    {
        var b = new Button { Text = text, BackColor = back, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Bounds = r, Cursor = Cursors.Hand, TabStop = false };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    void TrySave()
    {
        error.Text = "";
        UseWaitCursor = true;
        string result = save(cat.Text, name.Text);
        UseWaitCursor = false;
        if (result == null) Close();
        else error.Text = result;
    }

    // thin pink frame
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Accent, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
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
        if (fg == IntPtr.Zero || fg != OsuWindow) return false;
        var sb = new System.Text.StringBuilder(512);
        InternalGetWindowText(fg, sb, sb.Capacity);
        return sb.ToString().EndsWith(".osu");
    }
}
