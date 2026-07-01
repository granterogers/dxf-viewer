using SkiaSharp;

namespace DxfViewer;

public class DxfScene
{
    public readonly List<SceneCircle> Circles = new();
    public readonly List<SceneArc> Arcs = new();
    public readonly List<SceneLine> Lines = new();
    public readonly List<ScenePolyline> Polylines = new();
    public readonly List<SceneText> Texts = new();

    public bool IsEmpty => Circles.Count == 0 && Arcs.Count == 0 &&
                           Lines.Count == 0 && Polylines.Count == 0 && Texts.Count == 0;

    // Bounding box in screen space (DXF Y negated), set by ComputeBounds().
    public SKRect Bounds { get; private set; } = new SKRect(-10, -10, 10, 10);

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
            // Use full bounding box of each circle (Y-flipped center)
            Expand(c.Cx - c.R, -c.Cy - c.R);
            Expand(c.Cx + c.R, -c.Cy + c.R);
        }
        foreach (var a in Arcs)
        {
            // Conservative: full bounding box of arc's circle
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
            Expand(t.X, -t.Y);

        if (any && maxX - minX > 1e-4f && maxY - minY > 1e-4f)
            Bounds = new SKRect(minX, minY, maxX, maxY);
        else if (any)
            Bounds = new SKRect(minX - 10, minY - 10, maxX + 10, maxY + 10);
        // else leave default fallback bounds
    }
}

// All positions in DXF space (Y-up). Rendering negates Y for screen.
public readonly record struct SceneCircle(float Cx, float Cy, float R, SKColor Color);
public readonly record struct SceneArc(float Cx, float Cy, float R, float StartDeg, float EndDeg, SKColor Color);
public readonly record struct SceneLine(float X1, float Y1, float X2, float Y2, SKColor Color);
public readonly record struct SceneText(float X, float Y, float Height, float Rotation, string Value, SKColor Color);

public class ScenePolyline
{
    public readonly List<SKPoint> Points = new(); // X = DXF X, Y = DXF Y
    public bool Closed;
    public SKColor Color;
}
