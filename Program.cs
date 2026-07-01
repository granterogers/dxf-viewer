using System.IO;
using SkiaSharp;

namespace DxfViewer;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--render-test")
            return RunRenderTest(args[1], args[2]);

        var app = new App();
        app.InitializeComponent();
        app.Run(new MainWindow());
        return 0;
    }

    private static int RunRenderTest(string dxfPath, string outputPath)
    {
        if (!File.Exists(dxfPath))
        {
            Console.Error.WriteLine($"File not found: {dxfPath}");
            return 1;
        }

        try
        {
            var scene = DxfParser.Parse(dxfPath);
            var bounds = scene.Bounds;

            const int W = 1200, H = 900;
            float fitZoom = Math.Min(W / bounds.Width, H / bounds.Height) * 0.95f;
            float fitX = (W - bounds.Width  * fitZoom) / 2f - bounds.Left * fitZoom;
            float fitY = (H - bounds.Height * fitZoom) / 2f - bounds.Top  * fitZoom;

            var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;
            canvas.Clear(new SKColor(26, 26, 26)); // #1A1A1A background

            canvas.Save();
            canvas.Translate(fitX, fitY);
            canvas.Scale(fitZoom);
            DrawSceneForTest(canvas, scene);
            canvas.Restore();

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            data.SaveTo(fs);

            int circles   = scene.Circles.Count;
            int arcs      = scene.Arcs.Count;
            int lines     = scene.Lines.Count;
            int polylines = scene.Polylines.Count;
            int texts     = scene.Texts.Count;
            Console.WriteLine($"OK: {Path.GetFileName(dxfPath)} → {outputPath}  " +
                $"[C:{circles} A:{arcs} L:{lines} P:{polylines} T:{texts}]");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {Path.GetFileName(dxfPath)}: {ex.Message}");
            return 1;
        }
    }

    private static void DrawSceneForTest(SKCanvas canvas, DxfScene scene)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.5f };

        foreach (var c in scene.Circles)
        {
            paint.Color = c.Color;
            canvas.DrawCircle(c.Cx, -c.Cy, c.R, paint);
        }

        foreach (var a in scene.Arcs)
        {
            paint.Color = a.Color;
            var oval = new SKRect(a.Cx - a.R, -a.Cy - a.R, a.Cx + a.R, -a.Cy + a.R);
            float span = a.EndDeg > a.StartDeg
                ? a.EndDeg - a.StartDeg
                : 360f - a.StartDeg + a.EndDeg;
            canvas.DrawArc(oval, -a.StartDeg, -span, false, paint);
        }

        foreach (var l in scene.Lines)
        {
            paint.Color = l.Color;
            canvas.DrawLine(l.X1, -l.Y1, l.X2, -l.Y2, paint);
        }

        foreach (var p in scene.Polylines)
        {
            if (p.Points.Count < 2) continue;
            paint.Color = p.Color;
            using var path = new SKPath();
            path.MoveTo(p.Points[0].X, -p.Points[0].Y);
            for (int i = 1; i < p.Points.Count; i++)
                path.LineTo(p.Points[i].X, -p.Points[i].Y);
            if (p.Closed) path.Close();
            canvas.DrawPath(path, paint);
        }

        if (scene.Texts.Count > 0)
        {
            var tf = SKTypeface.FromFamilyName("Segoe UI");
            foreach (var t in scene.Texts)
            {
                if (string.IsNullOrEmpty(t.Value)) continue;
                float sz = t.Height > 0f ? t.Height : 2.5f;
                using var textPaint = new SKPaint
                {
                    IsAntialias = true,
                    Color = t.Color,
                    TextSize = sz,
                    Typeface = tf,
                };
                canvas.Save();
                canvas.Translate(t.X, -t.Y);
                if (Math.Abs(t.Rotation) > 0.01f)
                    canvas.RotateDegrees(-(float)t.Rotation);
                canvas.DrawText(t.Value, 0, 0, textPaint);
                canvas.Restore();
            }
            tf?.Dispose();
        }
    }
}
