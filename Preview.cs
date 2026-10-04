// Reads a pattern file and turns it into simple data for the preview pictures in the window
// (positions, new combos, slider shapes, BPM).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

static class Preview
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public class PatternData
    {
        public int count { get; set; }
        public int bpm { get; set; }
        public List<ObjData> objs { get; set; } = new List<ObjData>();
    }

    public class ObjData
    {
        public float x { get; set; }
        public float y { get; set; }
        public bool nc { get; set; }        // new combo
        public bool sp { get; set; }        // spinner
        public List<float[]> path { get; set; }   // slider shape (null for circles)
    }

    public static PatternData Build(string patternPath)
    {
        var data = new PatternData();
        string[] lines = File.ReadAllLines(patternPath);

        foreach (var line in Core.GetSection(lines, "HitObjects").OrderBy(Core.GetTime))
        {
            try
            {
                var p = line.Split(',');
                if (p.Length < 4) continue;
                int type = int.Parse(p[3], Inv);
                var o = new ObjData
                {
                    x = F(p[0]),
                    y = F(p[1]),
                    nc = (type & 4) != 0,
                    sp = (type & 8) != 0,
                };
                if ((type & 2) != 0 && p.Length > 5)
                {
                    var pts = SliderPath(new PointF(o.x, o.y), p[5]);
                    if (pts != null) o.path = pts.Select(q => new[] { (float)Math.Round(q.X, 1), (float)Math.Round(q.Y, 1) }).ToList();
                }
                data.objs.Add(o);
            }
            catch { }
        }
        data.count = data.objs.Count;

        // BPM of the first red line (saved together with the pattern)
        foreach (var t in Core.GetSection(lines, "TimingPoints"))
        {
            var p = t.Split(',');
            bool red = p.Length < 7 || p[6].Trim() == "1";
            if (red && p.Length > 1 && double.TryParse(p[1], NumberStyles.Float, Inv, out double beat) && beat > 0)
            {
                data.bpm = (int)Math.Round(60000 / beat);
                break;
            }
        }
        return data;
    }

    static float F(string s) => float.Parse(s, NumberStyles.Float, Inv);

    // Turns "B|x:y|x:y" into a list of points along the slider
    static List<PointF> SliderPath(PointF head, string curve)
    {
        var parts = curve.Split('|');
        char kind = parts[0].Length > 0 ? parts[0][0] : 'B';
        var pts = new List<PointF> { head };
        foreach (var part in parts.Skip(1))
        {
            var xy = part.Split(':');
            if (xy.Length == 2) pts.Add(new PointF(F(xy[0]), F(xy[1])));
        }
        if (pts.Count < 2) return null;

        if (kind == 'P' && pts.Count == 3)
        {
            var arc = Arc(pts[0], pts[1], pts[2]);
            if (arc != null) return arc;
        }
        if (kind == 'B')
        {
            // Red anchors (repeated points) split the slider into separate bezier curves
            var result = new List<PointF>();
            var segment = new List<PointF> { pts[0] };
            for (int i = 1; i < pts.Count; i++)
            {
                if (pts[i] == pts[i - 1])
                {
                    result.AddRange(Bezier(segment));
                    segment = new List<PointF> { pts[i] };
                }
                else segment.Add(pts[i]);
            }
            result.AddRange(Bezier(segment));
            return result;
        }
        return pts;   // L (linear) and C (catmull) drawn as straight lines
    }

    static IEnumerable<PointF> Bezier(List<PointF> c)
    {
        if (c.Count < 3) { foreach (var p in c) yield return p; yield break; }
        const int steps = 30;
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            var tmp = c.ToArray();
            for (int k = tmp.Length - 1; k > 0; k--)
                for (int i = 0; i < k; i++)
                    tmp[i] = new PointF(tmp[i].X + (tmp[i + 1].X - tmp[i].X) * t, tmp[i].Y + (tmp[i + 1].Y - tmp[i].Y) * t);
            yield return tmp[0];
        }
    }

    // Circular arc through three points (osu! "perfect circle" sliders)
    static List<PointF> Arc(PointF a, PointF b, PointF c)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-3) return null;
        double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
        double cx = (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d;
        double cy = (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d;
        double radius = Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy));

        double start = Math.Atan2(a.Y - cy, a.X - cx);
        double end = Math.Atan2(c.Y - cy, c.X - cx);
        bool clockwise = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) > 0;
        if (clockwise) { while (end < start) end += 2 * Math.PI; }
        else { while (end > start) end -= 2 * Math.PI; }

        var list = new List<PointF>();
        const int steps = 40;
        for (int i = 0; i <= steps; i++)
        {
            double ang = start + (end - start) * i / steps;
            list.Add(new PointF((float)(cx + radius * Math.Cos(ang)), (float)(cy + radius * Math.Sin(ang))));
        }
        return list;
    }
}
