using System.Numerics;
using SkiaSharp;

namespace DxfViewer;

public class DxfScene
{
    public readonly List<SceneCircle>   Circles   = new();
    public readonly List<SceneArc>      Arcs      = new();
    public readonly List<SceneLine>     Lines     = new();
    public readonly List<ScenePolyline> Polylines = new();
    public readonly List<SceneText>     Texts     = new();

    // Real 3D wireframe edges (e.g. DWG Solid3D legacy wires). When non-empty the
    // tab switches to an orbit-camera 3D view instead of the 2D pan/zoom view.
    public readonly List<Scene3DWire> Wires3D = new();
    public bool Is3D => Wires3D.Count > 0;

    public bool IsEmpty => Circles.Count == 0 && Arcs.Count == 0 &&
                           Lines.Count == 0 && Polylines.Count == 0 && Texts.Count == 0 &&
                           Wires3D.Count == 0;

    // Bounding box in screen space (DXF Y negated), set by ComputeBounds().
    public SKRect Bounds { get; private set; } = new SKRect(-10, -10, 10, 10);

    // 3D bounding box (world space, Y-up), set by ComputeBounds() when Is3D.
    public Vector3 Bounds3DMin { get; private set; } = new Vector3(-10, -10, -10);
    public Vector3 Bounds3DMax { get; private set; } = new Vector3(10, 10, 10);

    // Unique layers sorted: BORDER* first, ROUTE* second, 2D_DIM* third, rest alphabetically.
    public List<(string Name, SKColor Color)> Layers { get; private set; } = new();

    // What the parser actually did and what it had to skip. Without this an unsupported
    // entity renders as nothing and the user cannot tell "this file has none" from "we
    // don't draw those" -- a dangerous ambiguity in a tool used to verify exports.
    public string ParserUsed = "";

    // What one drawing unit means, from the file's $INSUNITS header. Frequently Unknown:
    // most of this project's corpus is headerless pre-R12 DXF that declares nothing.
    public CadUnits Units = CadUnits.Unknown;
    public readonly SortedSet<string> UnsupportedEntities = new(StringComparer.OrdinalIgnoreCase);
    public string? ParseWarning;

    public int EntityCount => Circles.Count + Arcs.Count + Lines.Count + Polylines.Count + Texts.Count + Wires3D.Count;

    public string DiagnosticsSummary
    {
        get
        {
            var parts = new List<string> { $"parser: {(string.IsNullOrEmpty(ParserUsed) ? "unknown" : ParserUsed)}" };
            parts.Add($"{EntityCount} entities");
            parts.Add($"{Layers.Count} layers");
            parts.Add("units: " + (Units == CadUnits.Unknown
                ? $"not declared (assuming {UnitConvert.Abbreviation(UnitConvert.AssumedWhenUndeclared)})"
                : UnitConvert.Abbreviation(Units)));
            if (UnsupportedEntities.Count > 0)
                parts.Add("skipped: " + string.Join(", ", UnsupportedEntities));
            if (!string.IsNullOrEmpty(ParseWarning)) parts.Add("note: " + ParseWarning);
            return string.Join("  ·  ", parts);
        }
    }

    public void ComputeBounds()
    {
        // Microvellum's own viewer recolors known cabinetry-cutlist layer names
        // regardless of whatever color the file itself stores on those entities
        // (confirmed against a real file: BORDER_Z0P04 is explicitly ACI 7/white in
        // the DXF data, and DIMENSION entities carry no color at all -- yet Microvellum
        // renders them blue and red respectively). Match that convention so the app
        // reads the same way Microvellum does, not just "whatever ACI code happened
        // to get written".
        ApplyLayerColorConventions();

        // Real-world files occasionally carry a handful of vertices with wildly corrupt
        // coordinates -- seen in practice ranging from hundreds of thousands to tens of
        // millions of units on an otherwise tens-of-thousands-unit drawing. A fixed cutoff
        // can't reject both a "slightly bad" and a "wildly bad" outlier at once, so instead
        // collect every point first and use a percentile-based robust range per axis: the
        // 1st/99th percentile marks the real drawing's core, padded generously (2x its own
        // span) so nothing legitimate gets clipped, and anything beyond that is excluded.
        var xs = new List<float>();
        var ys = new List<float>();

        // Layers hidden by default (CNC toolpath/routing annotations, see LayerInfo)
        // must not drive the fit-to-window view or get mistaken for corrupt outliers --
        // they can legitimately range far outside the part's own footprint (shared
        // machine toolpath data), and a user who re-enables them expects to see them
        // exactly as exported, not pruned.
        void Collect(string layer, float x, float y)
        {
            if (IsDefaultHiddenLayer(layer)) return;
            if (!float.IsFinite(x) || !float.IsFinite(y)) return;
            xs.Add(x);
            ys.Add(y);
        }

        foreach (var c in Circles)
        {
            Collect(c.Layer, c.Cx - c.R, -c.Cy - c.R);
            Collect(c.Layer, c.Cx + c.R, -c.Cy + c.R);
        }
        foreach (var a in Arcs)
        {
            Collect(a.Layer, a.Cx - a.R, -a.Cy - a.R);
            Collect(a.Layer, a.Cx + a.R, -a.Cy + a.R);
        }
        foreach (var l in Lines)
        {
            if (l.BoundsExempt) continue;
            Collect(l.Layer, l.X1, -l.Y1);
            Collect(l.Layer, l.X2, -l.Y2);
        }
        foreach (var p in Polylines)
            foreach (var pt in p.Points)
                Collect(p.Layer, pt.X, -pt.Y);
        foreach (var t in Texts)
        {
            if (t.BoundsExempt) continue;
            Collect(t.Layer, t.X, -t.Y);
            // Include approximate top of text (extends above baseline in screen Y = below in DXF Y)
            if (t.Height > 0f) Collect(t.Layer, t.X, -t.Y - t.Height);
        }

        if (xs.Count > 0)
        {
            var (loX, hiX) = RobustRange(xs);
            var (loY, hiY) = RobustRange(ys);
            // Drop the outliers from the scene itself, not just the fit calculation --
            // otherwise a corrupt vertex still draws as a long stray line/ray once the
            // view is correctly zoomed in on the real drawing. Polylines are split at
            // the bad point rather than dropped outright, so the rest of a legitimate
            // curve survives losing one bad vertex. Default-hidden layers are exempt --
            // they didn't shape this range and aren't necessarily corrupt.
            bool InRange(float x, float y) => x >= loX && x <= hiX && y >= loY && y <= hiY;
            Circles.RemoveAll(c => !IsDefaultHiddenLayer(c.Layer) && !InRange(c.Cx, -c.Cy));
            Arcs.RemoveAll(a => !IsDefaultHiddenLayer(a.Layer) && !InRange(a.Cx, -a.Cy));
            Texts.RemoveAll(t => !IsDefaultHiddenLayer(t.Layer) && !t.BoundsExempt && !InRange(t.X, -t.Y));
            Lines.RemoveAll(l => !IsDefaultHiddenLayer(l.Layer) && !l.BoundsExempt && (!InRange(l.X1, -l.Y1) || !InRange(l.X2, -l.Y2)));
            SplitPolylinesAtOutliers(InRange);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            bool any = false;
            void Expand(float x, float y)
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                any = true;
            }
            foreach (var c in Circles) if (!IsDefaultHiddenLayer(c.Layer)) { Expand(c.Cx - c.R, -c.Cy - c.R); Expand(c.Cx + c.R, -c.Cy + c.R); }
            foreach (var a in Arcs)    if (!IsDefaultHiddenLayer(a.Layer)) { Expand(a.Cx - a.R, -a.Cy - a.R); Expand(a.Cx + a.R, -a.Cy + a.R); }
            foreach (var l in Lines)   if (!IsDefaultHiddenLayer(l.Layer) && !l.BoundsExempt) { Expand(l.X1, -l.Y1); Expand(l.X2, -l.Y2); }
            foreach (var p in Polylines)
                if (!IsDefaultHiddenLayer(p.Layer))
                    foreach (var pt in p.Points) Expand(pt.X, -pt.Y);
            foreach (var t in Texts)
                if (!IsDefaultHiddenLayer(t.Layer) && !t.BoundsExempt)
                {
                    Expand(t.X, -t.Y);
                    if (t.Height > 0f) Expand(t.X, -t.Y - t.Height);
                }

            if (any && maxX - minX > 1e-4f && maxY - minY > 1e-4f)
                Bounds = new SKRect(minX, minY, maxX, maxY);
            else if (any)
                Bounds = new SKRect(minX - 10, minY - 10, maxX + 10, maxY + 10);
        }

        if (Wires3D.Count > 0)
        {
            var xs3 = new List<float>();
            var ys3 = new List<float>();
            var zs3 = new List<float>();
            foreach (var w in Wires3D)
                foreach (var p in w.Points)
                {
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
                    xs3.Add(p.X); ys3.Add(p.Y); zs3.Add(p.Z);
                }
            if (xs3.Count > 0)
            {
                var (loX, hiX) = RobustRange(xs3);
                var (loY, hiY) = RobustRange(ys3);
                var (loZ, hiZ) = RobustRange(zs3);
                bool InRange3(Vector3 p) => p.X >= loX && p.X <= hiX && p.Y >= loY && p.Y <= hiY && p.Z >= loZ && p.Z <= hiZ;
                SplitWires3DAtOutliers(InRange3);

                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                bool any3d = false;
                foreach (var w in Wires3D)
                    foreach (var p in w.Points)
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        any3d = true;
                    }
                if (any3d) { Bounds3DMin = min; Bounds3DMax = max; }
            }
        }

        // Compute unique layers with representative color (first entity's color per layer)
        var seen = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase);
        void AddLayer(string layer, SKColor color)
        {
            if (!seen.ContainsKey(layer)) seen[layer] = color;
        }
        foreach (var c in Circles)   AddLayer(c.Layer,   c.Color);
        foreach (var a in Arcs)      AddLayer(a.Layer,   a.Color);
        foreach (var l in Lines)     AddLayer(l.Layer,   l.Color);
        foreach (var p in Polylines) AddLayer(p.Layer,   p.Color);
        foreach (var t in Texts)     AddLayer(t.Layer,   t.Color);
        foreach (var w in Wires3D)   AddLayer(w.Layer,   w.Color);

        Layers = seen
            .OrderBy(kv => LayerSortKey(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private void ApplyLayerColorConventions()
    {
        for (int i = 0; i < Circles.Count; i++)
        {
            var ov = CadGeometry.LayerColorOverride(Circles[i].Layer);
            if (ov.HasValue) Circles[i] = Circles[i] with { Color = ov.Value };
        }
        for (int i = 0; i < Arcs.Count; i++)
        {
            var ov = CadGeometry.LayerColorOverride(Arcs[i].Layer);
            if (ov.HasValue) Arcs[i] = Arcs[i] with { Color = ov.Value };
        }
        for (int i = 0; i < Lines.Count; i++)
        {
            var ov = CadGeometry.LayerColorOverride(Lines[i].Layer);
            if (ov.HasValue) Lines[i] = Lines[i] with { Color = ov.Value };
        }
        foreach (var p in Polylines)
        {
            var ov = CadGeometry.LayerColorOverride(p.Layer);
            if (ov.HasValue) p.Color = ov.Value;
        }
        for (int i = 0; i < Texts.Count; i++)
        {
            var ov = CadGeometry.LayerColorOverride(Texts[i].Layer, isText: true);
            if (ov.HasValue) Texts[i] = Texts[i] with { Color = ov.Value };
        }
        foreach (var w in Wires3D)
        {
            var ov = CadGeometry.LayerColorOverride(w.Layer);
            if (ov.HasValue) w.Color = ov.Value;
        }
    }

    // Replaces any ScenePolyline containing an out-of-range point with one or more
    // shorter, unclosed polylines covering just its contiguous in-range runs.
    // Polylines with no outlier point are left completely untouched.
    private void SplitPolylinesAtOutliers(Func<float, float, bool> inRange)
    {
        List<ScenePolyline>? replacement = null;
        for (int pi = Polylines.Count - 1; pi >= 0; pi--)
        {
            var p = Polylines[pi];
            if (IsDefaultHiddenLayer(p.Layer)) continue;
            if (p.Points.All(pt => inRange(pt.X, -pt.Y))) continue;

            replacement ??= new List<ScenePolyline>();
            List<SKPoint>? run = null;
            void FlushRun()
            {
                if (run != null && run.Count >= 2)
                {
                    // CurvePath deliberately not carried over: it describes the whole
                    // original polyline, not this surviving run.
                    var np = new ScenePolyline { Color = p.Color, Layer = p.Layer };
                    np.Points.AddRange(run);
                    replacement.Add(np);
                }
                run = null;
            }
            foreach (var pt in p.Points)
            {
                if (inRange(pt.X, -pt.Y)) (run ??= new List<SKPoint>()).Add(pt);
                else FlushRun();
            }
            FlushRun();
            Polylines.RemoveAt(pi);
        }
        if (replacement != null) Polylines.AddRange(replacement);
    }

    private void SplitWires3DAtOutliers(Func<Vector3, bool> inRange)
    {
        List<Scene3DWire>? replacement = null;
        for (int wi = Wires3D.Count - 1; wi >= 0; wi--)
        {
            var w = Wires3D[wi];
            if (w.Points.All(inRange)) continue;

            replacement ??= new List<Scene3DWire>();
            List<Vector3>? run = null;
            void FlushRun()
            {
                if (run != null && run.Count >= 2)
                {
                    var nw = new Scene3DWire { Color = w.Color, Layer = w.Layer };
                    nw.Points.AddRange(run);
                    replacement.Add(nw);
                }
                run = null;
            }
            foreach (var pt in w.Points)
            {
                if (inRange(pt)) (run ??= new List<Vector3>()).Add(pt);
                else FlushRun();
            }
            FlushRun();
            Wires3D.RemoveAt(wi);
        }
        if (replacement != null) Wires3D.AddRange(replacement);
    }

    // 1st/99th percentile of values, padded by 2x its own span -- robust to a small
    // fraction of outlier points regardless of how extreme they are.
    private static (float Lo, float Hi) RobustRange(List<float> values)
    {
        var sorted = new List<float>(values);
        sorted.Sort();
        float p01 = Percentile(sorted, 0.01f);
        float p99 = Percentile(sorted, 0.99f);
        float pad = MathF.Max((p99 - p01) * 2f, 1f);
        return (p01 - pad, p99 + pad);
    }

    private static float Percentile(List<float> sorted, float p)
    {
        if (sorted.Count == 1) return sorted[0];
        float idx = p * (sorted.Count - 1);
        int lo = (int)MathF.Floor(idx);
        int hi = (int)MathF.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    private static int LayerSortKey(string name)
    {
        if (name.StartsWith("BORDER",  StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith("ROUTE",   StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.StartsWith("2D_DIM",  StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    // CNC toolpath/routing annotation layers (cabinetry cutlist convention): real data,
    // visible by default same as any other layer (LayerInfo), but not part of the
    // part's own shape and can legitimately range far outside its footprint (shared
    // machine toolpath) -- excluded from driving the fit view / outlier cleanup above
    // regardless of visibility, so a wide-ranging toolpath layer can't zoom the view
    // out until the actual part is invisible.
    public static bool IsDefaultHiddenLayer(string name) => name.StartsWith("ROUTE", StringComparison.OrdinalIgnoreCase);
}

// Positions in DXF space (Y-up). Rendering negates Y for screen.
// Layer property is init-only with default ""; set via object initializer where needed.
public readonly record struct SceneCircle(float Cx, float Cy, float R, SKColor Color)
{
    public string Layer { get; init; } = "";
    public float[]? Dash { get; init; } = null;
}
public readonly record struct SceneArc(float Cx, float Cy, float R, float StartDeg, float EndDeg, SKColor Color)
{
    public string Layer { get; init; } = "";
    public float[]? Dash { get; init; } = null;
}
public readonly record struct SceneLine(float X1, float Y1, float X2, float Y2, SKColor Color)
{
    public string Layer { get; init; } = "";

    // Dash/gap lengths in drawing units (CAD semantics: a dash pattern scales with the
    // drawing, not the screen), or null for a continuous stroke.
    public float[]? Dash { get; init; } = null;

    // A "ROUTEDIM"-styled DIMENSION entity measures a CNC toolpath/routing distance,
    // not a part edge -- its coordinates live in the machine's routing space, which
    // can be tens of units from the part itself (seen directly: one measured 86 units
    // below a part whose own geometry spanned under 8 units). Unlike a layer-name
    // convention, this can't just be recolored/hidden wholesale -- the same dimstyle
    // is also used for ordinary edge dimensions that sit right on the part -- so it
    // only exempts the entity from the fit-view bounds calculation, same treatment as
    // IsDefaultHiddenLayer, without changing how or whether it renders.
    public bool BoundsExempt { get; init; } = false;
}
// Where (X,Y) sits relative to the rendered string. DXF stores justification separately
// from the insertion point, so the same coordinate means "left edge" or "center" or
// "right edge" depending on these -- drawing every string left-aligned offsets any
// centered or right-aligned label by roughly its own width.
public enum TextHAlign { Left, Center, Right }
public enum TextVAlign { Baseline, Bottom, Middle, Top }

public readonly record struct SceneText(float X, float Y, float Height, float Rotation, string Value, SKColor Color)
{
    public string Layer { get; init; } = "";
    public bool BoundsExempt { get; init; } = false;
    public TextHAlign HAlign { get; init; } = TextHAlign.Left;
    public TextVAlign VAlign { get; init; } = TextVAlign.Baseline;
}

public class ScenePolyline
{
    public readonly List<SKPoint> Points = new();
    public bool   Closed;
    public SKColor Color;
    public string  Layer = "";
    public float[]? Dash;

    // True-curve form of this polyline, when it contains bulge arcs. Points stays populated
    // (flattened) because bounds, outlier rejection and splitting all reason over vertices;
    // CurvePath only changes how it is *drawn*, so an arc stays smooth at any zoom instead
    // of showing the facets baked in at parse time. Splitting a polyline at an outlier
    // clears this -- the path no longer describes the surviving runs.
    public SKPath? CurvePath;
}

// A single 3D wireframe edge (world space, Y-up), e.g. one edge of a DWG Solid3D body.
public class Scene3DWire
{
    public readonly List<Vector3> Points = new();
    public SKColor Color;
    public string  Layer = "";
}

// One "page" of a document -- the Model page for a plain drawing, or (for DWG) one
// per Layout tab: "Model" plus a composited page per Paper Space sheet.
public class DxfPage
{
    public string Name { get; }
    public DxfScene Scene { get; }
    public DxfPage(string name, DxfScene scene) { Name = name; Scene = scene; }
}
