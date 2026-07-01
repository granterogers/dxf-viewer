using System.IO;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using SkiaSharp;
using AciColor = netDxf.AciColor;

namespace DxfViewer;

public static class DxfParser
{
    private const double ArcDegreesPerSeg = 0.5;

    public static DxfScene Parse(string filePath)
    {
        // Try netDxf first — handles modern DXF with a $ACADVER header.
        try
        {
            DxfDocument doc;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                doc = DxfDocument.Load(fs);

            if (HasAnyEntities(doc))
            {
                var scene = new DxfScene();
                ParseDocument(scene, doc);
                scene.ComputeBounds();
                return scene;
            }
        }
        catch { }

        return ParseLegacy(filePath);
    }

    private static bool HasAnyEntities(DxfDocument doc) =>
        doc.Lines.Any() || doc.Arcs.Any() || doc.Circles.Any() ||
        doc.Ellipses.Any() || doc.LwPolylines.Any() || doc.Polylines.Any() ||
        doc.Texts.Any() || doc.MTexts.Any() || doc.Inserts.Any() ||
        doc.Splines.Any() || doc.Points.Any();

    // ─── Modern parser (netDxf) ──────────────────────────────────────────────

    private static void ParseDocument(DxfScene scene, DxfDocument doc)
    {
        foreach (var e in doc.Lines)       AddLine(scene, e, null);
        foreach (var e in doc.Arcs)        AddArc(scene, e, null);
        foreach (var e in doc.Circles)     AddCircle(scene, e, null);
        foreach (var e in doc.Ellipses)    AddEllipse(scene, e, null);
        foreach (var e in doc.LwPolylines) AddLwPolyline(scene, e, null);
        foreach (var e in doc.Polylines)   AddPolyline(scene, e, null);
        foreach (var e in doc.Splines)     AddSpline(scene, e, null);
        foreach (var e in doc.Texts)       AddText(scene, e, null);
        foreach (var e in doc.MTexts)      AddMText(scene, e, null);
        foreach (var e in doc.Inserts)     AddInsert(scene, e);
    }

    private static void AddEntityFromBlock(DxfScene scene, EntityObject entity, Layer? blockLayer, Matrix4 xform)
    {
        switch (entity)
        {
            case Line e:       AddLine(scene, e, blockLayer, xform); break;
            case Arc e:        AddArc(scene, e, blockLayer, xform); break;
            case Circle e:     AddCircle(scene, e, blockLayer, xform); break;
            case LwPolyline e: AddLwPolyline(scene, e, blockLayer, xform); break;
            case Polyline e:   AddPolyline(scene, e, blockLayer, xform); break;
            case Text e:       AddText(scene, e, blockLayer); break;
            case MText e:      AddMText(scene, e, blockLayer); break;
        }
    }

    private static void AddInsert(DxfScene scene, Insert ins)
    {
        if (ins.Block == null) return;

        double sinR = Math.Sin(ins.Rotation * Math.PI / 180);
        double cosR = Math.Cos(ins.Rotation * Math.PI / 180);
        double sx = ins.Scale.X, sy = ins.Scale.Y;
        double tx = ins.Position.X, ty = ins.Position.Y;

        // Build 2D transform: scale → rotate → translate
        var xform = new Matrix4(
            cosR * sx, -sinR * sy, 0, tx,
            sinR * sx,  cosR * sy, 0, ty,
            0, 0, 1, 0,
            0, 0, 0, 1);

        var blockLayer = ins.Layer;
        foreach (var entity in ins.Block.Entities)
            AddEntityFromBlock(scene, entity, blockLayer, xform);
    }

    private static Vector3 ApplyXform(Vector3 pt, Matrix4 xform)
    {
        return new Vector3(
            xform.M11 * pt.X + xform.M12 * pt.Y + xform.M14,
            xform.M21 * pt.X + xform.M22 * pt.Y + xform.M24,
            0);
    }

    private static void AddLine(DxfScene scene, Line e, Layer? bl, Matrix4? xf = null)
    {
        var s = xf.HasValue ? ApplyXform(e.StartPoint, xf.Value) : e.StartPoint;
        var p = xf.HasValue ? ApplyXform(e.EndPoint, xf.Value) : e.EndPoint;
        scene.Lines.Add(new SceneLine((float)s.X, (float)s.Y, (float)p.X, (float)p.Y,
            ResolveColor(e, bl)));
    }

    private static void AddCircle(DxfScene scene, Circle e, Layer? bl, Matrix4? xf = null)
    {
        var c = xf.HasValue ? ApplyXform(e.Center, xf.Value) : e.Center;
        float scale = xf.HasValue ? (float)Math.Sqrt(xf.Value.M11 * xf.Value.M11 + xf.Value.M21 * xf.Value.M21) : 1f;
        scene.Circles.Add(new SceneCircle((float)c.X, (float)c.Y, (float)e.Radius * scale,
            ResolveColor(e, bl)));
    }

    private static void AddArc(DxfScene scene, Arc e, Layer? bl, Matrix4? xf = null)
    {
        var c = xf.HasValue ? ApplyXform(e.Center, xf.Value) : e.Center;
        float scale = xf.HasValue ? (float)Math.Sqrt(xf.Value.M11 * xf.Value.M11 + xf.Value.M21 * xf.Value.M21) : 1f;
        scene.Arcs.Add(new SceneArc((float)c.X, (float)c.Y, (float)e.Radius * scale,
            (float)e.StartAngle, (float)e.EndAngle, ResolveColor(e, bl)));
    }

    private static void AddEllipse(DxfScene scene, Ellipse e, Layer? bl, Matrix4? xf = null)
    {
        // Discretize to polyline
        double cx = e.Center.X, cy = e.Center.Y;
        double rx = e.MajorAxis / 2;
        double ry = e.MajorAxis * e.MinorAxis / 2;
        double rotRad = e.Rotation * Math.PI / 180;
        bool isArc = Math.Abs(e.StartAngle) > 1e-6 || Math.Abs(e.EndAngle - 360) > 1e-6;
        double startRad = e.StartAngle * Math.PI / 180;
        double endRad = e.EndAngle * Math.PI / 180;
        if (endRad <= startRad) endRad += 2 * Math.PI;
        double span = isArc ? endRad - startRad : 2 * Math.PI;
        int segs = Math.Max(16, (int)(span * 180 / Math.PI / ArcDegreesPerSeg));

        var poly = new ScenePolyline { Closed = !isArc, Color = ResolveColor(e, bl) };
        for (int i = 0; i <= segs; i++)
        {
            double t = startRad + i * span / segs;
            double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
            double wx = cx + ex * Math.Cos(rotRad) - ey * Math.Sin(rotRad);
            double wy = cy + ex * Math.Sin(rotRad) + ey * Math.Cos(rotRad);
            var pt = xf.HasValue ? ApplyXform(new Vector3(wx, wy, 0), xf.Value) : new Vector3(wx, wy, 0);
            poly.Points.Add(new SKPoint((float)pt.X, (float)pt.Y));
        }
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);
    }

    private static void AddLwPolyline(DxfScene scene, LwPolyline e, Layer? bl, Matrix4? xf = null)
    {
        var verts = e.Vertexes;
        if (verts.Count < 2) return;

        var poly = new ScenePolyline { Closed = e.IsClosed, Color = ResolveColor(e, bl) };
        for (int i = 0; i < verts.Count; i++)
        {
            int ni = (i + 1) % verts.Count;
            if (ni == 0 && !e.IsClosed) { AddVert(verts[i].Position); break; }

            AddVert(verts[i].Position);

            if (Math.Abs(verts[i].Bulge) > 1e-9)
            {
                foreach (var bp in BulgePoints(verts[i].Position, verts[ni].Position, verts[i].Bulge))
                    AddVertRaw(bp);
            }
        }
        if (!e.IsClosed && verts.Count > 0) AddVert(verts[^1].Position);
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);

        void AddVert(Vector2 p2)
        {
            var v3 = xf.HasValue ? ApplyXform(new Vector3(p2.X, p2.Y, 0), xf.Value) : new Vector3(p2.X, p2.Y, 0);
            poly.Points.Add(new SKPoint((float)v3.X, (float)v3.Y));
        }
        void AddVertRaw(Vector2 p2)
        {
            var v3 = xf.HasValue ? ApplyXform(new Vector3(p2.X, p2.Y, 0), xf.Value) : new Vector3(p2.X, p2.Y, 0);
            poly.Points.Add(new SKPoint((float)v3.X, (float)v3.Y));
        }
    }

    private static IEnumerable<Vector2> BulgePoints(Vector2 from, Vector2 to, double bulge)
    {
        double angle = 4.0 * Math.Atan(Math.Abs(bulge));
        int segs = Math.Max(4, (int)(angle * 180 / Math.PI / ArcDegreesPerSeg));
        double totalAngle = bulge >= 0 ? angle : -angle;

        double dx = to.X - from.X, dy = to.Y - from.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12) yield break;
        double r = d / (2 * Math.Sin(totalAngle / 2));
        double mid = Math.Cos(totalAngle / 2);
        double cx = (from.X + to.X) / 2 - r * mid * dy / d;
        double cy = (from.Y + to.Y) / 2 + r * mid * dx / d;
        double startAngle = Math.Atan2(from.Y - cy, from.X - cx);
        double step = totalAngle / segs;
        for (int i = 1; i <= segs; i++)
        {
            double a = startAngle + i * step;
            yield return new Vector2(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
    }

    private static void AddPolyline(DxfScene scene, Polyline e, Layer? bl, Matrix4? xf = null)
    {
        var verts = e.Vertexes;
        if (verts.Count < 2) return;

        var poly = new ScenePolyline { Closed = e.IsClosed, Color = ResolveColor(e, bl) };
        foreach (var v in verts)
        {
            var pt = xf.HasValue ? ApplyXform(v.Position, xf.Value) : v.Position;
            poly.Points.Add(new SKPoint((float)pt.X, (float)pt.Y));
        }
        scene.Polylines.Add(poly);
    }

    private static void AddSpline(DxfScene scene, Spline e, Layer? bl)
    {
        var pts = e.PolygonalVertexes(64);
        if (pts == null || pts.Count < 2) return;
        var poly = new ScenePolyline { Color = ResolveColor(e, bl) };
        foreach (var p in pts)
            poly.Points.Add(new SKPoint((float)p.X, (float)p.Y));
        scene.Polylines.Add(poly);
    }

    private static void AddText(DxfScene scene, Text e, Layer? bl)
    {
        if (string.IsNullOrWhiteSpace(e.Value)) return;
        double h = e.Height > 0 ? e.Height : 2.5;
        scene.Texts.Add(new SceneText(
            (float)e.Position.X, (float)e.Position.Y,
            (float)h, (float)e.Rotation,
            DecodeDxfText(e.Value), ResolveColor(e, bl)));
    }

    private static void AddMText(DxfScene scene, MText e, Layer? bl)
    {
        var raw = DecodeDxfText(e.PlainText());
        if (string.IsNullOrWhiteSpace(raw)) return;
        double h = e.Height > 0 ? e.Height : 2.5;
        scene.Texts.Add(new SceneText(
            (float)e.Position.X, (float)e.Position.Y,
            (float)h, (float)e.Rotation,
            raw, ResolveColor(e, bl)));
    }

    private static string DecodeDxfText(string s)
    {
        if (!s.Contains("%%")) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '%' && i + 1 < s.Length && s[i + 1] == '%' && i + 2 < s.Length)
            {
                char c = s[i + 2];
                switch (char.ToLower(c))
                {
                    case 'c': sb.Append('∅'); i += 3; break; // ∅ diameter
                    case 'd': sb.Append('°'); i += 3; break; // °
                    case 'p': sb.Append('±'); i += 3; break; // ±
                    case 'u': case 'o': i += 3; break;            // toggle, skip
                    default:
                        if (char.IsDigit(c) && i + 4 < s.Length &&
                            char.IsDigit(s[i + 3]) && char.IsDigit(s[i + 4]))
                        {
                            int code = (c - '0') * 100 + (s[i + 3] - '0') * 10 + (s[i + 4] - '0');
                            sb.Append((char)code);
                            i += 5;
                        }
                        else { sb.Append('%'); i++; }
                        break;
                }
            }
            else { sb.Append(s[i++]); }
        }
        return sb.ToString();
    }

    // ─── Color resolution ────────────────────────────────────────────────────

    private static SKColor ResolveColor(EntityObject entity, Layer? blockLayer)
    {
        var aci = entity.Color;
        if (aci.IsByBlock) aci = blockLayer?.Color ?? AciColor.Default;
        if (aci.IsByLayer) aci = entity.Layer?.Color ?? AciColor.Default;
        if (aci.IsByLayer || aci.IsByBlock) return new SKColor(230, 230, 230);
        if (aci.UseTrueColor) return new SKColor(aci.R, aci.G, aci.B);
        return AciIndexToSKColor(aci.Index);
    }

    private static SKColor AciIndexToSKColor(short idx) => idx switch
    {
        1  => new SKColor(255,   0,   0),
        2  => new SKColor(255, 255,   0),
        3  => new SKColor(  0, 255,   0),
        4  => new SKColor(  0, 255, 255),
        5  => new SKColor(  0,   0, 255),
        6  => new SKColor(255,   0, 255),
        7  => new SKColor(230, 230, 230),
        8  => new SKColor(128, 128, 128),
        9  => new SKColor(192, 192, 192),
        _  => AciPaletteApprox(idx)
    };

    private static SKColor AciPaletteApprox(short idx)
    {
        if (idx is >= 10 and <= 19) return new SKColor(255, (byte)(255 - (idx - 10) * 28), 0);
        if (idx is >= 20 and <= 29) return new SKColor(255, (byte)((idx - 20) * 28), 0);
        if (idx is >= 40 and <= 49) return new SKColor((byte)(255 - (idx - 40) * 28), 255, 0);
        if (idx is >= 60 and <= 69) return new SKColor(0, 255, (byte)((idx - 60) * 28));
        if (idx is >= 80 and <= 89) return new SKColor(0, (byte)(255 - (idx - 80) * 28), 255);
        if (idx is >= 100 and <= 109) return new SKColor((byte)((idx - 100) * 28), 0, 255);
        if (idx is >= 120 and <= 129) return new SKColor(255, 0, (byte)(255 - (idx - 120) * 28));
        return new SKColor(200, 200, 200);
    }

    // ─── Legacy (headerless) DXF parser ─────────────────────────────────────
    // Handles pre-R12 files that lack $ACADVER/HEADER/BLOCKS.
    // Entities: POLYLINE+VERTEX+SEQEND, CIRCLE, ARC, LINE, TEXT.
    // DIMENSION and everything else is silently skipped.

    private readonly record struct GrpRec(int Code, string Value);

    private static DxfScene ParseLegacy(string filePath)
    {
        var recs = ReadGroupCodes(filePath);
        var scene = new DxfScene();
        int start = FindSection(recs, "ENTITIES");
        if (start >= 0) ParseLegacyEntities(scene, recs, start);
        scene.ComputeBounds();
        return scene;
    }

    private static List<GrpRec> ReadGroupCodes(string filePath)
    {
        var result = new List<GrpRec>(512);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        while (true)
        {
            var codeLine = sr.ReadLine();
            if (codeLine == null) break;
            var valLine = sr.ReadLine();
            if (valLine == null) break;
            if (int.TryParse(codeLine.Trim(), out int code))
                result.Add(new GrpRec(code, valLine.Trim()));
        }
        return result;
    }

    private static int FindSection(List<GrpRec> recs, string name)
    {
        for (int i = 0; i < recs.Count - 1; i++)
            if (recs[i].Code == 0 && recs[i].Value == "SECTION" &&
                recs[i + 1].Code == 2 && recs[i + 1].Value == name)
                return i + 2;
        return -1;
    }

    private static void ParseLegacyEntities(DxfScene scene, List<GrpRec> recs, int start)
    {
        int i = start;
        while (i < recs.Count)
        {
            var r = recs[i];
            if (r.Code != 0) { i++; continue; }
            switch (r.Value)
            {
                case "ENDSEC": case "EOF": return;
                case "LINE":     i = LegacyLine(scene, recs, i + 1);     break;
                case "CIRCLE":   i = LegacyCircle(scene, recs, i + 1);   break;
                case "ARC":      i = LegacyArc(scene, recs, i + 1);      break;
                case "POLYLINE": i = LegacyPolyline(scene, recs, i + 1); break;
                case "TEXT":     i = LegacyText(scene, recs, i + 1);     break;
                default:         i = NextCode0(recs, i + 1);              break;
            }
        }
    }

    private static int NextCode0(List<GrpRec> recs, int start)
    {
        for (int i = start; i < recs.Count; i++)
            if (recs[i].Code == 0) return i;
        return recs.Count;
    }

    private static SKColor LegacyColor(List<GrpRec> recs, int start, int end)
    {
        for (int i = start; i < Math.Min(end, recs.Count); i++)
            if (recs[i].Code == 62 && short.TryParse(recs[i].Value, out short aci))
                return AciIndexToSKColor(aci);
        return new SKColor(230, 230, 230);
    }

    private static float ParseF(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static int LegacyLine(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: x0 = ParseF(recs[i].Value); break;
                case 20: y0 = ParseF(recs[i].Value); break;
                case 11: x1 = ParseF(recs[i].Value); break;
                case 21: y1 = ParseF(recs[i].Value); break;
            }
        scene.Lines.Add(new SceneLine(x0, y0, x1, y1, LegacyColor(recs, start, end)));
        return end;
    }

    private static int LegacyCircle(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float cx = 0, cy = 0, r = 0;
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: cx = ParseF(recs[i].Value); break;
                case 20: cy = ParseF(recs[i].Value); break;
                case 40: r  = ParseF(recs[i].Value); break;
            }
        if (r > 0)
            scene.Circles.Add(new SceneCircle(cx, cy, r, LegacyColor(recs, start, end)));
        return end;
    }

    private static int LegacyArc(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float cx = 0, cy = 0, r = 0, sa = 0, ea = 180;
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: cx = ParseF(recs[i].Value); break;
                case 20: cy = ParseF(recs[i].Value); break;
                case 40: r  = ParseF(recs[i].Value); break;
                case 50: sa = ParseF(recs[i].Value); break;
                case 51: ea = ParseF(recs[i].Value); break;
            }
        if (r > 0)
            scene.Arcs.Add(new SceneArc(cx, cy, r, sa, ea, LegacyColor(recs, start, end)));
        return end;
    }

    private static int LegacyPolyline(DxfScene scene, List<GrpRec> recs, int start)
    {
        int hdrEnd = NextCode0(recs, start);
        var color = LegacyColor(recs, start, hdrEnd);
        bool closed = false;
        for (int i = start; i < hdrEnd; i++)
            if (recs[i].Code == 70 && int.TryParse(recs[i].Value, out int flags))
                closed = (flags & 1) != 0;

        var poly = new ScenePolyline { Color = color, Closed = closed };
        int i2 = hdrEnd;
        while (i2 < recs.Count)
        {
            if (recs[i2].Code != 0) { i2++; continue; }
            if (recs[i2].Value == "VERTEX")
            {
                int vEnd = NextCode0(recs, i2 + 1);
                float vx = 0, vy = 0;
                for (int j = i2 + 1; j < vEnd; j++)
                {
                    if (recs[j].Code == 10) vx = ParseF(recs[j].Value);
                    else if (recs[j].Code == 20) vy = ParseF(recs[j].Value);
                }
                poly.Points.Add(new SKPoint(vx, vy));
                i2 = vEnd;
            }
            else if (recs[i2].Value == "SEQEND")
            {
                i2 = NextCode0(recs, i2 + 1);
                break;
            }
            else break;
        }
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);
        return i2;
    }

    private static int LegacyText(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float tx = 0, ty = 0, height = 2.5f, rot = 0;
        string value = "";
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: tx     = ParseF(recs[i].Value); break;
                case 20: ty     = ParseF(recs[i].Value); break;
                case 40: height = ParseF(recs[i].Value); break;
                case 50: rot    = ParseF(recs[i].Value); break;
                case  1: value  = DecodeDxfText(recs[i].Value); break;
            }
        if (!string.IsNullOrWhiteSpace(value) && height > 0)
            scene.Texts.Add(new SceneText(tx, ty, height, rot, value, LegacyColor(recs, start, end)));
        return end;
    }
}
