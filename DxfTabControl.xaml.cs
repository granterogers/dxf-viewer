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
    private float _camPanX, _camPanY;
    private Vector3 _camCenter;

    private WriteableBitmap? _wbm;
    private bool _layerPanelExpanded = true;

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
        if (e.PropertyName != nameof(DxfTabViewModel.State)) return;
        if (_vm?.IsLoaded == true)
            Dispatcher.Invoke(FitToWindow);
        else
        {
            _zoom = 1f; _panX = _panY = 0f;
            _camPanX = _camPanY = 0f;
            Dispatcher.Invoke(Render);
        }
    }

    private void SkiaHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = (int)SkiaHost.ActualWidth;
        var h = (int)SkiaHost.ActualHeight;
        if (w <= 0 || h <= 0) return;

        _wbm = new WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
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
        var vw = (float)_wbm.PixelWidth;
        var vh = (float)_wbm.PixelHeight;
        if (vw <= 0 || vh <= 0 || bounds.Width < 1e-4f || bounds.Height < 1e-4f) return;

        _fitZoom    = Math.Min(vw / bounds.Width, vh / bounds.Height) * 0.95f;
        _fitOffsetX = (vw - bounds.Width  * _fitZoom) / 2f - bounds.Left * _fitZoom;
        _fitOffsetY = (vh - bounds.Height * _fitZoom) / 2f - bounds.Top  * _fitZoom;
        _zoom = 1f;
        _panX = _panY = 0f;
        Render();
    }

    private void FitToWindow3D()
    {
        var scene = _vm!.Scene!;
        var vw = (float)_wbm!.PixelWidth;
        var vh = (float)_wbm.PixelHeight;
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
            canvas.Clear(new SKColor(18, 18, 30));

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
                    DrawScene3D(canvas, _vm.Scene, visibleLayers, wbm.PixelWidth, wbm.PixelHeight);
                }
                else
                {
                    canvas.Save();
                    canvas.Translate(_panX, _panY);
                    canvas.Scale(_zoom);
                    canvas.Translate(_fitOffsetX, _fitOffsetY);
                    canvas.Scale(_fitZoom);          // apply fit scale so DXF units map to screen pixels
                    DrawScene(canvas, _vm.Scene, visibleLayers);
                    canvas.Restore();
                }
            }
        }
        finally
        {
            wbm.AddDirtyRect(new Int32Rect(0, 0, wbm.PixelWidth, wbm.PixelHeight));
            wbm.Unlock();
        }
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

        foreach (var c in scene.Circles)
        {
            if (vis != null && !vis.Contains(c.Layer)) continue;
            paint.Color = c.Color;
            canvas.DrawCircle(c.Cx, -c.Cy, c.R, paint);
        }

        foreach (var a in scene.Arcs)
        {
            if (vis != null && !vis.Contains(a.Layer)) continue;
            paint.Color = a.Color;
            var oval = new SKRect(a.Cx - a.R, -a.Cy - a.R, a.Cx + a.R, -a.Cy + a.R);
            float span = a.EndDeg > a.StartDeg
                ? a.EndDeg - a.StartDeg
                : 360f - a.StartDeg + a.EndDeg;
            canvas.DrawArc(oval, -a.StartDeg, -span, false, paint);
        }

        foreach (var l in scene.Lines)
        {
            if (vis != null && !vis.Contains(l.Layer)) continue;
            paint.Color = l.Color;
            canvas.DrawLine(l.X1, -l.Y1, l.X2, -l.Y2, paint);
        }

        foreach (var p in scene.Polylines)
        {
            if (p.Points.Count < 2) continue;
            if (vis != null && !vis.Contains(p.Layer)) continue;
            paint.Color = p.Color;
            using var path = new SKPath();
            path.MoveTo(p.Points[0].X, -p.Points[0].Y);
            for (int i = 1; i < p.Points.Count; i++)
                path.LineTo(p.Points[i].X, -p.Points[i].Y);
            if (p.Closed) path.Close();
            canvas.DrawPath(path, paint);
        }

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
        var bg = new SKColor(18, 18, 30);

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

    private static void DrawTexts(SKCanvas canvas, DxfScene scene, HashSet<string>? vis)
    {
        if (scene.Texts.Count == 0) return;
        var tf = SKTypeface.FromFamilyName("Segoe UI");

        // Placed text positions for collision avoidance (in DXF coords)
        var placed = new List<(float X, float Y, float Footprint)>();

        foreach (var t in scene.Texts)
        {
            if (string.IsNullOrEmpty(t.Value)) continue;
            if (vis != null && !vis.Contains(t.Layer)) continue;

            float sz = t.Height > 0f ? t.Height : 2.5f;
            var placement = CadGeometry.PlaceText(placed, t.X, t.Y, sz, t.Rotation, t.Value);
            placed.Add(placement);
            float ax = placement.X, ay = placement.Y;

            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Color       = t.Color,
                TextSize    = sz,
                Typeface    = tf,
            };
            canvas.Save();
            canvas.Translate(ax, -ay);
            if (MathF.Abs(t.Rotation) > 0.01f)
                canvas.RotateDegrees(-t.Rotation);
            canvas.DrawText(t.Value, 0f, 0f, textPaint);
            canvas.Restore();
        }
        tf?.Dispose();
    }

    // --- Layer panel toggle ---

    private void LayerToggle_Click(object sender, RoutedEventArgs e)
    {
        _layerPanelExpanded = !_layerPanelExpanded;
        LayerPanelCol.Width   = new GridLength(_layerPanelExpanded ? 200 : 20);
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
        SkiaHost.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
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

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        _orbiting = false;
        SkiaHost.ReleaseMouseCapture();
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        float factor = e.Delta > 0 ? 1.12f : 1f / 1.12f;

        if (_vm?.Scene?.Is3D == true)
        {
            _camZoom *= factor;
            Render();
            e.Handled = true;
            return;
        }

        var p = e.GetPosition(SkiaHost);
        float cx = (float)p.X, cy = (float)p.Y;

        _panX = cx + (_panX - cx) * factor;
        _panY = cy + (_panY - cy) * factor;
        _zoom *= factor;

        Render();
        e.Handled = true;
    }
}
