using System.Numerics;
using SkiaSharp;

namespace DxfViewer;

// Format-agnostic geometry/color helpers shared by DxfParser (netDxf) and DwgParser (ACadSharp).
internal static class CadGeometry
{
    public const double ArcDegreesPerSeg = 0.5;

    // Microvellum's own viewer recolors these known cabinetry-cutlist layer name
    // conventions regardless of whatever color the source file actually stores on
    // those entities (verified against a real file: BORDER_Z0P04 is explicitly ACI
    // 7/white in the DXF data itself, yet Microvellum renders it blue). Returns null
    // for anything that isn't one of these known conventions, meaning "leave the
    // entity's own resolved color alone".
    //
    // 2D_DIM is split by entity kind: the dimension/extension lines and tick marks
    // render blue (same as BORDER), while the dimension value text renders red --
    // confirmed against Microvellum's own rendering of the same file, where the
    // two never share a color even though both come from the same DIMENSION entity.
    public static SKColor? LayerColorOverride(string layerName, bool isText = false)
    {
        if (layerName.StartsWith("BORDER", StringComparison.OrdinalIgnoreCase)) return AciIndexToSKColor(5);  // blue
        if (layerName.StartsWith("2D_DIM", StringComparison.OrdinalIgnoreCase)) return isText ? AciIndexToSKColor(1) : AciIndexToSKColor(5);
        if (layerName.StartsWith("ROUTE",  StringComparison.OrdinalIgnoreCase)) return AciIndexToSKColor(3);  // green
        return null;
    }

    // Greedy label-collision avoidance shared by the interactive renderer and the
    // --render-test harness. A label's footprint uses its rendered *length* along its
    // own reading direction, not just its character height -- a run of short rotated
    // numeric dimension values (DXF group-code 11/21 midpoints, already positioned
    // correctly by the source CAD software) is much longer than it is tall, and sizing
    // the footprint off height alone made this drastically under-estimate real overlap.
    //
    // Collision is a proper anisotropic bounding-box test (along the reading direction
    // vs. across it), not a single Euclidean distance: several short consecutive
    // dimension values (e.g. 0.837/0.374/0.952 back-to-back) can sit close enough along
    // the chain that their text footprints genuinely overlap even though each one's own
    // position is exactly where the source data put it. When that happens, stagger the
    // overflow label onto a new line by nudging perpendicular to the reading direction
    // and re-checking, up to a few times -- the same "kick it to the next row" behavior
    // Microvellum's own dimension text uses for cramped chains, rather than leaving
    // genuinely fused digits on screen.
    public static (float X, float Y, float Footprint) PlaceText(
        IReadOnlyList<(float X, float Y, float Footprint)> placed,
        float x, float y, float height, float rotation, string value)
    {
        bool isVertical = MathF.Abs(rotation - 90f) < 1f;
        float footprint = MathF.Max(height, value.Length * height * 0.6f);
        float ax = x, ay = y;

        for (int iter = 0; iter < 6; iter++)
        {
            bool collided = false;
            foreach (var (px, py, pFootprint) in placed)
            {
                float alongDist = isVertical ? MathF.Abs(ay - py) : MathF.Abs(ax - px);
                float crossDist = isVertical ? MathF.Abs(ax - px) : MathF.Abs(ay - py);
                bool alongOverlap = alongDist < (footprint + pFootprint) * 0.5f;
                bool crossOverlap = crossDist < height * 1.1f;
                if (!alongOverlap || !crossOverlap) continue;

                if (isVertical) ax += height * 1.2f;
                else            ay -= height * 1.2f;
                collided = true;
                break;
            }
            if (!collided) break;
        }

        return (ax, ay, footprint);
    }

    // MText stores its content as one formatted blob plus a wrap column. PlainText()
    // flattens the formatting but keeps the hard breaks, so split on those first and then
    // soft-wrap each paragraph to the stored column width. Width is estimated from the
    // glyph height rather than measured, because wrapping happens at parse time where no
    // font or canvas exists yet -- close enough for the short labels these files carry.
    public static List<string> WrapMText(string text, double height, double wrapWidth)
    {
        var paragraphs = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var outLines = new List<string>();

        if (wrapWidth <= 1e-6 || height <= 1e-9)
        {
            outLines.AddRange(paragraphs);
            return outLines;
        }

        int maxChars = Math.Max(1, (int)(wrapWidth / (height * 0.6)));
        foreach (var para in paragraphs)
        {
            if (para.Length <= maxChars) { outLines.Add(para); continue; }

            var line = new System.Text.StringBuilder();
            foreach (var word in para.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > maxChars)
                {
                    outLines.Add(line.ToString());
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            if (line.Length > 0) outLines.Add(line.ToString());
        }
        return outLines;
    }

    // Offset from a text's stored anchor point to the baseline-left origin Skia draws at,
    // expressed in the text's own rotated frame with screen-Y (down-positive) convention.
    public static (float Dx, float Dy) AlignOffset(TextHAlign h, TextVAlign v, float width, float height) =>
        (h switch
         {
             TextHAlign.Center => -width / 2f,
             TextHAlign.Right  => -width,
             _                 => 0f,
         },
         v switch
         {
             TextVAlign.Top    => height,
             TextVAlign.Middle => height / 2f,
             _                 => 0f,
         });

    // The full 256-entry AutoCAD Color Index table, taken from netDxf's own implementation
    // rather than transcribed by hand -- the previous approximation interpolated seven
    // decades and collapsed everything else (30-39, 50-59, 70-79, 90-99, 110-119 and all
    // of 130-255) to a single flat gray.
    private static readonly SKColor[] AciTable = BuildAciTable();

    private static SKColor[] BuildAciTable()
    {
        var table = new SKColor[256];
        for (int i = 0; i < 256; i++)
        {
            try
            {
                var c = new netDxf.AciColor((short)i).ToColor();
                table[i] = new SKColor(c.R, c.G, c.B);
            }
            catch
            {
                table[i] = new SKColor(255, 255, 255);
            }
        }
        return table;
    }

    // Standard AutoCAD linetype patterns, as dash/gap run lengths in drawing units. Named
    // rather than read from the file's own LTYPE table because every real export uses the
    // stock ACAD names, and the name is the one thing both parser paths can see. A dot is
    // emitted as a very short dash -- Skia has no zero-length dash concept.
    public static float[]? LinetypeDash(string? name, double scale = 1.0)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().ToUpperInvariant();
        if (n is "CONTINUOUS" or "BYLAYER" or "BYBLOCK" or "SOLID") return null;

        // Trailing 2 / X suffixes are ACAD's half- and double-scale variants.
        double mult = 1.0;
        if (n.EndsWith("X2")) { mult = 2.0; n = n[..^2]; }
        else if (n.EndsWith("2")) { mult = 0.5; n = n[..^1]; }

        float[]? baseline = n switch
        {
            "DASHED"   or "DASH"     => new[] { 0.5f,  0.25f },
            "HIDDEN"                 => new[] { 0.25f, 0.125f },
            "CENTER"                 => new[] { 1.25f, 0.25f, 0.25f, 0.25f },
            "PHANTOM"                => new[] { 1.25f, 0.25f, 0.25f, 0.25f, 0.25f, 0.25f },
            "DOT"                    => new[] { 0.01f, 0.25f },
            "DASHDOT"  or "DASHDOT2" => new[] { 0.5f,  0.25f, 0.01f, 0.25f },
            "DIVIDE"                 => new[] { 0.5f,  0.25f, 0.01f, 0.25f, 0.01f, 0.25f },
            "BORDER"                 => new[] { 0.5f,  0.25f, 0.5f,  0.25f, 0.01f, 0.25f },
            _                        => null,
        };
        if (baseline == null) return null;

        double s = mult * (scale > 1e-9 ? scale : 1.0);
        var result = new float[baseline.Length];
        for (int i = 0; i < baseline.Length; i++) result[i] = (float)(baseline[i] * s);
        return result;
    }

    public static SKColor AciIndexToSKColor(short idx) =>
        idx >= 0 && idx < 256 ? AciTable[idx] : new SKColor(255, 255, 255);

    public static string DecodeDxfText(string s)
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
                    case 'c': sb.Append('∅'); i += 3; break; // diameter
                    case 'd': sb.Append('°'); i += 3; break; // degree
                    case 'p': sb.Append('±'); i += 3; break; // plus-minus
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

    // Orbit-camera basis (world Z-up) shared by the interactive 3D renderer
    // (DxfTabControl) and the --render-test harness (Program.cs), so the two
    // can't silently drift apart. Orthographic: project a point with
    // Dot(point - center, Right) / Dot(point - center, Up).
    public static (Vector3 Right, Vector3 Up) OrbitCameraBasis(float azimuthRad, float elevationRad)
    {
        float cosEl = MathF.Cos(elevationRad), sinEl = MathF.Sin(elevationRad);
        float cosAz = MathF.Cos(azimuthRad), sinAz = MathF.Sin(azimuthRad);
        var eyeDir = new Vector3(cosEl * cosAz, cosEl * sinAz, sinEl);
        var worldUp = new Vector3(0, 0, 1);
        var right = Vector3.Normalize(Vector3.Cross(worldUp, eyeDir));
        var up = Vector3.Cross(eyeDir, right);
        return (right, up);
    }

    public static IEnumerable<(double X, double Y)> BulgePoints((double X, double Y) from, (double X, double Y) to, double bulge)
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
        // Point generation must use the radius *magnitude* -- startAngle (via atan2)
        // and the parametric circle equation both assume a positive radius. Using the
        // signed r here (negative whenever bulge is negative, i.e. any clockwise arc)
        // silently reflects every generated point 180 degrees around the center,
        // sending the arc to the far side of a circle that can be huge for a gentle
        // bulge on a long chord -- exactly the "wild stray line" artifact this caused.
        double absR = Math.Abs(r);
        for (int i = 1; i <= segs; i++)
        {
            double a = startAngle + i * step;
            yield return (cx + absR * Math.Cos(a), cy + absR * Math.Sin(a));
        }
    }
}
