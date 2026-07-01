using SkiaSharp;

namespace DxfViewer;

public class DxfScene
{
    public readonly List<SceneCircle>   Circles   = new();
    public readonly List<SceneArc>      Arcs      = new();
    public readonly List<SceneLine>     Lines     = new();
    public readonly List<ScenePolyline> Polylines = new();
    public readonly List<SceneText>     Texts     = new();

    public bool IsEmpty => Circles.Count == 0 && Arcs.Count == 0 &&
                           Lines.Count == 0 && Polylines.Count == 0 && Texts.Count == 0;

    // Bounding box in screen space (DXF Y negated), set by ComputeBounds().
    public SKRect Bounds { get; private set; } = new SKRect(-10, -10, 10, 10);

    // Unique layers sorted: BORDER* first, ROUTE* second, 2D_DIM* third, rest alphabetically.
    public List<(string Name, SKColor Color)> Layers { get; private set; } = new();

    public void ComputeBounds()
    {
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

        foreach (var c in Circles)
        {
            Expand(c.Cx - c.R, -c.Cy - c.R);
            Expand(c.Cx + c.R, -c.Cy + c.R);
        }
        foreach (var a in Arcs)
        {
            Expand(a.Cx - a.R, -a.Cy - a.R);
            Expand(a.Cx + a.R, -a.Cy + a.R);
        }
        foreach (var l in Lines)
        {
            Expand(l.X1, -l.Y1);
            Expand(l.X2, -l.Y2);
        }
        foreach (var p in Polylines)
            foreach (var pt in p.Points)
                Expand(pt.X, -pt.Y);
        foreach (var t in Texts)
        {
            Expand(t.X, -t.Y);
            // Include approximate top of text (extends above baseline in screen Y = below in DXF Y)
            if (t.Height > 0f) Expand(t.X, -t.Y - t.Height);
        }

        if (any && maxX - minX > 1e-4f && maxY - minY > 1e-4f)
            Bounds = new SKRect(minX, minY, maxX, maxY);
        else if (any)
            Bounds = new SKRect(minX - 10, minY - 10, maxX + 10, maxY + 10);

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

        Layers = seen
            .OrderBy(kv => LayerSortKey(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private static int LayerSortKey(string name)
    {
        if (name.StartsWith("BORDER",  StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith("ROUTE",   StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.StartsWith("2D_DIM",  StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }
}

// Positions in DXF space (Y-up). Rendering negates Y for screen.
// Layer property is init-only with default ""; set via object initializer where needed.
public readonly record struct SceneCircle(float Cx, float Cy, float R, SKColor Color)
{
    public string Layer { get; init; } = "";
}
public readonly record struct SceneArc(float Cx, float Cy, float R, float StartDeg, float EndDeg, SKColor Color)
{
    public string Layer { get; init; } = "";
}
public readonly record struct SceneLine(float X1, float Y1, float X2, float Y2, SKColor Color)
{
    public string Layer { get; init; } = "";
}
public readonly record struct SceneText(float X, float Y, float Height, float Rotation, string Value, SKColor Color)
{
    public string Layer { get; init; } = "";
}

public class ScenePolyline
{
    public readonly List<SKPoint> Points = new();
    public bool   Closed;
    public SKColor Color;
    public string  Layer = "";
}
