using SkiaSharp;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DxfViewer;

public partial class DxfTabControl : UserControl
{
    private DxfTabViewModel? _vm;
    private bool _panning;
    private Point _lastMouse;

    private float _zoom = 1f, _panX, _panY;
    private float _fitOffsetX, _fitOffsetY, _fitZoom = 1f;

    // Orbit camera state for 3D scenes (Solid3D wireframes). Kept entirely
    // separate from the 2D pan/zoom fields above so the two modes never fight
    // over the same state.
    private bool _orbiting;
    private float _camAzimuth = MathF.PI / 4f;    // 45 deg
    private float _camElevation = MathF.PI / 6f;  // 30 deg
    private float _camZoom = 1f;

    // Zoom eases toward a target rather than snapping, so a wheel flick reads as motion
    // instead of a jump. The 2D and 3D cameras each keep their own target.
    private float _zoomTarget = 1f, _camZoomTarget = 1f;
    private System.Windows.Threading.DispatcherTimer? _zoomTimer;
    private float _camPanX, _camPanY;
    private Vector3 _camCenter;

    private WriteableBitmap? _wbm;
    private bool _layerPanelExpanded = true;

    // The backing bitmap is allocated in real device pixels, which on a scaled display is
    // larger than the control's DIP size. All view math (pan, zoom, fit, hit positions)
    // stays in DIPs and the canvas is scaled by this once per frame, so drawing code never
    // has to care -- and hairline strokes land on real pixels instead of being upscaled.
    private double _dpiX = 1.0, _dpiY = 1.0;

    // Recorded 2D scene, replayed under the pan/zoom matrix. Rebuilt only when the scene
    // or the visible-layer set actually changes -- previously every mouse-move re-walked
    // every entity in the drawing.
    private SKPicture? _picture;
    private string? _pictureKey;

    // Inspect / measure overlay state. Drawn on top of the cached scene picture so making
    // a selection never invalidates the cache.
    private SKRect? _selectionBox;
    private SKPoint? _measureA, _measureB;
    private Point _pressOrigin;

    // Live cursor position in DIPs, used for the measure crosshair and rubber-band line.
    // Null when the pointer is outside the canvas, so the crosshair disappears with it.
    private Point? _cursorDip;

    public DxfTabControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= VmPropertyChanged;
            _vm.RenderAction = null;
        }
        _vm = e.NewValue as DxfTabViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += VmPropertyChanged;
            _vm.FitAction    = FitToWindow;
            _vm.RenderAction = () => Dispatcher.Invoke(Render);
            if (_vm.IsLoaded) FitToWindow();
        }
    }

    private void VmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DxfTabViewModel.MeasureMode))
        {
            if (_vm?.MeasureMode != true)
            {
                SkiaHost.Cursor = Cursors.Arrow;
                _measureA = _measureB = null;
                _cursorDip = null;
            }
            Dispatcher.Invoke(Render);
            return;
        }
        if (e.PropertyName != nameof(DxfTabViewModel.State)) return;
        InvalidateScenePicture();
        if (_vm?.IsLoaded == true)
            Dispatcher.Invoke(FitToWindow);
        else
        {
            _zoom = 1f; _zoomTarget = 1f; _panX = _panY = 0f;
            _camPanX = _camPanY = 0f;
            Dispatcher.Invoke(Render);
        }
    }

    private void SkiaHost_SizeChanged(object sender, SizeChangedEventArgs e) => AllocateBitmap();

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        AllocateBitmap();
    }

    private void AllocateBitmap()
    {
        if (SkiaHost.ActualWidth <= 0 || SkiaHost.ActualHeight <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        _dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        int w = Math.Max(1, (int)Math.Round(SkiaHost.ActualWidth  * _dpiX));
        int h = Math.Max(1, (int)Math.Round(SkiaHost.ActualHeight * _dpiY));

        _wbm = new WriteableBitmap(w, h, 96 * _dpiX, 96 * _dpiY, PixelFormats.Pbgra32, null);
        SkiaImage.Source = _wbm;

        if (_vm?.IsLoaded == true)
            FitToWindow();
        else
            Render();
    }

    public void FitToWindow()
    {
        if (_vm?.Scene == null || _wbm == null) return;
        if (_vm.Scene.Is3D) { FitToWindow3D(); return; }

        var bounds = _vm.Scene.Bounds;
        var vw = (float)(_wbm.PixelWidth  / _dpiX);
        var vh = (float)(_wbm.PixelHeight / _dpiY);
        if (vw <= 0 || vh <= 0 || bounds.Width < 1e-4f || bounds.Height < 1e-4f) return;

        _fitZoom    = Math.Min(vw / bounds.Width, vh / bounds.Height) * 0.95f;
        _fitOffsetX = (vw - bounds.Width  * _fitZoom) / 2f - bounds.Left * _fitZoom;
        _fitOffsetY = (vh - bounds.Height * _fitZoom) / 2f - bounds.Top  * _fitZoom;
        _zoom = 1f;
        _zoomTarget = 1f;
        _panX = _panY = 0f;
        Render();
    }

    private void FitToWindow3D()
    {
        var scene = _vm!.Scene!;
        var vw = (float)(_wbm!.PixelWidth  / _dpiX);
        var vh = (float)(_wbm.PixelHeight / _dpiY);
        if (vw <= 0 || vh <= 0) return;

        var min = scene.Bounds3DMin;
        var max = scene.Bounds3DMax;
        _camCenter = (min + max) / 2f;
        float radius = (max - min).Length() / 2f;
        if (radius < 1e-4f) radius = 1f;

        _camAzimuth   = MathF.PI / 4f;
        _camElevation = MathF.PI / 6f;
        _camPanX = _camPanY = 0f;
        _camZoom = Math.Min(vw, vh) / (radius * 2.4f); // 2.4 leaves a margin around the fit sphere
        _camZoomTarget = _camZoom;
        Render();
    }

    private void Render()
    {
        var wbm = _wbm;
        if (wbm == null) return;

        wbm.Lock();
        try
        {
            var info = new SKImageInfo(wbm.PixelWidth, wbm.PixelHeight,
                SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, wbm.BackBuffer, wbm.BackBufferStride);
            var canvas = surface.Canvas;
            canvas.Clear(Theme.CanvasBackground);
            canvas.Scale((float)_dpiX, (float)_dpiY);   // everything below works in DIPs

            if (_vm?.Scene != null && _vm.IsLoaded)
            {
                // Build visible-layer set (null = show all when no layer info)
                HashSet<string>? visibleLayers = null;
                if (_vm.Layers.Count > 0)
                    visibleLayers = _vm.Layers
                        .Where(l => l.IsVisible)
                        .Select(l => l.Name)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (_vm.Scene.Is3D)
                {
                    DrawScene3D(canvas, _vm.Scene, visibleLayers,
                        (int)(wbm.PixelWidth / _dpiX), (int)(wbm.PixelHeight / _dpiY));
                }
                else
                {
                    canvas.Save();
                    canvas.Translate(_panX, _panY);
                    canvas.Scale(_zoom);
                    canvas.Translate(_fitOffsetX, _fitOffsetY);
                    canvas.Scale(_fitZoom);          // apply fit scale so DXF units map to screen pixels
                    canvas.DrawPicture(GetScenePicture(_vm.Scene, visibleLayers));
                    DrawOverlay(canvas);
                    canvas.Restore();
                    DrawMeasureCursor(canvas,
                        (float)(wbm.PixelWidth / _dpiX), (float)(wbm.PixelHeight / _dpiY));
                }
            }
        }
        finally
        {
            wbm.AddDirtyRect(new Int32Rect(0, 0, wbm.PixelWidth, wbm.PixelHeight));
            wbm.Unlock();
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
    // Cache key is the visible-layer set plus the scene's identity: a layer toggle or a
    // page/file switch invalidates, a pan or zoom does not.
    private SKPicture GetScenePicture(DxfScene scene, HashSet<string>? vis)
    {
        string key = scene.GetHashCode().ToString() + "|" +
                     (vis == null ? "*" : string.Join(",", vis.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        if (_picture != null && _pictureKey == key) return _picture;

        _picture?.Dispose();
        var b = scene.Bounds;
        using var recorder = new SKPictureRecorder();
        var cull = new SKRect(b.Left - b.Width, b.Top - b.Height, b.Right + b.Width, b.Bottom + b.Height);
        DrawScene(recorder.BeginRecording(cull), scene, vis);
        _picture = recorder.EndRecording();
        _pictureKey = key;
        return _picture;
    }

    // Renders the current page at a fixed size independent of the window, so an export is
    // a usable reference image rather than a screenshot of whatever the window happened
    // to be. Reuses the same scene picture the on-screen view draws.
    public void ExportPng(string path, int width = 2400, int height = 1800)
    {
        var scene = _vm?.Scene;
        if (scene == null) return;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(Theme.CanvasBackground);

        HashSet<string>? vis = null;
        if (_vm!.Layers.Count > 0)
            vis = _vm.Layers.Where(l => l.IsVisible).Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (scene.Is3D)
        {
            DrawScene3D(canvas, scene, vis, width, height);
        }
        else
        {
            var b = scene.Bounds;
            if (b.Width < 1e-4f || b.Height < 1e-4f) return;
            float fit = Math.Min(width / b.Width, height / b.Height) * 0.95f;
            canvas.Translate((width - b.Width * fit) / 2f - b.Left * fit,
                             (height - b.Height * fit) / 2f - b.Top * fit);
            canvas.Scale(fit);
            canvas.DrawPicture(GetScenePicture(scene, vis));
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        data.SaveTo(fs);
    }

    // Nearest entity to a scene-space point, within a tolerance expressed in screen pixels
    // so the grab radius feels the same at every zoom level.
    private bool TryPickEntity(float sx, float sy, out string description, out SKRect box)
    {
        description = ""; box = default;
        var scene = _vm?.Scene;
        if (scene == null) return false;

        float scale = _zoom * _fitZoom;
        float tol = scale > 1e-6f ? 8f / scale : 8f;
        float best = float.MaxValue;
        string bestDesc = "";
        SKRect bestBox = default;

        // Locals rather than the out parameters directly: a local function cannot capture
        // an out parameter.
        void Consider(float d, string desc, SKRect b)
        {
            if (d >= best || d > tol) return;
            best = d; bestDesc = desc; bestBox = b;
        }

        foreach (var l in scene.Lines)
        {
            float len = MathF.Sqrt((l.X2 - l.X1) * (l.X2 - l.X1) + (l.Y2 - l.Y1) * (l.Y2 - l.Y1));
            Consider(DistanceToSegment(sx, sy, l.X1, l.Y1, l.X2, l.Y2),
                $"LINE   layer {l.Layer}   ({l.X1:F3}, {l.Y1:F3}) to ({l.X2:F3}, {l.Y2:F3})   length {len:F3}",
                new SKRect(MathF.Min(l.X1, l.X2), -MathF.Max(l.Y1, l.Y2),
                           MathF.Max(l.X1, l.X2), -MathF.Min(l.Y1, l.Y2)));
        }
        foreach (var c in scene.Circles)
        {
            Consider(MathF.Abs(MathF.Sqrt((sx - c.Cx) * (sx - c.Cx) + (sy - c.Cy) * (sy - c.Cy)) - c.R),
                $"CIRCLE   layer {c.Layer}   centre ({c.Cx:F3}, {c.Cy:F3})   radius {c.R:F3}   dia {c.R * 2:F3}",
                new SKRect(c.Cx - c.R, -c.Cy - c.R, c.Cx + c.R, -c.Cy + c.R));
        }
        foreach (var a in scene.Arcs)
        {
            Consider(MathF.Abs(MathF.Sqrt((sx - a.Cx) * (sx - a.Cx) + (sy - a.Cy) * (sy - a.Cy)) - a.R),
                $"ARC   layer {a.Layer}   centre ({a.Cx:F3}, {a.Cy:F3})   radius {a.R:F3}   {a.StartDeg:F1} to {a.EndDeg:F1} deg",
                new SKRect(a.Cx - a.R, -a.Cy - a.R, a.Cx + a.R, -a.Cy + a.R));
        }
        foreach (var p in scene.Polylines)
        {
            for (int i = 1; i < p.Points.Count; i++)
            {
                float d = DistanceToSegment(sx, sy, p.Points[i - 1].X, p.Points[i - 1].Y, p.Points[i].X, p.Points[i].Y);
                if (d >= best || d > tol) continue;
                float mnx = float.MaxValue, mny = float.MaxValue, mxx = float.MinValue, mxy = float.MinValue;
                foreach (var pt in p.Points)
                {
                    mnx = MathF.Min(mnx, pt.X); mxx = MathF.Max(mxx, pt.X);
                    mny = MathF.Min(mny, -pt.Y); mxy = MathF.Max(mxy, -pt.Y);
                }
                string closed = p.Closed ? "   closed" : "";
                Consider(d, $"POLYLINE   layer {p.Layer}   {p.Points.Count} vertices{closed}",
                    new SKRect(mnx, mny, mxx, mxy));
            }
        }
        foreach (var t in scene.Texts)
        {
            float w = t.Value.Length * t.Height * 0.6f;
            if (sx < t.X - w || sx > t.X + w || sy < t.Y - t.Height || sy > t.Y + t.Height) continue;
            Consider(0f, $"TEXT   layer {t.Layer}   {t.Value}   height {t.Height:F3}",
                new SKRect(t.X, -t.Y - t.Height, t.X + w, -t.Y));
        }

        description = bestDesc;
        box = bestBox;
        return !string.IsNullOrEmpty(description);
    }

    private static float DistanceToSegment(float px, float py, float x1, float y1, float x2, float y2)
    {
        float dx = x2 - x1, dy = y2 - y1;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-12f) return MathF.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
        float t = Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lenSq, 0f, 1f);
        float cx = x1 + t * dx, cy = y1 + t * dy;
        return MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    // Snaps to the nearest endpoint or circle/arc centre, so a measurement lands on real
    // geometry rather than wherever the cursor happened to be.
    private SKPoint SnapPoint(float sx, float sy)
    {
        var scene = _vm?.Scene;
        if (scene == null) return new SKPoint(sx, sy);
        float scale = _zoom * _fitZoom;
        float best = scale > 1e-6f ? 12f / scale : 12f;
        var result = new SKPoint(sx, sy);

        void Try(float x, float y)
        {
            float d = MathF.Sqrt((sx - x) * (sx - x) + (sy - y) * (sy - y));
            if (d < best) { best = d; result = new SKPoint(x, y); }
        }
        foreach (var l in scene.Lines) { Try(l.X1, l.Y1); Try(l.X2, l.Y2); }
        foreach (var c in scene.Circles) Try(c.Cx, c.Cy);
        foreach (var a in scene.Arcs) Try(a.Cx, a.Cy);
        foreach (var p in scene.Polylines) foreach (var pt in p.Points) Try(pt.X, pt.Y);
        return result;
    }

    private void DrawOverlay(SKCanvas canvas)
    {
        if (_selectionBox == null && _measureA == null) return;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            Color = new SKColor(0xE0, 0x78, 0x20),
        };

        if (_selectionBox is { } box)
        {
            var outline = box;
            outline.Inflate(box.Width * 0.04f + 0.5f, box.Height * 0.04f + 0.5f);
            canvas.DrawRect(outline, paint);
        }

        if (_measureA is { } a)
        {
            float r = 3f / MathF.Max(1e-6f, _zoom * _fitZoom);
            canvas.DrawCircle(a.X, -a.Y, r, paint);
            if (_measureB is { } b)
            {
                canvas.DrawCircle(b.X, -b.Y, r, paint);
                canvas.DrawLine(a.X, -a.Y, b.X, -b.Y, paint);
            }
        }
    }

    // A left press that never became a drag is a pick, not a pan.
    private void HandleClick(Point p)
    {
        if (_vm == null || _vm.Scene?.Is3D == true) return;
        if (!TryScenePoint(p, out float x, out float y)) return;

        if (_vm.MeasureMode)
        {
            var snapped = SnapPoint(x, y);
            if (_measureA == null || _measureB != null)
            {
                _measureA = snapped; _measureB = null;
                _vm.SelectionReadout = "measure: pick the second point";
            }
            else
            {
                _measureB = snapped;
                float dx = _measureB.Value.X - _measureA.Value.X;
                float dy = _measureB.Value.Y - _measureA.Value.Y;
                _vm.SelectionReadout =
                    $"distance {MathF.Sqrt(dx * dx + dy * dy):F4}    dX {dx:F4}    dY {dy:F4}";
            }
            Render();
            return;
        }

        if (TryPickEntity(x, y, out string desc, out SKRect box))
        {
            _selectionBox = box;
            _vm.SelectionReadout = desc;
        }
        else
        {
            _selectionBox = null;
            _vm.SelectionReadout = "";
        }
        Render();
    }

    // Drawn after the scene transform is popped, so the crosshair spans the whole viewport
    // in screen space regardless of zoom -- the CAD convention, and far easier to aim with
    // than a small pointer glyph.
    private void DrawMeasureCursor(SKCanvas canvas, float vw, float vh)
    {
        if (_vm?.MeasureMode != true || _vm.Scene?.Is3D == true) return;

        using var hair = new SKPaint
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0f,
            Color = new SKColor(0xF9, 0x54, 0x11, 0xB0),
        };

        // Rubber band from the anchored first point to wherever the cursor is now, so the
        // measurement is visible while it is being made rather than only after.
        if (_measureA is { } a && _measureB == null && _cursorDip is { } c)
        {
            var sa = ToScreen(a.X, a.Y);
            using var band = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.4f,
                Color = new SKColor(0xF9, 0x54, 0x11),
                PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0f),
            };
            canvas.DrawLine(sa, new SKPoint((float)c.X, (float)c.Y), band);
            band.PathEffect?.Dispose();
        }

        if (_cursorDip is { } p)
        {
            float x = (float)p.X, y = (float)p.Y;
            canvas.DrawLine(0, y, vw, y, hair);
            canvas.DrawLine(x, 0, x, vh, hair);

            using var hub = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.2f,
                Color = new SKColor(0xF9, 0x54, 0x11),
            };
            canvas.DrawCircle(x, y, 7f, hub);
        }
    }

    private void InvalidateScenePicture()
    {
        _picture?.Dispose();
        _picture = null;
        _pictureKey = null;
    }

    private static void DrawScene(SKCanvas canvas, DxfScene scene, HashSet<string>? vis)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            // Hairline: Skia always draws this as exactly 1 device pixel regardless of
            // the canvas scale, so lines stay thin and crisp whether the fit view is
            // zoomed into a 20-unit part or zoomed out on a 10,000-unit floor plan.
            // A fixed width in *drawing* units (e.g. 0.5) blows up to dozens of pixels
            // once the fit-to-window scale for a small part is applied.
            StrokeWidth = 0f,
        };
        var dashCache = new Dictionary<float[], SKPathEffect>();

        foreach (var c in scene.Circles)
        {
            if (vis != null && !vis.Contains(c.Layer)) continue;
            paint.Color = Theme.ForBackground(c.Color);
            paint.PathEffect = DashEffect(c.Dash, dashCache);
            canvas.DrawCircle(c.Cx, -c.Cy, c.R, paint);
        }

        foreach (var a in scene.Arcs)
        {
            if (vis != null && !vis.Contains(a.Layer)) continue;
            paint.Color = Theme.ForBackground(a.Color);
            paint.PathEffect = DashEffect(a.Dash, dashCache);
            var oval = new SKRect(a.Cx - a.R, -a.Cy - a.R, a.Cx + a.R, -a.Cy + a.R);
            float span = a.EndDeg > a.StartDeg
                ? a.EndDeg - a.StartDeg
                : 360f - a.StartDeg + a.EndDeg;
            canvas.DrawArc(oval, -a.StartDeg, -span, false, paint);
        }

        foreach (var l in scene.Lines)
        {
            if (vis != null && !vis.Contains(l.Layer)) continue;
            paint.Color = Theme.ForBackground(l.Color);
            paint.PathEffect = DashEffect(l.Dash, dashCache);
            canvas.DrawLine(l.X1, -l.Y1, l.X2, -l.Y2, paint);
        }

        foreach (var p in scene.Polylines)
        {
            if (p.Points.Count < 2) continue;
            if (vis != null && !vis.Contains(p.Layer)) continue;
            paint.Color = Theme.ForBackground(p.Color);
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

        DrawTexts(canvas, scene, vis);
    }

    // Orthographic orbit-camera projection for Solid3D wireframes. No hidden-line
    // removal or perspective -- a fixed-angle wireframe read, just now rotatable.
    private void DrawScene3D(SKCanvas canvas, DxfScene scene, HashSet<string>? vis, int viewportWidth, int viewportHeight)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            // Hairline: Skia always draws this as exactly 1 device pixel regardless of
            // the canvas scale, so lines stay thin and crisp whether the fit view is
            // zoomed into a 20-unit part or zoomed out on a 10,000-unit floor plan.
            // A fixed width in *drawing* units (e.g. 0.5) blows up to dozens of pixels
            // once the fit-to-window scale for a small part is applied.
            StrokeWidth = 0f,
        };

        var (right, up) = CadGeometry.OrbitCameraBasis(_camAzimuth, _camElevation);
        // (right, up, eyeDir) is the right-handed basis OrbitCameraBasis builds
        // internally (up = Cross(eyeDir, right)), so this recovers eyeDir -- the
        // direction from the orbit center toward the camera -- without needing
        // CadGeometry to expose it separately.
        var eyeDir = Vector3.Cross(right, up);

        float cx = viewportWidth / 2f + _camPanX;
        float cy = viewportHeight / 2f + _camPanY;

        SKPoint Project(Vector3 p)
        {
            var rel = p - _camCenter;
            float sx = Vector3.Dot(rel, right) * _camZoom;
            float sy = Vector3.Dot(rel, up) * _camZoom;
            return new SKPoint(cx + sx, cy - sy);
        }

        // Depth-cue the wireframe (fade far edges toward the canvas background) so
        // the silhouette reads as 3D at a glance instead of a flat tangle of
        // equal-weight lines -- the closest a pure edge wireframe can get to
        // Microvellum's solid-shaded look without a real B-rep kernel to fill faces.
        var visibleWires = new List<Scene3DWire>();
        float minDepth = float.MaxValue, maxDepth = float.MinValue;
        foreach (var w in scene.Wires3D)
        {
            if (w.Points.Count < 2) continue;
            if (vis != null && !vis.Contains(w.Layer)) continue;
            visibleWires.Add(w);
            foreach (var p in w.Points)
            {
                float depth = Vector3.Dot(p - _camCenter, eyeDir);
                if (depth < minDepth) minDepth = depth;
                if (depth > maxDepth) maxDepth = depth;
            }
        }
        float depthRange = maxDepth - minDepth;
        var bg = Theme.CanvasBackground;

        foreach (var w in visibleWires)
        {
            for (int i = 1; i < w.Points.Count; i++)
            {
                var a = w.Points[i - 1];
                var b = w.Points[i];
                float t = 1f;
                if (depthRange > 1e-6f)
                {
                    float depthA = Vector3.Dot(a - _camCenter, eyeDir);
                    float depthB = Vector3.Dot(b - _camCenter, eyeDir);
                    t = ((depthA + depthB) / 2f - minDepth) / depthRange;
                }
                // Blend toward the background at the far end; floor at 35% of the
                // original color so nothing fully vanishes into the backdrop.
                float blend = 0.35f + 0.65f * t;
                paint.Color = new SKColor(
                    (byte)(bg.Red   + (w.Color.Red   - bg.Red)   * blend),
                    (byte)(bg.Green + (w.Color.Green - bg.Green) * blend),
                    (byte)(bg.Blue  + (w.Color.Blue  - bg.Blue)  * blend));
                canvas.DrawLine(Project(a), Project(b), paint);
            }
        }
    }

    // Created once: SKTypeface.FromFamilyName does real font lookup, and this used to run
    // on every frame -- including every mouse-move during a pan.
    internal static readonly SKTypeface TextTypeface = SKTypeface.FromFamilyName("Segoe UI");

    private static void DrawTexts(SKCanvas canvas, DxfScene scene, HashSet<string>? vis)
    {
        if (scene.Texts.Count == 0) return;

        // Placed text positions for collision avoidance (in DXF coords)
        var placed = new List<(float X, float Y, float Footprint)>();

        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Typeface    = TextTypeface,
        };

        foreach (var t in scene.Texts)
        {
            if (string.IsNullOrEmpty(t.Value)) continue;
            if (vis != null && !vis.Contains(t.Layer)) continue;

            float sz = t.Height > 0f ? t.Height : 2.5f;
            var placement = CadGeometry.PlaceText(placed, t.X, t.Y, sz, t.Rotation, t.Value);
            placed.Add(placement);
            float ax = placement.X, ay = placement.Y;

            textPaint.Color    = Theme.ForBackground(t.Color);
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

    // --- Layer panel toggle ---

    private void LayerToggle_Click(object sender, RoutedEventArgs e)
    {
        _layerPanelExpanded = !_layerPanelExpanded;
        LayerPanelCol.Width   = new GridLength(_layerPanelExpanded ? 220 : 20);
        LayerPanelBody.Visibility = _layerPanelExpanded ? Visibility.Visible : Visibility.Collapsed;
        LayerToggleBtn.Content    = _layerPanelExpanded ? "◄" : "►";
    }

    // --- Pan & Zoom ---

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.Scene?.Is3D == true)
        {
            // Left = orbit, middle = pan -- common CAD convention.
            if (e.LeftButton == MouseButtonState.Pressed) _orbiting = true;
            else if (e.MiddleButton == MouseButtonState.Pressed) _panning = true;
            else return;
        }
        else
        {
            if (e.LeftButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed) return;
            _panning = true;
        }
        _lastMouse = e.GetPosition(SkiaHost);
        _pressOrigin = _lastMouse;
        SkiaHost.Cursor = _orbiting ? Cursors.SizeAll : Cursors.ScrollAll;
        SkiaHost.CaptureMouse();
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e)
    {
        _cursorDip = null;
        if (_vm?.MeasureMode == true) Render();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var here = e.GetPosition(SkiaHost);
        UpdateReadouts(here);

        if (_vm?.MeasureMode == true && _vm.Scene?.Is3D != true)
        {
            _cursorDip = here;
            SkiaHost.Cursor = Cursors.None;   // the drawn crosshair replaces the pointer
            if (!_panning && !_orbiting) { Render(); return; }
        }

        if (!_panning && !_orbiting) return;
        var cur = e.GetPosition(SkiaHost);
        float dx = (float)(cur.X - _lastMouse.X);
        float dy = (float)(cur.Y - _lastMouse.Y);
        _lastMouse = cur;

        if (_orbiting)
        {
            const float sensitivity = 0.01f;
            _camAzimuth -= dx * sensitivity;
            _camElevation = Math.Clamp(_camElevation - dy * sensitivity, -1.48f, 1.48f); // clamp to +-~85 deg
        }
        else if (_vm?.Scene?.Is3D == true)
        {
            _camPanX += dx;
            _camPanY += dy;
        }
        else
        {
            _panX += dx;
            _panY += dy;
        }
        Render();
    }

    // Maps a screen point back through the pan/zoom/fit chain to drawing coordinates.
    // Y is negated on the way out because the scene stores DXF (Y-up) coordinates.
    private bool TryScenePoint(Point screen, out float x, out float y)
    {
        x = y = 0f;
        if (_vm?.Scene == null || _vm.Scene.Is3D || _fitZoom == 0f || _zoom == 0f) return false;
        // Must undo the canvas chain in reverse: Translate(pan) Scale(zoom)
        // Translate(fitOffset) Scale(fitZoom). The offset is applied BEFORE the fit scale,
        // so it has to be subtracted before dividing -- dividing first (as this did) put
        // the readout and every pick off by fitOffset*(fitZoom-1).
        float sx = ((float)screen.X - _panX) / _zoom;
        float sy = ((float)screen.Y - _panY) / _zoom;
        x = (sx - _fitOffsetX) / _fitZoom;
        y = -((sy - _fitOffsetY) / _fitZoom);
        return true;
    }

    // Forward transform matching TryScenePoint, for drawing overlay bits in screen space.
    private SKPoint ToScreen(float x, float y)
    {
        float lx = _fitOffsetX + _fitZoom * x;
        float ly = _fitOffsetY + _fitZoom * -y;
        return new SKPoint(_panX + _zoom * lx, _panY + _zoom * ly);
    }

    private void UpdateReadouts(Point screen)
    {
        if (_vm == null) return;
        if (_vm.Scene?.Is3D == true)
        {
            _vm.CursorReadout = "";
            _vm.ZoomReadout = $"orbit {_camAzimuth * 180f / MathF.PI:F0}° / {_camElevation * 180f / MathF.PI:F0}°";
            return;
        }
        if (TryScenePoint(screen, out float x, out float y))
            _vm.CursorReadout = $"X {x:F3}   Y {y:F3}";
        _vm.ZoomReadout = $"{_zoom * _fitZoom * 100f:F0}%";
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        bool wasPanning = _panning;
        _panning = false;
        _orbiting = false;
        SkiaHost.Cursor = _vm?.MeasureMode == true ? Cursors.None : Cursors.Arrow;
        SkiaHost.ReleaseMouseCapture();

        var p = e.GetPosition(SkiaHost);
        bool dragged = Math.Abs(p.X - _pressOrigin.X) > 3 || Math.Abs(p.Y - _pressOrigin.Y) > 3;
        if (wasPanning && !dragged && e.ChangedButton == MouseButton.Left)
            HandleClick(p);
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        float factor = e.Delta > 0 ? 1.12f : 1f / 1.12f;
        e.Handled = true;

        if (_vm?.Scene?.Is3D == true)
        {
            if (_camZoomTarget <= 0f) _camZoomTarget = _camZoom;
            _camZoomTarget *= factor;
            StartZoomEase(0f, 0f);
            return;
        }

        var p = e.GetPosition(SkiaHost);
        if (_zoomTarget <= 0f) _zoomTarget = _zoom;
        _zoomTarget *= factor;
        StartZoomEase((float)p.X, (float)p.Y);
    }

    // Steps the current zoom a fraction of the way to its target each tick, keeping the
    // point under the cursor fixed, and stops once it is close enough to matter.
    private void StartZoomEase(float anchorX, float anchorY)
    {
        _zoomTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _zoomTimer.Tick -= ZoomTick;
        void ZoomTick(object? s, EventArgs e)
        {
            bool is3d = _vm?.Scene?.Is3D == true;
            float cur = is3d ? _camZoom : _zoom;
            float target = is3d ? _camZoomTarget : _zoomTarget;

            if (MathF.Abs(target - cur) <= MathF.Max(1e-4f, cur * 0.005f))
            {
                if (is3d) _camZoom = target; else ApplyZoom(target, anchorX, anchorY);
                _zoomTimer!.Stop();
                Render();
                return;
            }

            float next = cur + (target - cur) * 0.35f;
            if (is3d) _camZoom = next; else ApplyZoom(next, anchorX, anchorY);
            UpdateReadouts(new Point(anchorX, anchorY));
            Render();
        }
        _zoomTimer.Tick += ZoomTick;
        _zoomTimer.Start();
    }

    private void ApplyZoom(float newZoom, float cx, float cy)
    {
        float factor = _zoom == 0f ? 1f : newZoom / _zoom;
        _panX = cx + (_panX - cx) * factor;
        _panY = cy + (_panY - cy) * factor;
        _zoom = newZoom;
    }
}
