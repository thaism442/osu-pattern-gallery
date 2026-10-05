// All texts of the program live in ui\lang\<code>.json (the window and the program use the same files).
// Languages: see ui\lang\languages.json. Missing texts fall back to English.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

static class Lang
{
    static readonly string SettingsFile = Path.Combine(AppContext.BaseDirectory, "language.txt");
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "ui", "lang");
    static readonly Dictionary<string, Dictionary<string, string>> loaded =
        new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

    public static string Current = "en";

    public static readonly string[] Codes = { "tr", "en", "cs", "de", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans", "zh-Hant" };

    // "DE", "pt", "zh-tw" ... -> a supported code (or null)
    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim().Replace('_', '-');
        var exact = Codes.FirstOrDefault(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        string lower = code.ToLowerInvariant();
        if (lower.StartsWith("zh")) return lower.Contains("tw") || lower.Contains("hk") || lower.Contains("hant") ? "zh-Hant" : "zh-Hans";
        if (lower.StartsWith("pt")) return "pt-BR";
        string two = lower.Split('-')[0];
        return Codes.FirstOrDefault(c => c.ToLowerInvariant() == two);
    }

    static Dictionary<string, string> Get(string code)
    {
        lock (loaded)
        {
            if (loaded.TryGetValue(code, out var d)) return d;
            try { d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Folder, code + ".json"))); }
            catch { d = null; }
            d ??= new Dictionary<string, string>();
            loaded[code] = d;
            return d;
        }
    }

    [ThreadStatic] static string overrideCode;

    public static string T(string key, params object[] args) => TFor(overrideCode ?? Current, key, args);

    // runs work with texts in another language (used for bot answers)
    public static string With(string code, Func<string> work)
    {
        var old = overrideCode;
        overrideCode = Normalize(code) ?? Current;
        try { return work(); }
        finally { overrideCode = old; }
    }

    // text in a given language (the bot answers every user in their own language)
    public static string TFor(string code, string key, params object[] args)
    {
        code = Normalize(code) ?? Current;
        if (!Get(code).TryGetValue(key, out var s) && !Get("en").TryGetValue(key, out s)) s = key;
        try { return args.Length > 0 ? string.Format(s, args) : s; } catch { return s; }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsFile)) { Current = Normalize(File.ReadAllText(SettingsFile)) ?? "en"; return; }
        }
        catch { }
        // first start: use the Windows language if we have it
        Current = Normalize(CultureInfo.CurrentUICulture.Name) ?? "en";
    }

    public static void Set(string code)
    {
        Current = Normalize(code) ?? "en";
        try { File.WriteAllText(SettingsFile, Current); } catch { }
    }
}
