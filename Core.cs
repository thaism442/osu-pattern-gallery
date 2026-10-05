// Core logic: talks to the osu! stable editor and edits .osu files.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Editor_Reader;

static class Core
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static readonly string PatternDir = Path.Combine(AppContext.BaseDirectory, "Patterns");
    static readonly string BackupDir = Path.Combine(AppContext.BaseDirectory, "Backups");
    static readonly string HistoryFile = Path.Combine(AppContext.BaseDirectory, "Backups", "history.json");
    const int MaxHistory = 30;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    // ---------------------------------------------------------------- INSERT
    public static string Insert(string patternPath, bool replaceOverlapping, bool adaptRhythm)
    {
        if (!File.Exists(patternPath))
            throw new Exception(Lang.T("patternMissing", patternPath));

        string[] patternLines = File.ReadAllLines(patternPath);
        List<string> patternObjects = GetSection(patternLines, "HitObjects");
        if (patternObjects.Count == 0)
            throw new Exception(Lang.T("patternEmpty"));

        Process osu = GetOsuProcess();
        EditorReader reader = ReadEditor(osu);
        int editorTime = reader.EditorTime();
        string mapPath = GetMapPath(osu, reader);

        SaveInEditor(osu, mapPath);

        // Backup
        Directory.CreateDirectory(BackupDir);
        string backupPath = Path.Combine(BackupDir,
            DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) + "_" + Path.GetFileName(mapPath));
        File.Copy(mapPath, backupPath, true);
        CleanBackups();

        // Move the pattern to the editor time
        double first = patternObjects.Min(GetTime);
        string rhythmNote = "";
        List<string> shifted = null;
        if (adaptRhythm) shifted = AdaptRhythm(patternLines, patternObjects, reader, editorTime, out rhythmNote);
        if (shifted == null) shifted = patternObjects.Select(l => ShiftObject(l, editorTime - first)).ToList();

        // Add to the beatmap and sort by time
        var lines = File.ReadAllLines(mapPath).ToList();
        int start = lines.FindIndex(l => l.Trim() == "[HitObjects]");
        if (start < 0) { lines.Add(""); lines.Add("[HitObjects]"); start = lines.Count - 1; }
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;

        var objects = lines.Skip(start + 1).Take(end - start - 1)
                           .Where(l => l.Trim().Length > 0).ToList();

        // Remove the old objects that are in the pattern's time range (from its first object to the end of its last one)
        var removedLines = new List<string>();
        if (replaceOverlapping)
        {
            double from = shifted.Min(GetTime) - 2;
            double to = shifted.Max(l => EndTime(l, reader)) + 2;
            removedLines = objects.Where(l => { double t = GetTime(l); return t >= from && t <= to; }).ToList();
            objects.RemoveAll(l => removedLines.Contains(l));
        }
        int removed = removedLines.Count;
        objects.AddRange(shifted);
        objects = objects.OrderBy(GetTime).ToList();   // OrderBy keeps the order for equal times

        var result = new List<string>();
        result.AddRange(lines.Take(start + 1));
        result.AddRange(objects);
        if (end < lines.Count) { result.Add(""); result.AddRange(lines.Skip(end)); }
        WriteFile(mapPath, result);

        // Remember exactly what was added and removed, so "Undo" can take back only this insert
        var history = LoadHistory();
        history.Add(new InsertRecord
        {
            Map = mapPath,
            Backup = backupPath,
            Inserted = shifted.Select(Key).ToList(),
            Removed = removedLines,
            Name = Path.GetFileNameWithoutExtension(patternPath)
        });
        SaveHistory(history);

        ReloadEditor(osu);
        string msg = Lang.T("inserted", shifted.Count, FormatTime(editorTime));
        string notes = (removed > 0 ? " " + Lang.T("replaced", removed) : "") + rhythmNote;
        // for osu! chat the time goes last: osu! turns the time into a link up to the next ")", so nothing may follow it
        LastChatText = Lang.T("botInsertedChat", shifted.Count, notes) + " " + FormatTime(editorTime);
        return msg + notes;
    }

    public static string LastChatText;   // the last insert message, written for osu! chat

    // ---------------------------------------------------------------- RHYTHM ADAPTATION
    // The pattern keeps its rhythm in beats: a 1/4 stream stays 1/4 and a 1/2 slider stays 1/2 long,
    // whatever the BPM and slider velocity of the beatmap are. Slider shapes are kept; only their length is adjusted.
    record TimingPoint(double Time, double BeatLength, bool Red);

    static List<TimingPoint> ParseTiming(IEnumerable<string> lines)
    {
        var list = new List<TimingPoint>();
        foreach (var l in lines)
        {
            var p = l.Split(',');
            if (p.Length < 2) continue;
            if (!double.TryParse(p[0], NumberStyles.Float, Inv, out double t)) continue;
            if (!double.TryParse(p[1], NumberStyles.Float, Inv, out double bl)) continue;
            bool red = p.Length < 7 || p[6].Trim() == "1";
            list.Add(new TimingPoint(t, bl, red));
        }
        return list.OrderBy(x => x.Time).ToList();
    }

    static List<TimingPoint> ReaderTiming(EditorReader reader)
    {
        var list = new List<TimingPoint>();
        if (reader.controlPoints != null)
            foreach (var cp in reader.controlPoints)
                list.Add(new TimingPoint(Convert.ToDouble(cp.Offset), Convert.ToDouble(cp.BeatLength), Convert.ToBoolean(cp.TimingChange)));
        return list.OrderBy(x => x.Time).ToList();
    }

    // Beat length (ms per beat) of the red line active at time t
    static double BeatAt(List<TimingPoint> timing, double t)
    {
        var red = timing.Where(x => x.Red && x.BeatLength > 0).ToList();
        if (red.Count == 0) return 0;
        var at = red.LastOrDefault(x => x.Time <= t + 1) ?? red[0];
        return at.BeatLength;
    }

    // Slider velocity multiplier active at time t
    static double SvAt(List<TimingPoint> timing, double t)
    {
        double sv = 1;
        foreach (var x in timing)
        {
            if (x.Time > t + 1) break;
            if (x.Red) sv = 1;
            else if (x.BeatLength < 0) sv = Math.Clamp(-100 / x.BeatLength, 0.1, 10);
        }
        return sv;
    }

    static double SnapBeats(double beats) => Math.Round(beats * 48) / 48;   // 1/48 covers 1/2, 1/3, 1/4, 1/6, 1/8, 1/12, 1/16

    static List<string> AdaptRhythm(string[] patternLines, List<string> objects, EditorReader reader, int editorTime, out string note)
    {
        note = "";
        var pTiming = ParseTiming(GetSection(patternLines, "TimingPoints"));
        var tTiming = ReaderTiming(reader);
        double t0 = objects.Min(GetTime);
        double pBeat = BeatAt(pTiming, t0), tBeat = BeatAt(tTiming, editorTime);
        if (tBeat <= 0) return null;                 // the beatmap has no timing: insert as it is
        if (pBeat <= 0) return SnapWithoutTiming(objects, t0, editorTime, tBeat, out note);   // the pattern has no BPM

        double pSM = 1.4;
        foreach (var l in GetSection(patternLines, "Difficulty"))
            if (l.StartsWith("SliderMultiplier") && double.TryParse(l.Substring(l.IndexOf(':') + 1).Trim(), NumberStyles.Float, Inv, out double v) && v > 0) pSM = v;
        double tSM = Convert.ToDouble(reader.SliderMultiplier);
        if (tSM <= 0) tSM = 1.4;

        double NewTime(double t) => editorTime + SnapBeats((t - t0) / pBeat) * tBeat;

        var result = new List<string>();
        foreach (var line in objects)
        {
            var p = line.Split(',');
            double t = double.Parse(p[2], NumberStyles.Float, Inv);
            double nt = NewTime(t);
            p[2] = Fmt(nt);
            int type = int.Parse(p[3], Inv);

            if ((type & 8) != 0 && p.Length > 5)                 // spinner end
                p[5] = Fmt(NewTime(double.Parse(p[5], NumberStyles.Float, Inv)));
            else if ((type & 128) != 0 && p.Length > 5)          // mania hold end
            {
                var h = p[5].Split(':');
                h[0] = Fmt(NewTime(double.Parse(h[0], NumberStyles.Float, Inv)));
                p[5] = string.Join(":", h);
            }
            else if ((type & 2) != 0 && p.Length > 7)            // slider: same length in beats
            {
                double len = double.Parse(p[7], NumberStyles.Float, Inv);
                double beats = len / (pSM * 100 * SvAt(pTiming, t));
                beats = Math.Max(1.0 / 48, SnapBeats(beats));
                double newLen = beats * tSM * 100 * SvAt(tTiming, nt);
                p[7] = Math.Round(newLen, 4).ToString(Inv);
            }
            result.Add(string.Join(",", p));
        }

        int pBpm = (int)Math.Round(60000 / pBeat), tBpm = (int)Math.Round(60000 / tBeat);
        note = " " + (pBpm != tBpm ? Lang.T("adapted", pBpm, tBpm) : Lang.T("adaptedSame"));
        return result;
    }

    // For patterns saved without BPM: keep the spacing in ms, but put every object on the nearest
    // 1/4 or 1/3 tick of the beatmap so the notes line up with the timeline
    static List<string> SnapWithoutTiming(List<string> objects, double t0, int editorTime, double tBeat, out string note)
    {
        double Snap(double t)
        {
            double rel = t - t0;
            double q = Math.Round(rel / (tBeat / 4)) * (tBeat / 4);
            double r = Math.Round(rel / (tBeat / 3)) * (tBeat / 3);
            return editorTime + (Math.Abs(q - rel) <= Math.Abs(r - rel) ? q : r);
        }

        var result = new List<string>();
        foreach (var line in objects)
        {
            var p = line.Split(',');
            p[2] = Fmt(Snap(double.Parse(p[2], NumberStyles.Float, Inv)));
            int type = int.Parse(p[3], Inv);
            if ((type & 8) != 0 && p.Length > 5)
                p[5] = Fmt(Snap(double.Parse(p[5], NumberStyles.Float, Inv)));
            else if ((type & 128) != 0 && p.Length > 5)
            {
                var h = p[5].Split(':');
                h[0] = Fmt(Snap(double.Parse(h[0], NumberStyles.Float, Inv)));
                p[5] = string.Join(":", h);
            }
            result.Add(string.Join(",", p));
        }
        note = " " + Lang.T("adaptedNoBpm");
        return result;
    }

    // End time of an object line, using the beatmap's timing for sliders
    static double EndTime(string line, EditorReader reader)
    {
        var p = line.Split(',');
        double t = GetTime(line);
        try
        {
            int type = int.Parse(p[3], Inv);
            if ((type & 8) != 0 || (type & 128) != 0)                     // spinner / hold note
                return double.Parse(p[5].Split(':')[0], NumberStyles.Float, Inv);
            if ((type & 2) == 0 || p.Length < 8) return t;                 // circle

            int slides = int.Parse(p[6], Inv);
            double length = double.Parse(p[7], NumberStyles.Float, Inv);
            double beat = 500, sv = 1;
            bool haveRed = false;
            if (reader.controlPoints != null)
                foreach (var cp in reader.controlPoints.OrderBy(c => Convert.ToDouble(c.Offset)))
                {
                    if (Convert.ToDouble(cp.Offset) > t + 1) break;
                    double bl = Convert.ToDouble(cp.BeatLength);
                    if (Convert.ToBoolean(cp.TimingChange)) { if (bl > 0) beat = bl; sv = 1; haveRed = true; }
                    else if (bl < 0) sv = Math.Clamp(-100 / bl, 0.1, 10);
                }
            if (!haveRed) return t;
            double sm = Convert.ToDouble(reader.SliderMultiplier);
            if (sm <= 0) sm = 1.4;
            return t + length / (sm * 100 * sv) * beat * slides;
        }
        catch { return t; }
    }

    // ---------------------------------------------------------------- SAVE SELECTION
    // Reads the selected objects straight from osu!'s memory, so the beatmap does not need to be saved.
    public static string SaveSelection(string outPath)
    {
        Process osu = GetOsuProcess();
        EditorReader reader = ReadEditor(osu);

        var selected = reader.hitObjects
            .Where(o => o.IsSelected && IsSane(o))
            .OrderBy(o => Convert.ToDouble(o.StartTime))
            .ToList();
        if (selected.Count == 0)
            throw new Exception(Lang.T("noSelection"));

        var objects = selected.Select(ToOsuLine).ToList();
        double first = Convert.ToDouble(selected[0].StartTime);
        double last = selected.Max(o => Math.Max(Convert.ToDouble(o.StartTime), Convert.ToDouble(o.EndTime)));

        // Keep the timing (BPM / slider velocity) around the pattern, for rhythm adaptation later
        var timing = new List<(double time, bool red, string line)>();
        if (reader.controlPoints != null)
            foreach (var cp in reader.controlPoints)
            {
                double time = Convert.ToDouble(cp.Offset);
                bool red = Convert.ToBoolean(cp.TimingChange);
                string line = string.Join(",",
                    Num(time),
                    Num(Convert.ToDouble(cp.BeatLength)),
                    Convert.ToInt32(cp.TimeSignature).ToString(Inv),
                    Convert.ToInt32(cp.SampleSet).ToString(Inv),
                    Convert.ToInt32(cp.CustomSamples).ToString(Inv),
                    Convert.ToInt32(cp.Volume).ToString(Inv),
                    red ? "1" : "0",
                    Convert.ToInt32(cp.EffectFlags).ToString(Inv));
                timing.Add((time, red, line));
            }
        timing = timing.OrderBy(x => x.time).ToList();

        var keptTiming = new List<string>();
        var lastRed = timing.Where(x => x.red && x.time <= first).Select(x => x.line).LastOrDefault()
                      ?? timing.Where(x => x.red).Select(x => x.line).FirstOrDefault();
        var lastAny = timing.Where(x => x.time <= first).Select(x => x.line).LastOrDefault();
        if (lastRed != null) keptTiming.Add(lastRed);
        if (lastAny != null && lastAny != lastRed) keptTiming.Add(lastAny);
        keptTiming.AddRange(timing.Where(x => x.time > first && x.time <= last).Select(x => x.line));

        var content = new List<string>
        {
            "osu file format v14", "",
            "// Pattern saved from: " + reader.Filename, "",
            "[Difficulty]",
            "SliderMultiplier:" + Num(Convert.ToDouble(reader.SliderMultiplier)),
            "SliderTickRate:" + Num(Convert.ToDouble(reader.SliderTickRate)),
            "", "[TimingPoints]"
        };
        content.AddRange(keptTiming);
        content.Add(""); content.Add("[HitObjects]");
        content.AddRange(objects);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        WriteFile(outPath, content);
        return Lang.T("saved", objects.Count, Path.GetFileNameWithoutExtension(outPath));
    }

    // Same check as Mapping Tools: skip objects that were read wrongly
    static bool IsSane(Editor_Reader.HitObject o) =>
        Convert.ToInt32(o.SegmentCount) <= 9000 && Convert.ToInt32(o.Type) != 0 &&
        Convert.ToInt32(o.SampleSet) <= 1000 && Convert.ToInt32(o.SampleSetAdditions) <= 1000 &&
        Convert.ToInt32(o.SampleVolume) <= 1000;

    // Turns an object from osu!'s memory into a line of the .osu file
    static string ToOsuLine(Editor_Reader.HitObject o)
    {
        int type = Convert.ToInt32(o.Type) & 255;
        string x = ((int)Math.Round(Convert.ToDouble(o.X))).ToString(Inv);
        string y = ((int)Math.Round(Convert.ToDouble(o.Y))).ToString(Inv);
        string time = ((int)Math.Round(Convert.ToDouble(o.StartTime))).ToString(Inv);
        string end = ((int)Math.Round(Convert.ToDouble(o.EndTime))).ToString(Inv);
        string sound = Convert.ToInt32(o.SoundType).ToString(Inv);
        string sample = string.Join(":",
            Convert.ToInt32(o.SampleSet).ToString(Inv),
            Convert.ToInt32(o.SampleSetAdditions).ToString(Inv),
            Convert.ToInt32(o.CustomSampleSet).ToString(Inv),
            Convert.ToInt32(o.SampleVolume).ToString(Inv),
            o.SampleFile ?? "");

        string head = $"{x},{y},{time},{type},{sound}";

        if ((type & 2) != 0)   // slider
        {
            int repeats = Math.Max(1, Convert.ToInt32(o.SegmentCount));
            char curve = Convert.ToInt32(o.CurveType) switch { 0 => 'C', 2 => 'L', 3 => 'P', _ => 'B' };
            var pts = new List<string>();
            var raw = o.sliderCurvePoints;
            if (raw != null)
                for (int i = 1; i < raw.Length / 2; i++)   // the first point is the slider head
                    pts.Add(((int)Math.Round(Convert.ToDouble(raw[i * 2]))).ToString(Inv) + ":" +
                            ((int)Math.Round(Convert.ToDouble(raw[i * 2 + 1]))).ToString(Inv));
            if (pts.Count == 0) pts.Add(x + ":" + y);

            var edgeSounds = new List<int>();
            if (o.SoundTypeList != null) foreach (var v in o.SoundTypeList) edgeSounds.Add(Convert.ToInt32(v));
            var edgeSets = new List<int>();
            if (o.SampleSetList != null) foreach (var v in o.SampleSetList) edgeSets.Add(Convert.ToInt32(v));
            var edgeAdds = new List<int>();
            if (o.SampleSetAdditionsList != null) foreach (var v in o.SampleSetAdditionsList) edgeAdds.Add(Convert.ToInt32(v));
            while (edgeSounds.Count < repeats + 1) edgeSounds.Add(0);
            while (edgeSets.Count < repeats + 1) edgeSets.Add(0);
            while (edgeAdds.Count < repeats + 1) edgeAdds.Add(0);

            string sounds = string.Join("|", edgeSounds.Take(repeats + 1).Select(v => v.ToString(Inv)));
            string sets = string.Join("|", Enumerable.Range(0, repeats + 1)
                .Select(i => edgeSets[i].ToString(Inv) + ":" + edgeAdds[i].ToString(Inv)));

            return $"{head},{curve}|{string.Join("|", pts)},{repeats},{Num(Convert.ToDouble(o.SpatialLength))},{sounds},{sets},{sample}";
        }
        if ((type & 8) != 0)     // spinner
            return $"{head},{end},{sample}";
        if ((type & 128) != 0)   // mania hold note
            return $"{head},{end}:{sample}";
        return $"{head},{sample}";   // circle
    }

    static string Num(double v) => Math.Round(v, 4).ToString(Inv);

    // Keeps only the newest backups so the folder does not grow forever
    const int MaxBackups = 50;
    static void CleanBackups()
    {
        try
        {
            foreach (var old in new DirectoryInfo(BackupDir).GetFiles("*.osu")
                         .OrderByDescending(f => f.CreationTimeUtc).Skip(MaxBackups))
                old.Delete();
        }
        catch { }
    }

    // How many objects are selected in the editor (-1 if the editor could not be read)
    public static int SelectedCount()
    {
        try
        {
            var reader = ReadEditor(GetOsuProcess());
            return reader.hitObjects.Count(o => o.IsSelected && IsSane(o));
        }
        catch { return -1; }
    }

    // ---------------------------------------------------------------- UNDO
    // Takes back only the last insert: removes the pattern's objects and puts back the objects it replaced.
    // Everything else you did in the editor after the insert stays.
    public class InsertRecord
    {
        public string Map { get; set; }
        public string Backup { get; set; }
        public string Name { get; set; }
        public List<string> Inserted { get; set; } = new List<string>();
        public List<string> Removed { get; set; } = new List<string>();
    }

    static List<InsertRecord> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFile))
                return System.Text.Json.JsonSerializer.Deserialize<List<InsertRecord>>(File.ReadAllText(HistoryFile)) ?? new List<InsertRecord>();
        }
        catch { }
        return new List<InsertRecord>();
    }

    static void SaveHistory(List<InsertRecord> history)
    {
        Directory.CreateDirectory(BackupDir);
        if (history.Count > MaxHistory) history.RemoveRange(0, history.Count - MaxHistory);
        File.WriteAllText(HistoryFile, System.Text.Json.JsonSerializer.Serialize(history), new UTF8Encoding(false));
    }

    // Identifies an object by position, time and kind (osu! may write the rest of the line a bit differently when it saves)
    static string Key(string line)
    {
        var p = line.Split(',');
        if (p.Length < 4) return line.Trim();
        int type = int.TryParse(p[3], NumberStyles.Integer, Inv, out int ty) ? ty & (1 | 2 | 8 | 128) : 0;
        return $"{Fmt(F(p[0]))},{Fmt(F(p[1]))},{Fmt(GetTime(line))},{type}";
    }

    static double F(string s) => double.TryParse(s, NumberStyles.Float, Inv, out double v) ? v : 0;

    public static string Undo()
    {
        var history = LoadHistory();
        if (history.Count == 0)
            throw new Exception(Lang.T("nothingToUndo"));
        var rec = history[history.Count - 1];
        if (!File.Exists(rec.Map))
        {
            history.RemoveAt(history.Count - 1);
            SaveHistory(history);
            throw new Exception(Lang.T("mapMissing", rec.Map));
        }

        // Save the editor first, so changes made after the insert are kept
        var osu = Process.GetProcessesByName("osu!").FirstOrDefault();
        bool editorOnThisMap = false;
        try
        {
            if (osu != null && osu.MainWindowTitle.EndsWith(".osu"))
            {
                var reader = ReadEditor(osu);
                editorOnThisMap = string.Equals(GetMapPath(osu, reader), rec.Map, StringComparison.OrdinalIgnoreCase);
                if (editorOnThisMap) SaveInEditor(osu, rec.Map);
            }
        }
        catch { }

        var lines = File.ReadAllLines(rec.Map).ToList();
        int start = lines.FindIndex(l => l.Trim() == "[HitObjects]");
        if (start < 0) throw new Exception(Lang.T("nothingToUndo"));
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
        var objects = lines.Skip(start + 1).Take(end - start - 1).Where(l => l.Trim().Length > 0).ToList();

        // remove the pattern's objects (each one once)
        var toRemove = rec.Inserted.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        int taken = 0;
        var kept = new List<string>();
        foreach (var l in objects)
        {
            string k = Key(l);
            if (toRemove.TryGetValue(k, out int n) && n > 0) { toRemove[k] = n - 1; taken++; }
            else kept.Add(l);
        }
        // put back the objects that the pattern replaced (unless they are already there)
        var existing = new HashSet<string>(kept.Select(Key));
        foreach (var l in rec.Removed)
            if (existing.Add(Key(l))) kept.Add(l);
        kept = kept.OrderBy(GetTime).ToList();

        var result = new List<string>();
        result.AddRange(lines.Take(start + 1));
        result.AddRange(kept);
        if (end < lines.Count) { result.Add(""); result.AddRange(lines.Skip(end)); }
        WriteFile(rec.Map, result);

        history.RemoveAt(history.Count - 1);
        SaveHistory(history);

        if (osu != null && editorOnThisMap) ReloadEditor(osu);
        string msg = Lang.T("undoneN", rec.Name ?? "", taken);
        return history.Count > 0 ? msg + " " + Lang.T("undoMore", history.Count) : msg;
    }

    // ---------------------------------------------------------------- HELPERS
    static Process GetOsuProcess()
    {
        var osu = Process.GetProcessesByName("osu!").FirstOrDefault();
        if (osu == null)
            throw new Exception(Lang.T("osuNotRunning"));
        if (!osu.MainWindowTitle.EndsWith(".osu"))
            throw new Exception(Lang.T("editorNotOpen"));
        return osu;
    }

    static EditorReader ReadEditor(Process osu)
    {
        try
        {
            var reader = new EditorReader();
            reader.SetProcess(osu);
            reader.autoDeStack = true;   // real positions, without the stacking offset
            reader.FetchAll();
            return reader;
        }
        catch (Exception e)
        {
            throw new Exception(Lang.T("readFail", e.Message));
        }
    }

    static string GetMapPath(Process osu, EditorReader reader)
    {
        string mapPath = Path.Combine(GetSongsPath(osu), reader.ContainingFolder, reader.Filename);
        if (!File.Exists(mapPath))
            throw new Exception(Lang.T("mapMissing", mapPath));
        return mapPath;
    }

    static string GetSongsPath(Process osu)
    {
        string osuDir;
        try { osuDir = Path.GetDirectoryName(osu.MainModule.FileName); }
        catch { osuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!"); }

        // A custom Songs folder may be set in osu!.<user>.cfg
        try
        {
            foreach (var cfg in Directory.GetFiles(osuDir, "osu!.*.cfg"))
            {
                foreach (var line in File.ReadAllLines(cfg))
                {
                    if (!line.StartsWith("BeatmapDirectory")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string dir = line.Substring(eq + 1).Trim();
                    if (dir.Length == 0) continue;
                    string full = Path.IsPathRooted(dir) ? dir : Path.Combine(osuDir, dir);
                    if (Directory.Exists(full)) return full;
                }
            }
        }
        catch { }
        return Path.Combine(osuDir, "Songs");
    }

    // Presses Ctrl+S in the editor so the file on disk matches the editor
    static void SaveInEditor(Process osu, string mapPath)
    {
        DateTime before = File.GetLastWriteTimeUtc(mapPath);
        FocusOsu(osu);
        SendKeys.SendWait("^s");
        for (int i = 0; i < 20 && File.GetLastWriteTimeUtc(mapPath) == before; i++)
            Thread.Sleep(100);
        Thread.Sleep(300);
    }

    public static List<string> GetSection(string[] lines, string section)
    {
        var list = new List<string>();
        bool inside = false;
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            if (l.StartsWith("[")) { inside = l == "[" + section + "]"; continue; }
            if (inside && l.Length > 0 && !l.StartsWith("//")) list.Add(l);
        }
        return list;
    }

    public static double GetTime(string line)
    {
        var p = line.Split(',');
        return p.Length > 2 && double.TryParse(p[2], NumberStyles.Float, Inv, out double t) ? t : 0;
    }

    static double TimingTime(string line)
    {
        var p = line.Split(',');
        return double.TryParse(p[0], NumberStyles.Float, Inv, out double t) ? t : 0;
    }

    static bool IsRedLine(string line)
    {
        var p = line.Split(',');
        return p.Length < 7 || p[6].Trim() == "1";
    }

    // Shifts the object's time (and spinner / hold note end time)
    static string ShiftObject(string line, double offset)
    {
        var p = line.Split(',');
        double t = double.Parse(p[2], NumberStyles.Float, Inv);
        p[2] = Fmt(t + offset);

        int type = int.Parse(p[3], Inv);
        if ((type & 8) != 0 && p.Length > 5)            // spinner: endTime
        {
            p[5] = Fmt(double.Parse(p[5], NumberStyles.Float, Inv) + offset);
        }
        else if ((type & 128) != 0 && p.Length > 5)     // mania hold: endTime:hitSample
        {
            var h = p[5].Split(':');
            h[0] = Fmt(double.Parse(h[0], NumberStyles.Float, Inv) + offset);
            p[5] = string.Join(":", h);
        }
        return string.Join(",", p);
    }

    static string Fmt(double t) => ((int)Math.Round(t)).ToString(Inv);

    static string FormatTime(int ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}:{ts.Milliseconds:000}";
    }

    static void WriteFile(string path, List<string> lines)
    {
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
    }

    static void FocusOsu(Process osu)
    {
        if (GetForegroundWindow() != osu.MainWindowHandle)
        {
            SetForegroundWindow(osu.MainWindowHandle);
            Thread.Sleep(300);
        }
    }

    // Same method as ForceReloadEditor in Mapping Tools
    static void ReloadEditor(Process osu)
    {
        FocusOsu(osu);
        SendKeys.SendWait("^{L 10}");
        Thread.Sleep(100);
        SendKeys.SendWait("{ENTER}");
    }
}
