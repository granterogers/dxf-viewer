using SkiaSharp;
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

                canvas.Save();
                canvas.Translate(_panX, _panY);
                canvas.Scale(_zoom);
                canvas.Translate(_fitOffsetX, _fitOffsetY);
                canvas.Scale(_fitZoom);          // apply fit scale so DXF units map to screen pixels
                DrawScene(canvas, _vm.Scene, visibleLayers);
                canvas.Restore();
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
            StrokeWidth = 0.5f,
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

    private static void DrawTexts(SKCanvas canvas, DxfScene scene, HashSet<string>? vis)
    {
        if (scene.Texts.Count == 0) return;
        var tf = SKTypeface.FromFamilyName("Segoe UI");

        // Placed text positions for collision avoidance (in DXF coords)
        var placed = new List<(float x, float y, float sz)>();

        foreach (var t in scene.Texts)
        {
            if (string.IsNullOrEmpty(t.Value)) continue;
            if (vis != null && !vis.Contains(t.Layer)) continue;

            float sz = t.Height > 0f ? t.Height : 2.5f;
            float ax = t.X, ay = t.Y;
            bool isVert = MathF.Abs(t.Rotation - 90f) < 1f;

            // Greedy collision avoidance: offset if too close to an already-placed label
            foreach (var (px, py, ps) in placed)
            {
                float thresh = (sz + ps) * 0.75f;
                if (MathF.Sqrt(MathF.Pow(ax - px, 2) + MathF.Pow(ay - py, 2)) < thresh)
                {
                    if (isVert) ax += sz * 1.2f;
                    else        ay -= sz * 1.2f;
                    break;
                }
            }
            placed.Add((ax, ay, sz));

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
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _panning = true;
        _lastMouse = e.GetPosition(SkiaHost);
        SkiaHost.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var cur = e.GetPosition(SkiaHost);
        _panX += (float)(cur.X - _lastMouse.X);
        _panY += (float)(cur.Y - _lastMouse.Y);
        _lastMouse = cur;
        Render();
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        SkiaHost.ReleaseMouseCapture();
    }

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var p = e.GetPosition(SkiaHost);
        float cx = (float)p.X, cy = (float)p.Y;
        float factor = e.Delta > 0 ? 1.12f : 1f / 1.12f;

        _panX = cx + (_panX - cx) * factor;
        _panY = cy + (_panY - cy) * factor;
        _zoom *= factor;

        Render();
        e.Handled = true;
    }
}
