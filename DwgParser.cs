using System.IO;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using SkiaSharp;

namespace DxfViewer;

// Reads DWG natively via ACadSharp and produces the same DxfScene the renderer
// already knows how to draw. Mirrors DxfParser's entity handling; kept separate
// so the mature, well-tested netDxf-based DXF path is untouched.
public static class DwgParser
{
    public static List<DxfPage> Parse(string filePath)
    {
        // Never writes -- open with the most permissive sharing mode so a file already
        // open elsewhere (Microvellum, a text editor, Explorer's preview pane) can still
        // be opened here read-only instead of failing with a sharing violation.
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        CadDocument doc = DwgReader.Read(fs, null);

        var modelScene = BuildScene(doc.ModelSpace.Entities);
        var pages = new List<DxfPage> { new("Model", modelScene) };

        // Paper Space layouts ("sheets") -- each gets its own entities (border, title
        // block, annotations) plus one composited copy of the Model geometry per
        // real Viewport window into it.
        foreach (var layout in doc.Layouts
                     .Where(l => !string.Equals(l.Name, "Model", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(l => l.TabOrder))
        {
            var block = layout.AssociatedBlock;
            if (block == null) continue;

            var paperScene = new DxfScene();
            foreach (var e in block.Entities)
            {
                if (e is Viewport vp)
                {
                    if (vp.Id == 1) continue; // implicit paper-space background viewport, not real content
                    CompositeViewport(paperScene, modelScene, vp);
                }
                else
                {
                    AddEntity(paperScene, e, null);
                }
            }
            paperScene.ComputeBounds();
            pages.Add(new DxfPage(layout.Name, paperScene));
        }

        return pages;
    }

    private static DxfScene BuildScene(CadObjectCollection<Entity> entities)
    {
        var scene = new DxfScene();
        foreach (var e in entities)
            AddEntity(scene, e, null);
        scene.ComputeBounds();
        return scene;
    }

    // Composites already-built Model geometry into a Paper Space page through a single
    // viewport's window: paperXY = vp.Center + (modelXY - vp.ViewCenter) * vp.ScaleFactor.
    // Broad-phase bounding-box cull against the viewport rectangle (not exact clipping --
    // a primitive straddling the edge may slightly overflow it) and respects FrozenLayers.
    private static void CompositeViewport(DxfScene paperScene, DxfScene modelScene, Viewport vp)
    {
        float scale = (float)vp.ScaleFactor;
        if (scale <= 0) return;
        float cx = (float)vp.Center.X, cy = (float)vp.Center.Y;
        float vcx = (float)vp.ViewCenter.X, vcy = (float)vp.ViewCenter.Y;
        float halfW = (float)vp.Width / 2f, halfH = (float)vp.Height / 2f;
        float rectMinX = cx - halfW, rectMaxX = cx + halfW;
        float rectMinY = cy - halfH, rectMaxY = cy + halfH;

        HashSet<string>? frozen = vp.FrozenLayers.Count > 0
            ? vp.FrozenLayers.Select(l => l.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        (float X, float Y) Tf(float x, float y) => (cx + (x - vcx) * scale, cy + (y - vcy) * scale);
        bool Intersects(float minX, float minY, float maxX, float maxY) =>
            minX <= rectMaxX && maxX >= rectMinX && minY <= rectMaxY && maxY >= rectMinY;
        bool Frozen(string layer) => frozen != null && frozen.Contains(layer);

        foreach (var l in modelScene.Lines)
        {
            if (Frozen(l.Layer)) continue;
            var (x1, y1) = Tf(l.X1, l.Y1);
            var (x2, y2) = Tf(l.X2, l.Y2);
            if (!Intersects(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2))) continue;
            paperScene.Lines.Add(new SceneLine(x1, y1, x2, y2, l.Color) { Layer = l.Layer });
        }
        foreach (var c in modelScene.Circles)
        {
            if (Frozen(c.Layer)) continue;
            var (px, py) = Tf(c.Cx, c.Cy);
            float r = c.R * scale;
            if (!Intersects(px - r, py - r, px + r, py + r)) continue;
            paperScene.Circles.Add(new SceneCircle(px, py, r, c.Color) { Layer = c.Layer });
        }
        foreach (var a in modelScene.Arcs)
        {
            if (Frozen(a.Layer)) continue;
            var (px, py) = Tf(a.Cx, a.Cy);
            float r = a.R * scale;
            if (!Intersects(px - r, py - r, px + r, py + r)) continue;
            paperScene.Arcs.Add(new SceneArc(px, py, r, a.StartDeg, a.EndDeg, a.Color) { Layer = a.Layer });
        }
        foreach (var p in modelScene.Polylines)
        {
            if (Frozen(p.Layer) || p.Points.Count == 0) continue;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            var pts = new List<SKPoint>(p.Points.Count);
            foreach (var pt in p.Points)
            {
                var (x, y) = Tf(pt.X, pt.Y);
                pts.Add(new SKPoint(x, y));
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (!Intersects(minX, minY, maxX, maxY)) continue;
            var poly = new ScenePolyline { Closed = p.Closed, Color = p.Color, Layer = p.Layer };
            poly.Points.AddRange(pts);
            paperScene.Polylines.Add(poly);
        }
        foreach (var t in modelScene.Texts)
        {
            if (Frozen(t.Layer)) continue;
            var (px, py) = Tf(t.X, t.Y);
            float h = t.Height * scale;
            if (!Intersects(px - h * 4, py - h, px + h * 4, py + h)) continue; // rough width guess, no glyph metrics
            paperScene.Texts.Add(new SceneText(px, py, h, t.Rotation, t.Value, t.Color) { Layer = t.Layer });
        }
    }

    private static void AddEntity(DxfScene scene, Entity e, Layer? blockLayer)
    {
        switch (e)
        {
            // Arc derives from Circle in ACadSharp -- must be matched first.
            case Arc arc:            AddArc(scene, arc, blockLayer); break;
            case Circle circle:      AddCircle(scene, circle, blockLayer); break;
            case Line line:          AddLine(scene, line, blockLayer); break;
            case LwPolyline lwp:     AddLwPolyline(scene, lwp, blockLayer); break;
            case Polyline2D p2:      AddPolyline(scene, p2, blockLayer); break;
            case Polyline3D p3:      AddPolyline(scene, p3, blockLayer); break;
            case Ellipse ellipse:    AddEllipse(scene, ellipse, blockLayer); break;
            case Spline spline:      AddSpline(scene, spline, blockLayer); break;
            case TextEntity text:    AddText(scene, text, blockLayer); break;
            case MText mtext:        AddMText(scene, mtext, blockLayer); break;
            case Insert insert:      AddInsert(scene, insert); break;
            case Solid3D solid:      AddSolid3D(scene, solid, blockLayer); break;
        }
    }

    // Insert.Explode() already flattens the referenced block into world-space
    // entities (handles nested blocks, scale/rotate/position, MInsert arrays).
    private static void AddInsert(DxfScene scene, Insert ins)
    {
        var blockLayer = ins.Layer;
        foreach (var entity in ins.Explode())
            AddEntity(scene, entity, blockLayer);
    }

    private static string LayerOf(Entity e) => e.Layer?.Name ?? "";

    private static void AddLine(DxfScene scene, Line e, Layer? bl)
    {
        scene.Lines.Add(new SceneLine(
            (float)e.StartPoint.X, (float)e.StartPoint.Y,
            (float)e.EndPoint.X, (float)e.EndPoint.Y,
            ResolveColor(e, bl)) { Layer = LayerOf(e) });
    }

    private static void AddCircle(DxfScene scene, Circle e, Layer? bl)
    {
        scene.Circles.Add(new SceneCircle(
            (float)e.Center.X, (float)e.Center.Y, (float)e.Radius,
            ResolveColor(e, bl)) { Layer = LayerOf(e) });
    }

    private static void AddArc(DxfScene scene, Arc e, Layer? bl)
    {
        // ACadSharp angles are radians; SceneArc expects degrees.
        scene.Arcs.Add(new SceneArc(
            (float)e.Center.X, (float)e.Center.Y, (float)e.Radius,
            (float)(e.StartAngle * 180 / Math.PI), (float)(e.EndAngle * 180 / Math.PI),
            ResolveColor(e, bl)) { Layer = LayerOf(e) });
    }

    private static void AddEllipse(DxfScene scene, Ellipse e, Layer? bl)
    {
        double cx = e.Center.X, cy = e.Center.Y;
        double rx = Math.Sqrt(e.MajorAxisEndPoint.X * e.MajorAxisEndPoint.X + e.MajorAxisEndPoint.Y * e.MajorAxisEndPoint.Y);
        double ry = rx * e.RadiusRatio;
        double rotRad = e.Rotation;
        bool isArc = !e.IsFullEllipse;
        double startRad = e.StartParameter, endRad = e.EndParameter;
        if (endRad <= startRad) endRad += 2 * Math.PI;
        double span = isArc ? endRad - startRad : 2 * Math.PI;
        int segs = Math.Max(16, (int)(span * 180 / Math.PI / CadGeometry.ArcDegreesPerSeg));

        var poly = new ScenePolyline { Closed = !isArc, Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        for (int i = 0; i <= segs; i++)
        {
            double t = startRad + i * span / segs;
            double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
            double wx = cx + ex * Math.Cos(rotRad) - ey * Math.Sin(rotRad);
            double wy = cy + ex * Math.Sin(rotRad) + ey * Math.Cos(rotRad);
            poly.Points.Add(new SKPoint((float)wx, (float)wy));
        }
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);
    }

    private static void AddLwPolyline(DxfScene scene, LwPolyline e, Layer? bl)
    {
        var verts = e.Vertices;
        if (verts.Count < 2) return;

        var poly = new ScenePolyline { Closed = e.IsClosed, Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        for (int i = 0; i < verts.Count; i++)
        {
            int ni = (i + 1) % verts.Count;
            if (ni == 0 && !e.IsClosed) { AddVert(verts[i].Location); break; }

            AddVert(verts[i].Location);

            if (Math.Abs(verts[i].Bulge) > 1e-9)
            {
                foreach (var bp in CadGeometry.BulgePoints(
                    (verts[i].Location.X, verts[i].Location.Y),
                    (verts[ni].Location.X, verts[ni].Location.Y),
                    verts[i].Bulge))
                    poly.Points.Add(new SKPoint((float)bp.X, (float)bp.Y));
            }
        }
        if (!e.IsClosed && verts.Count > 0) AddVert(verts[^1].Location);
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);

        void AddVert(CSMath.XY p) => poly.Points.Add(new SKPoint((float)p.X, (float)p.Y));
    }

    private static void AddPolyline<T>(DxfScene scene, Polyline<T> e, Layer? bl) where T : Vertex
    {
        var verts = e.Vertices.ToList();
        if (verts.Count < 2) return;

        var poly = new ScenePolyline { Closed = e.IsClosed, Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        for (int i = 0; i < verts.Count; i++)
        {
            int ni = (i + 1) % verts.Count;
            if (ni == 0 && !e.IsClosed) { AddVert(verts[i].Location); break; }

            AddVert(verts[i].Location);

            if (Math.Abs(verts[i].Bulge) > 1e-9)
            {
                foreach (var bp in CadGeometry.BulgePoints(
                    (verts[i].Location.X, verts[i].Location.Y),
                    (verts[ni].Location.X, verts[ni].Location.Y),
                    verts[i].Bulge))
                    poly.Points.Add(new SKPoint((float)bp.X, (float)bp.Y));
            }
        }
        if (!e.IsClosed && verts.Count > 0) AddVert(verts[^1].Location);
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);

        void AddVert(CSMath.XYZ p) => poly.Points.Add(new SKPoint((float)p.X, (float)p.Y));
    }

    private static void AddSpline(DxfScene scene, Spline e, Layer? bl)
    {
        var pts = e.PolygonalVertexes(64);
        if (pts == null || pts.Count < 2) return;
        var poly = new ScenePolyline { Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        foreach (var p in pts)
            poly.Points.Add(new SKPoint((float)p.X, (float)p.Y));
        scene.Polylines.Add(poly);
    }

    private static void AddText(DxfScene scene, TextEntity e, Layer? bl)
    {
        if (string.IsNullOrWhiteSpace(e.Value)) return;
        double h = e.Height > 0 ? e.Height : 2.5;
        scene.Texts.Add(new SceneText(
            (float)e.InsertPoint.X, (float)e.InsertPoint.Y,
            (float)h, (float)(e.Rotation * 180 / Math.PI),
            CadGeometry.DecodeDxfText(e.Value), ResolveColor(e, bl)) { Layer = LayerOf(e) });
    }

    private static void AddMText(DxfScene scene, MText e, Layer? bl)
    {
        var raw = CadGeometry.DecodeDxfText(e.PlainText);
        if (string.IsNullOrWhiteSpace(raw)) return;
        double h = e.Height > 0 ? e.Height : 2.5;
        scene.Texts.Add(new SceneText(
            (float)e.InsertPoint.X, (float)e.InsertPoint.Y,
            (float)h, (float)(e.Rotation * 180 / Math.PI),
            raw, ResolveColor(e, bl)) { Layer = LayerOf(e) });
    }

    // 3D solids have no 2D drawing to show, but DWG keeps a legacy wireframe
    // edge list (Solid3D.Wires) alongside the ACIS body for backward-compatible
    // display. Keep the raw 3D points -- the orbit camera in DxfTabControl
    // projects them at render time, so the shape can actually be rotated.
    // Wire.Type is undocumented, but empirically (checked against real samples,
    // rendering each type in isolation) Type 1 is the real 2-point straight-edge
    // geometry -- curves are already polygonalized into many short Type-1
    // segments. Types 2/3 are degenerate/scattered artifacts unrelated to the
    // part's actual shape (stray zigzags, disconnected fragments) and must be
    // dropped or the view looks "ghostly".
    private const byte RealEdgeWireType = 1;

    private static void AddSolid3D(DxfScene scene, Solid3D e, Layer? bl)
    {
        var color = ResolveColor(e, bl);
        var layer = LayerOf(e);
        foreach (var wire in e.Wires)
        {
            if (wire.Type != RealEdgeWireType) continue;
            if (wire.Points.Count < 2) continue;
            var w3 = new Scene3DWire { Color = color, Layer = layer };
            foreach (var p in wire.Points)
                w3.Points.Add(new System.Numerics.Vector3((float)p.X, (float)p.Y, (float)p.Z));
            scene.Wires3D.Add(w3);
        }
    }

    // --- Color resolution ---

    private static SKColor ResolveColor(Entity entity, Layer? blockLayer)
    {
        var c = entity.Color;
        if (c.IsByBlock) return ColorOf(blockLayer);
        if (c.IsByLayer) return ColorOf(entity.Layer);
        if (c.IsTrueColor) return new SKColor(c.R, c.G, c.B);
        return CadGeometry.AciIndexToSKColor(c.Index);
    }

    private static SKColor ColorOf(Layer? layer)
    {
        if (layer == null) return new SKColor(255, 255, 255);
        var c = layer.Color;
        if (c.IsTrueColor) return new SKColor(c.R, c.G, c.B);
        return CadGeometry.AciIndexToSKColor(c.Index);
    }
}
