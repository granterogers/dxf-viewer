using System.IO;
using System.Linq;
using System.Numerics;
using SkiaSharp;

namespace DxfViewer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // --render-test <file> <out.png> [page] [azimuthDeg] [elevationDeg]
        // page defaults to "Model"; azimuth/elevation only matter for 3D scenes.
        if (args.Length >= 3 && args[0] == "--render-test")
        {
            string page = args.Length > 3 ? args[3] : "Model";
            float az = args.Length > 4 && float.TryParse(args[4], out var azv) ? azv : 45f;
            float el = args.Length > 5 && float.TryParse(args[5], out var elv) ? elv : 30f;
            return RunRenderTest(args[1], args[2], page, az, el);
        }

        // --make-fixture <out.dxf>
        if (args.Length >= 2 && args[0] == "--make-fixture")
            return TestFixture.Write(args[1]);

        string? initialFile = args.Length >= 1 && File.Exists(args[0]) ? args[0] : null;

        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        if (initialFile != null)
            window.Loaded += (_, _) => (window.DataContext as MainViewModel)?.TryOpenFile(initialFile);
        app.Run(window);
        return 0;
    }

    private static int RunRenderTest(string dxfPath, string outputPath, string pageName, float azimuthDeg, float elevationDeg)
    {
        if (!File.Exists(dxfPath))
        {
            Console.Error.WriteLine($"File not found: {dxfPath}");
            return 1;
        }

        try
        {
            var pages = dxfPath.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                ? DwgParser.Parse(dxfPath)
                : DxfParser.Parse(dxfPath);
            var page = pages.FirstOrDefault(p => string.Equals(p.Name, pageName, StringComparison.OrdinalIgnoreCase))
                ?? pages[0];
            var scene = page.Scene;

            const int W = 1200, H = 900;
            var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(26, 26, 26)); // #1A1A1A background

            if (scene.Is3D)
            {
                DrawScene3DForTest(canvas, scene, W, H, azimuthDeg * MathF.PI / 180f, elevationDeg * MathF.PI / 180f);
            }
            else
            {
                var bounds = scene.Bounds;
                float fitZoom = Math.Min(W / bounds.Width, H / bounds.Height) * 0.95f;
                float fitX = (W - bounds.Width  * fitZoom) / 2f - bounds.Left * fitZoom;
                float fitY = (H - bounds.Height * fitZoom) / 2f - bounds.Top  * fitZoom;

                canvas.Save();
                canvas.Translate(fitX, fitY);
                canvas.Scale(fitZoom);
                DrawSceneForTest(canvas, scene);
                canvas.Restore();
            }

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(fs);

            int circles   = scene.Circles.Count;
            int arcs      = scene.Arcs.Count;
            int lines     = scene.Lines.Count;
            int polylines = scene.Polylines.Count;
            int texts     = scene.Texts.Count;
            int wires3d   = scene.Wires3D.Count;
            var b = scene.Bounds;
            Console.WriteLine($"OK: {Path.GetFileName(dxfPath)} page='{page.Name}' -> {outputPath}  " +
                $"[C:{circles} A:{arcs} L:{lines} P:{polylines} T:{texts} W3D:{wires3d}]  " +
                $"bounds=[{b.Left:F1},{b.Top:F1},{b.Right:F1},{b.Bottom:F1}]" +
                (scene.Is3D ? $"  az={azimuthDeg}deg el={elevationDeg}deg" : "") +
                $"  pages=[{string.Join(",", pages.Select(p => p.Name))}]");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {Path.GetFileName(dxfPath)}: {ex.Message}");
            return 1;
        }
    }

    // Mirrors DxfTabControl.DrawScene3D/FitToWindow3D so the headless test harness
    // exercises the exact same camera-basis math as the interactive renderer.
    private static void DrawScene3DForTest(SKCanvas canvas, DxfScene scene, int w, int h, float azimuthRad, float elevationRad)
    {
        var min = scene.Bounds3DMin;
        var max = scene.Bounds3DMax;
        var center = (min + max) / 2f;
        float radius = (max - min).Length() / 2f;
        if (radius < 1e-4f) radius = 1f;
        float zoom = Math.Min(w, h) / (radius * 2.4f);

        var (right, up) = CadGeometry.OrbitCameraBasis(azimuthRad, elevationRad);
        var eyeDir = Vector3.Cross(right, up);
        float cx = w / 2f, cy = h / 2f;

        SKPoint Project(Vector3 p)
        {
            var rel = p - center;
            return new SKPoint(cx + Vector3.Dot(rel, right) * zoom, cy - Vector3.Dot(rel, up) * zoom);
        }

        // Hairline + depth-cue fade (see DxfTabControl.DrawScene3D) so the test harness matches the real renderer.
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0f };
        var visibleWires = new List<Scene3DWire>();
        float minDepth = float.MaxValue, maxDepth = float.MinValue;
        foreach (var wire in scene.Wires3D)
        {
            if (wire.Points.Count < 2) continue;
            if (!LayerInfo.DefaultVisible(wire.Layer)) continue;
            visibleWires.Add(wire);
            foreach (var p in wire.Points)
            {
                float depth = Vector3.Dot(p - center, eyeDir);
                if (depth < minDepth) minDepth = depth;
                if (depth > maxDepth) maxDepth = depth;
            }
        }
        float depthRange = maxDepth - minDepth;
        var bg = new SKColor(18, 18, 30);
        foreach (var wire in visibleWires)
        {
            for (int i = 1; i < wire.Points.Count; i++)
            {
                var a = wire.Points[i - 1];
                var b = wire.Points[i];
                float t = 1f;
                if (depthRange > 1e-6f)
                {
                    float depthA = Vector3.Dot(a - center, eyeDir);
                    float depthB = Vector3.Dot(b - center, eyeDir);
                    t = ((depthA + depthB) / 2f - minDepth) / depthRange;
                }
                float blend = 0.35f + 0.65f * t;
                paint.Color = new SKColor(
                    (byte)(bg.Red   + (wire.Color.Red   - bg.Red)   * blend),
                    (byte)(bg.Green + (wire.Color.Green - bg.Green) * blend),
                    (byte)(bg.Blue  + (wire.Color.Blue  - bg.Blue)  * blend));
                canvas.DrawLine(Project(a), Project(b), paint);
            }
        }
    }


    // Dash patterns are in drawing units, matching CAD semantics (a dashed line's dashes
    // scale with the drawing, not the screen). The effect must be recreated per entity and
    // disposed, so it is cached by pattern for the duration of one scene draw.
    private static SKPathEffect? DashEffect(float[]? dash, Dictionary<float[], SKPathEffect> cache)
    {
        if (dash == null || dash.Length < 2) return null;
        if (cache.TryGetValue(dash, out var fx)) return fx;
        fx = SKPathEffect.CreateDash(dash, 0f);
        cache[dash] = fx;
        return fx;
    }
    private static void DrawSceneForTest(SKCanvas canvas, DxfScene scene)
    {
        // Hairline (see DxfTabControl.DrawScene) so the test harness matches the real renderer.
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0f };
        var dashCache = new Dictionary<float[], SKPathEffect>();

        foreach (var c in scene.Circles)
        {
            if (!LayerInfo.DefaultVisible(c.Layer)) continue;
            paint.Color = c.Color;
            paint.PathEffect = DashEffect(c.Dash, dashCache);
            canvas.DrawCircle(c.Cx, -c.Cy, c.R, paint);
        }

        foreach (var a in scene.Arcs)
        {
            if (!LayerInfo.DefaultVisible(a.Layer)) continue;
            paint.Color = a.Color;
            paint.PathEffect = DashEffect(a.Dash, dashCache);
            var oval = new SKRect(a.Cx - a.R, -a.Cy - a.R, a.Cx + a.R, -a.Cy + a.R);
            float span = a.EndDeg > a.StartDeg
                ? a.EndDeg - a.StartDeg
                : 360f - a.StartDeg + a.EndDeg;
            canvas.DrawArc(oval, -a.StartDeg, -span, false, paint);
        }

        foreach (var l in scene.Lines)
        {
            if (!LayerInfo.DefaultVisible(l.Layer)) continue;
            paint.Color = l.Color;
            paint.PathEffect = DashEffect(l.Dash, dashCache);
            canvas.DrawLine(l.X1, -l.Y1, l.X2, -l.Y2, paint);
        }

        foreach (var p in scene.Polylines)
        {
            if (p.Points.Count < 2) continue;
            if (!LayerInfo.DefaultVisible(p.Layer)) continue;
            paint.Color = p.Color;
            paint.PathEffect = DashEffect(p.Dash, dashCache);
            if (p.CurvePath != null) { canvas.DrawPath(p.CurvePath, paint); continue; }
            using var path = new SKPath();
            path.MoveTo(p.Points[0].X, -p.Points[0].Y);
            for (int i = 1; i < p.Points.Count; i++)
                path.LineTo(p.Points[i].X, -p.Points[i].Y);
            if (p.Closed) path.Close();
            canvas.DrawPath(path, paint);
        }

        paint.PathEffect = null;
        foreach (var fx in dashCache.Values) fx.Dispose();

        if (scene.Texts.Count > 0)
        {
            var placed = new List<(float X, float Y, float Footprint)>();
            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Typeface = DxfTabControl.TextTypeface,
            };
            foreach (var t in scene.Texts)
            {
                if (string.IsNullOrEmpty(t.Value)) continue;
                if (!LayerInfo.DefaultVisible(t.Layer)) continue;
                float sz = t.Height > 0f ? t.Height : 2.5f;
                var placement = CadGeometry.PlaceText(placed, t.X, t.Y, sz, t.Rotation, t.Value);
                placed.Add(placement);
                float ax = placement.X, ay = placement.Y;
                textPaint.Color = t.Color;
                textPaint.TextSize = sz;
                var (dx, dy) = CadGeometry.AlignOffset(
                    t.HAlign, t.VAlign, textPaint.MeasureText(t.Value), sz);
                canvas.Save();
                canvas.Translate(ax, -ay);
                if (MathF.Abs(t.Rotation) > 0.01f)
                    canvas.RotateDegrees(-t.Rotation);
                canvas.DrawText(t.Value, dx, dy, textPaint);
                canvas.Restore();
            }
        }
    }
}
