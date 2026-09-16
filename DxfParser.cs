using System.IO;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using SkiaSharp;
using AciColor = netDxf.AciColor;

namespace DxfViewer;

public static class DxfParser
{
    public static List<DxfPage> Parse(string filePath) => new() { new DxfPage("Model", ParseScene(filePath)) };

    private static DxfScene ParseScene(string filePath)
    {
        // Try netDxf first -- handles modern DXF with a $ACADVER header.
        try
        {
            DxfDocument doc;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                doc = DxfDocument.Load(fs);

            if (HasAnyEntities(doc))
            {
                var scene = new DxfScene();
                ParseDocument(scene, doc);
                scene.ComputeBounds();
                return scene;
            }
        }
        catch { }

        return ParseLegacy(filePath);
    }

    private static bool HasAnyEntities(DxfDocument doc) =>
        doc.Lines.Any() || doc.Arcs.Any() || doc.Circles.Any() ||
        doc.Ellipses.Any() || doc.LwPolylines.Any() || doc.Polylines.Any() ||
        doc.Texts.Any() || doc.MTexts.Any() || doc.Inserts.Any() ||
        doc.Splines.Any() || doc.Points.Any() ||
        doc.Dimensions.Any() || doc.Hatches.Any();

    // --- Modern parser (netDxf) ---

    private static void ParseDocument(DxfScene scene, DxfDocument doc)
    {
        try { _ltScale = doc.DrawingVariables.LtScale; } catch { _ltScale = 1.0; }
        if (_ltScale <= 1e-9) _ltScale = 1.0;

        foreach (var e in doc.Lines)       AddLine(scene, e, null);
        foreach (var e in doc.Arcs)        AddArc(scene, e, null);
        foreach (var e in doc.Circles)     AddCircle(scene, e, null);
        foreach (var e in doc.Ellipses)    AddEllipse(scene, e, null);
        foreach (var e in doc.LwPolylines) AddLwPolyline(scene, e, null);
        foreach (var e in doc.Polylines)   AddPolyline(scene, e, null);
        foreach (var e in doc.Splines)     AddSpline(scene, e, null);
        foreach (var e in doc.Texts)       AddText(scene, e, null);
        foreach (var e in doc.MTexts)      AddMText(scene, e, null);
        foreach (var e in doc.Points)      AddPoint(scene, e, null);
        foreach (var e in doc.Hatches)     AddHatch(scene, e);
        foreach (var e in doc.Dimensions)  AddDimension(scene, e);
        foreach (var e in doc.Inserts)     AddInsert(scene, e);
    }

    private static void AddEntityFromBlock(DxfScene scene, EntityObject entity, Layer? blockLayer, Matrix4 xform, int depth)
    {
        switch (entity)
        {
            case Line e:       AddLine(scene, e, blockLayer, xform); break;
            case Arc e:        AddArc(scene, e, blockLayer, xform); break;
            case Circle e:     AddCircle(scene, e, blockLayer, xform); break;
            case LwPolyline e: AddLwPolyline(scene, e, blockLayer, xform); break;
            case Polyline e:   AddPolyline(scene, e, blockLayer, xform); break;
            case Ellipse e:    AddEllipse(scene, e, blockLayer, xform); break;
            case Spline e:     AddSpline(scene, e, blockLayer, xform); break;
            case Point e:      AddPoint(scene, e, blockLayer, xform); break;
            case Text e:       AddText(scene, e, blockLayer, xform); break;
            case MText e:      AddMText(scene, e, blockLayer, xform); break;
            case Insert e:     AddInsert(scene, e, xform, depth + 1); break;
        }
    }

    // Guards against a malformed file whose block definitions reference each other in a
    // cycle -- without a cap that recurses until the stack dies.
    private const int MaxBlockNestDepth = 16;

    private static void AddInsert(DxfScene scene, Insert ins, Matrix4? parent = null, int depth = 0)
    {
        if (ins.Block == null || depth > MaxBlockNestDepth) return;

        double sinR = Math.Sin(ins.Rotation * Math.PI / 180);
        double cosR = Math.Cos(ins.Rotation * Math.PI / 180);
        double sx = ins.Scale.X, sy = ins.Scale.Y;
        double tx = ins.Position.X, ty = ins.Position.Y;

        // Build 2D transform: scale -> rotate -> translate
        var xform = new Matrix4(
            cosR * sx, -sinR * sy, 0, tx,
            sinR * sx,  cosR * sy, 0, ty,
            0, 0, 1, 0,
            0, 0, 0, 1);

        // A nested INSERT's own transform is relative to the block that contains it, so it
        // has to be composed with every enclosing insert's transform, outermost first.
        if (parent.HasValue) xform = Compose(parent.Value, xform);

        var blockLayer = ins.Layer;
        foreach (var entity in ins.Block.Entities)
            AddEntityFromBlock(scene, entity, blockLayer, xform, depth);
    }

    // Composes two affine transforms over the 2D subset ApplyXform actually reads
    // (M11/M12/M14 and M21/M22/M24), producing "apply inner, then outer".
    private static Matrix4 Compose(Matrix4 outer, Matrix4 inner) => new(
        outer.M11 * inner.M11 + outer.M12 * inner.M21,
        outer.M11 * inner.M12 + outer.M12 * inner.M22,
        0,
        outer.M11 * inner.M14 + outer.M12 * inner.M24 + outer.M14,
        outer.M21 * inner.M11 + outer.M22 * inner.M21,
        outer.M21 * inner.M12 + outer.M22 * inner.M22,
        0,
        outer.M21 * inner.M14 + outer.M22 * inner.M24 + outer.M24,
        0, 0, 1, 0,
        0, 0, 0, 1);

    private static float XformScale(Matrix4? xf) =>
        xf.HasValue ? (float)Math.Sqrt(xf.Value.M11 * xf.Value.M11 + xf.Value.M21 * xf.Value.M21) : 1f;

    private static float XformRotationDeg(Matrix4? xf) =>
        xf.HasValue ? (float)(Math.Atan2(xf.Value.M21, xf.Value.M11) * 180 / Math.PI) : 0f;

    private static Vector3 ApplyXform(Vector3 pt, Matrix4 xform)
    {
        return new Vector3(
            xform.M11 * pt.X + xform.M12 * pt.Y + xform.M14,
            xform.M21 * pt.X + xform.M22 * pt.Y + xform.M24,
            0);
    }

    private static string LayerOf(EntityObject e) => e.Layer?.Name ?? "";

    // Header LTSCALE for the document currently being parsed. Parsing is single-threaded
    // and one document at a time, so a static is enough and avoids threading a scale
    // argument through every Add* method.
    private static double _ltScale = 1.0;

    // An entity's own linetype wins; "ByLayer" defers to the layer's. LinetypeScale is a
    // per-entity multiplier on top of the drawing-wide LTSCALE.
    private static float[]? DashOf(EntityObject e)
    {
        string? name = e.Linetype?.Name;
        if (string.IsNullOrEmpty(name) || name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
            name = e.Layer?.Linetype?.Name;
        return CadGeometry.LinetypeDash(name, _ltScale * (e.LinetypeScale > 1e-9 ? e.LinetypeScale : 1.0));
    }

    private static void AddLine(DxfScene scene, Line e, Layer? bl, Matrix4? xf = null)
    {
        var s = xf.HasValue ? ApplyXform(e.StartPoint, xf.Value) : e.StartPoint;
        var p = xf.HasValue ? ApplyXform(e.EndPoint, xf.Value) : e.EndPoint;
        scene.Lines.Add(new SceneLine((float)s.X, (float)s.Y, (float)p.X, (float)p.Y,
            ResolveColor(e, bl)) { Layer = LayerOf(e), Dash = DashOf(e) });
    }

    private static void AddCircle(DxfScene scene, Circle e, Layer? bl, Matrix4? xf = null)
    {
        var c = xf.HasValue ? ApplyXform(e.Center, xf.Value) : e.Center;
        float scale = xf.HasValue ? (float)Math.Sqrt(xf.Value.M11 * xf.Value.M11 + xf.Value.M21 * xf.Value.M21) : 1f;
        scene.Circles.Add(new SceneCircle((float)c.X, (float)c.Y, (float)e.Radius * scale,
            ResolveColor(e, bl)) { Layer = LayerOf(e), Dash = DashOf(e) });
    }

    private static void AddArc(DxfScene scene, Arc e, Layer? bl, Matrix4? xf = null)
    {
        var c = xf.HasValue ? ApplyXform(e.Center, xf.Value) : e.Center;
        float scale = xf.HasValue ? (float)Math.Sqrt(xf.Value.M11 * xf.Value.M11 + xf.Value.M21 * xf.Value.M21) : 1f;
        scene.Arcs.Add(new SceneArc((float)c.X, (float)c.Y, (float)e.Radius * scale,
            (float)e.StartAngle, (float)e.EndAngle, ResolveColor(e, bl)) { Layer = LayerOf(e), Dash = DashOf(e) });
    }

    private static void AddEllipse(DxfScene scene, Ellipse e, Layer? bl, Matrix4? xf = null)
    {
        // Discretize to polyline
        double cx = e.Center.X, cy = e.Center.Y;
        double rx = e.MajorAxis / 2;
        double ry = e.MajorAxis * e.MinorAxis / 2;
        double rotRad = e.Rotation * Math.PI / 180;
        bool isArc = Math.Abs(e.StartAngle) > 1e-6 || Math.Abs(e.EndAngle - 360) > 1e-6;
        double startRad = e.StartAngle * Math.PI / 180;
        double endRad = e.EndAngle * Math.PI / 180;
        if (endRad <= startRad) endRad += 2 * Math.PI;
        double span = isArc ? endRad - startRad : 2 * Math.PI;
        int segs = Math.Max(16, (int)(span * 180 / Math.PI / CadGeometry.ArcDegreesPerSeg));

        var poly = new ScenePolyline { Closed = !isArc, Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        poly.Dash = DashOf(e);
        for (int i = 0; i <= segs; i++)
        {
            double t = startRad + i * span / segs;
            double ex = rx * Math.Cos(t), ey = ry * Math.Sin(t);
            double wx = cx + ex * Math.Cos(rotRad) - ey * Math.Sin(rotRad);
            double wy = cy + ex * Math.Sin(rotRad) + ey * Math.Cos(rotRad);
            var pt = xf.HasValue ? ApplyXform(new Vector3(wx, wy, 0), xf.Value) : new Vector3(wx, wy, 0);
            poly.Points.Add(new SKPoint((float)pt.X, (float)pt.Y));
        }
        if (poly.Points.Count >= 2) scene.Polylines.Add(poly);
    }

    private static void AddLwPolyline(DxfScene scene, LwPolyline e, Layer? bl, Matrix4? xf = null)
    {
        var verts = e.Vertexes;
        if (verts.Count < 2) return;

        var poly = new ScenePolyline { Closed = e.IsClosed, Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        poly.Dash = DashOf(e);

        var path = new SKPath();
        bool pathStarted = false, anyBulge = false;
        void PathTo(Vector2 p2, bool line)
        {
            var v3 = xf.HasValue ? ApplyXform(new Vector3(p2.X, p2.Y, 0), xf.Value) : new Vector3(p2.X, p2.Y, 0);
            var sp = new SKPoint((float)v3.X, (float)-v3.Y);   // path is built in screen space (Y negated)
            if (!pathStarted) { path.MoveTo(sp); pathStarted = true; }
            else if (line) path.LineTo(sp);
        }

        for (int i = 0; i < verts.Count; i++)
        {
            int ni = (i + 1) % verts.Count;
            if (ni == 0 && !e.IsClosed) { AddVert(verts[i].Position); break; }

            AddVert(verts[i].Position);
            PathTo(verts[i].Position, line: true);

            double bulge = verts[i].Bulge;
            if (Math.Abs(bulge) > 1e-9)
            {
                anyBulge = true;
                foreach (var bp in CadGeometry.BulgePoints(
                    (verts[i].Position.X, verts[i].Position.Y),
                    (verts[ni].Position.X, verts[ni].Position.Y),
                    bulge))
                    AddVertRaw(new Vector2(bp.X, bp.Y));

                AppendBulgeArc(path, verts[i].Position, verts[ni].Position, bulge, xf);
            }
        }
        if (!e.IsClosed && verts.Count > 0)
        {
            AddVert(verts[^1].Position);
            PathTo(verts[^1].Position, line: true);
        }
        if (poly.Points.Count < 2) { path.Dispose(); return; }

        if (anyBulge)
        {
            if (e.IsClosed) path.Close();
            poly.CurvePath = path;
        }
        else path.Dispose();

        scene.Polylines.Add(poly);

        void AddVert(Vector2 p2)
        {
            var v3 = xf.HasValue ? ApplyXform(new Vector3(p2.X, p2.Y, 0), xf.Value) : new Vector3(p2.X, p2.Y, 0);
            poly.Points.Add(new SKPoint((float)v3.X, (float)v3.Y));
        }
        void AddVertRaw(Vector2 p2)
        {
            var v3 = xf.HasValue ? ApplyXform(new Vector3(p2.X, p2.Y, 0), xf.Value) : new Vector3(p2.X, p2.Y, 0);
            poly.Points.Add(new SKPoint((float)v3.X, (float)v3.Y));
        }
    }

    // Appends the bulge arc from -> to as a real conic arc, so it stays smooth at any zoom
    // instead of showing the segment facets frozen in at parse time. Mirrors the geometry
    // in CadGeometry.BulgePoints, including its hard-won use of the radius *magnitude*.
    private static void AppendBulgeArc(SKPath path, Vector2 from, Vector2 to, double bulge, Matrix4? xf)
    {
        double angle = 4.0 * Math.Atan(Math.Abs(bulge));
        double totalAngle = bulge >= 0 ? angle : -angle;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12 || Math.Abs(Math.Sin(totalAngle / 2)) < 1e-12) return;

        double r = Math.Abs(d / (2 * Math.Sin(totalAngle / 2)));
        var end3 = xf.HasValue ? ApplyXform(new Vector3(to.X, to.Y, 0), xf.Value) : new Vector3(to.X, to.Y, 0);
        float scale = XformScale(xf);
        float rr = (float)(r * scale);

        // Y is negated for screen space, which mirrors the plane -- so the sweep direction
        // flips relative to the DXF-space bulge sign.
        var dir = bulge >= 0 ? SKPathDirection.CounterClockwise : SKPathDirection.Clockwise;
        var size = angle > Math.PI ? SKPathArcSize.Large : SKPathArcSize.Small;

        path.ArcTo(new SKPoint(rr, rr), 0f, size, dir,
                   new SKPoint((float)end3.X, (float)-end3.Y));
    }

    private static void AddPolyline(DxfScene scene, Polyline e, Layer? bl, Matrix4? xf = null)
    {
        var verts = e.Vertexes;
        if (verts.Count < 2) return;

        var poly = new ScenePolyline { Closed = e.IsClosed, Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        poly.Dash = DashOf(e);
        foreach (var v in verts)
        {
            var pt = xf.HasValue ? ApplyXform(v.Position, xf.Value) : v.Position;
            poly.Points.Add(new SKPoint((float)pt.X, (float)pt.Y));
        }
        scene.Polylines.Add(poly);
    }

    private static void AddSpline(DxfScene scene, Spline e, Layer? bl, Matrix4? xf = null)
    {
        var pts = e.PolygonalVertexes(64);
        if (pts == null || pts.Count < 2) return;
        var poly = new ScenePolyline { Color = ResolveColor(e, bl) };
        poly.Layer = LayerOf(e);
        poly.Dash = DashOf(e);
        foreach (var p in pts)
        {
            var q = xf.HasValue ? ApplyXform(new Vector3(p.X, p.Y, 0), xf.Value) : new Vector3(p.X, p.Y, 0);
            poly.Points.Add(new SKPoint((float)q.X, (float)q.Y));
        }
        scene.Polylines.Add(poly);
    }

    // POINT has no extent of its own, so it is drawn as a small cross. The arm length is
    // in drawing units here because the scene has no notion of zoom; it is deliberately
    // tiny so a point never reads as geometry.
    private static void AddPoint(DxfScene scene, Point e, Layer? bl, Matrix4? xf = null)
    {
        var p = xf.HasValue ? ApplyXform(e.Position, xf.Value) : e.Position;
        var color = ResolveColor(e, bl);
        string layer = LayerOf(e);
        float x = (float)p.X, y = (float)p.Y;
        float r = 0.5f * XformScale(xf);
        scene.Lines.Add(new SceneLine(x - r, y, x + r, y, color) { Layer = layer });
        scene.Lines.Add(new SceneLine(x, y - r, x, y + r, color) { Layer = layer });
    }

    private static (TextHAlign H, TextVAlign V) MapAlignment(TextAlignment a) => a switch
    {
        TextAlignment.TopLeft        => (TextHAlign.Left,   TextVAlign.Top),
        TextAlignment.TopCenter      => (TextHAlign.Center, TextVAlign.Top),
        TextAlignment.TopRight       => (TextHAlign.Right,  TextVAlign.Top),
        TextAlignment.MiddleLeft     => (TextHAlign.Left,   TextVAlign.Middle),
        TextAlignment.MiddleCenter   => (TextHAlign.Center, TextVAlign.Middle),
        TextAlignment.MiddleRight    => (TextHAlign.Right,  TextVAlign.Middle),
        TextAlignment.BottomLeft     => (TextHAlign.Left,   TextVAlign.Bottom),
        TextAlignment.BottomCenter   => (TextHAlign.Center, TextVAlign.Bottom),
        TextAlignment.BottomRight    => (TextHAlign.Right,  TextVAlign.Bottom),
        TextAlignment.BaselineCenter => (TextHAlign.Center, TextVAlign.Baseline),
        TextAlignment.BaselineRight  => (TextHAlign.Right,  TextVAlign.Baseline),
        TextAlignment.Middle         => (TextHAlign.Center, TextVAlign.Middle),
        _                            => (TextHAlign.Left,   TextVAlign.Baseline),
    };

    private static void AddText(DxfScene scene, Text e, Layer? bl, Matrix4? xf = null)
    {
        if (string.IsNullOrWhiteSpace(e.Value)) return;
        var p = xf.HasValue ? ApplyXform(e.Position, xf.Value) : e.Position;
        double h = (e.Height > 0 ? e.Height : 2.5) * XformScale(xf);
        var (ha, va) = MapAlignment(e.Alignment);
        scene.Texts.Add(new SceneText(
            (float)p.X, (float)p.Y,
            (float)h, (float)e.Rotation + XformRotationDeg(xf),
            CadGeometry.DecodeDxfText(e.Value), ResolveColor(e, bl))
            { Layer = LayerOf(e), HAlign = ha, VAlign = va });
    }

    private static void AddMText(DxfScene scene, MText e, Layer? bl, Matrix4? xf = null)
    {
        var raw = CadGeometry.DecodeDxfText(e.PlainText());
        if (string.IsNullOrWhiteSpace(raw)) return;

        var p = xf.HasValue ? ApplyXform(e.Position, xf.Value) : e.Position;
        double h = (e.Height > 0 ? e.Height : 2.5) * XformScale(xf);
        float rot = (float)e.Rotation + XformRotationDeg(xf);
        var color = ResolveColor(e, bl);
        string layer = LayerOf(e);
        var (ha, va) = MapMTextAttachment(e.AttachmentPoint);

        double wrapWidth = e.RectangleWidth * XformScale(xf);
        var lines = CadGeometry.WrapMText(raw, h, wrapWidth);

        // Successive lines step down the page along the text's own reading direction, so
        // rotated multi-line notes stack correctly instead of marching off horizontally.
        double rad = rot * Math.PI / 180;
        double stepX = Math.Sin(rad) * h * 1.2, stepY = -Math.Cos(rad) * h * 1.2;

        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            scene.Texts.Add(new SceneText(
                (float)(p.X + stepX * i), (float)(p.Y + stepY * i),
                (float)h, rot, lines[i], color)
                { Layer = layer, HAlign = ha, VAlign = va });
        }
    }

    private static (TextHAlign H, TextVAlign V) MapMTextAttachment(MTextAttachmentPoint a) => a switch
    {
        MTextAttachmentPoint.TopLeft      => (TextHAlign.Left,   TextVAlign.Top),
        MTextAttachmentPoint.TopCenter    => (TextHAlign.Center, TextVAlign.Top),
        MTextAttachmentPoint.TopRight     => (TextHAlign.Right,  TextVAlign.Top),
        MTextAttachmentPoint.MiddleLeft   => (TextHAlign.Left,   TextVAlign.Middle),
        MTextAttachmentPoint.MiddleCenter => (TextHAlign.Center, TextVAlign.Middle),
        MTextAttachmentPoint.MiddleRight  => (TextHAlign.Right,  TextVAlign.Middle),
        MTextAttachmentPoint.BottomLeft   => (TextHAlign.Left,   TextVAlign.Bottom),
        MTextAttachmentPoint.BottomCenter => (TextHAlign.Center, TextVAlign.Bottom),
        MTextAttachmentPoint.BottomRight  => (TextHAlign.Right,  TextVAlign.Bottom),
        _                                 => (TextHAlign.Left,   TextVAlign.Top),
    };

    // A HATCH's fill pattern isn't reproduced, but its boundary is real geometry -- drawing
    // the outline is strictly better than rendering nothing at all and silently losing the
    // region, which is what happened before.
    private static void AddHatch(DxfScene scene, Hatch e)
    {
        foreach (var path in e.BoundaryPaths)
        {
            foreach (var edge in path.Edges)
            {
                EntityObject? ent = null;
                try { ent = edge.ConvertTo(); } catch { }
                if (ent == null) continue;
                ent.Layer = e.Layer;
                ent.Color = e.Color;
                AddEntityFromBlock(scene, ent, e.Layer, Matrix4.Identity, 0);
            }
        }
    }

    // A DIMENSION carries an anonymous block holding the exact geometry AutoCAD drew for
    // it (dimension line, extension lines, ticks, value text), so rendering that block is
    // more faithful than re-deriving the presentation from the measurement points.
    private static void AddDimension(DxfScene scene, Dimension e)
    {
        if (e.Block == null) return;

        int lineStart = scene.Lines.Count;
        int textStart = scene.Texts.Count;

        foreach (var entity in e.Block.Entities)
            AddEntityFromBlock(scene, entity, e.Layer, Matrix4.Identity, 0);

        // Same semantic exemption the legacy parser applies: a ROUTE-dimstyle dimension
        // measures a CNC toolpath distance, not a part edge, and can sit far outside the
        // part's own footprint -- it still draws, it just must not drive the fit view.
        if (e.Style?.Name?.StartsWith("ROUTE", StringComparison.OrdinalIgnoreCase) != true) return;

        for (int i = lineStart; i < scene.Lines.Count; i++)
            scene.Lines[i] = scene.Lines[i] with { BoundsExempt = true };
        for (int i = textStart; i < scene.Texts.Count; i++)
            scene.Texts[i] = scene.Texts[i] with { BoundsExempt = true };
    }

    // --- Color resolution ---

    private static SKColor ResolveColor(EntityObject entity, Layer? blockLayer)
    {
        var aci = entity.Color;
        if (aci.IsByBlock) aci = blockLayer?.Color ?? AciColor.Default;
        if (aci.IsByLayer) aci = entity.Layer?.Color ?? AciColor.Default;
        if (aci.IsByLayer || aci.IsByBlock) return new SKColor(255, 255, 255);
        if (aci.UseTrueColor) return new SKColor(aci.R, aci.G, aci.B);
        return CadGeometry.AciIndexToSKColor(aci.Index);
    }

    // --- Legacy (headerless) DXF parser ---
    // Handles pre-R12 files that lack $ACADVER/HEADER/BLOCKS.
    // Entities: POLYLINE+VERTEX+SEQEND (with bulge arcs), CIRCLE, ARC, LINE, TEXT, DIMENSION.
    // Unknown entities are silently skipped.

    private readonly record struct GrpRec(int Code, string Value);

    private static DxfScene ParseLegacy(string filePath)
    {
        var recs = ReadGroupCodes(filePath);
        var scene = new DxfScene();
        int start = FindSection(recs, "ENTITIES");
        if (start >= 0) ParseLegacyEntities(scene, recs, start);
        scene.ComputeBounds();
        return scene;
    }

    private static List<GrpRec> ReadGroupCodes(string filePath)
    {
        var result = new List<GrpRec>(512);
        // Never writes -- open with the most permissive sharing mode so a file already
        // open elsewhere (Microvellum, a text editor, Explorer's preview pane) can still
        // be opened here read-only instead of failing with a sharing violation.
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        while (true)
        {
            var codeLine = sr.ReadLine();
            if (codeLine == null) break;
            var valLine = sr.ReadLine();
            if (valLine == null) break;
            if (int.TryParse(codeLine.Trim(), out int code))
                result.Add(new GrpRec(code, valLine.Trim()));
        }
        return result;
    }

    private static int FindSection(List<GrpRec> recs, string name)
    {
        for (int i = 0; i < recs.Count - 1; i++)
            if (recs[i].Code == 0 && recs[i].Value == "SECTION" &&
                recs[i + 1].Code == 2 && recs[i + 1].Value == name)
                return i + 2;
        return -1;
    }

    private static void ParseLegacyEntities(DxfScene scene, List<GrpRec> recs, int start)
    {
        int i = start;
        while (i < recs.Count)
        {
            var r = recs[i];
            if (r.Code != 0) { i++; continue; }
            switch (r.Value)
            {
                case "ENDSEC": case "EOF": return;
                case "LINE":     i = LegacyLine(scene, recs, i + 1);     break;
                case "CIRCLE":   i = LegacyCircle(scene, recs, i + 1);   break;
                case "ARC":      i = LegacyArc(scene, recs, i + 1);      break;
                case "POLYLINE":  i = LegacyPolyline(scene, recs, i + 1);  break;
                case "TEXT":      i = LegacyText(scene, recs, i + 1);      break;
                case "DIMENSION": i = LegacyDimension(scene, recs, i + 1); break;
                default:          i = NextCode0(recs, i + 1);              break;
            }
        }
    }

    private static int NextCode0(List<GrpRec> recs, int start)
    {
        for (int i = start; i < recs.Count; i++)
            if (recs[i].Code == 0) return i;
        return recs.Count;
    }

    private static SKColor LegacyColor(List<GrpRec> recs, int start, int end)
    {
        for (int i = start; i < Math.Min(end, recs.Count); i++)
            if (recs[i].Code == 62 && short.TryParse(recs[i].Value, out short aci))
                return CadGeometry.AciIndexToSKColor(aci);
        return new SKColor(255, 255, 255);
    }

    private static string LegacyLayer(List<GrpRec> recs, int start, int end)
    {
        for (int i = start; i < Math.Min(end, recs.Count); i++)
            if (recs[i].Code == 8) return recs[i].Value;
        return "";
    }

    private static float ParseF(string s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;

    private static int LegacyLine(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: x0 = ParseF(recs[i].Value); break;
                case 20: y0 = ParseF(recs[i].Value); break;
                case 11: x1 = ParseF(recs[i].Value); break;
                case 21: y1 = ParseF(recs[i].Value); break;
            }
        scene.Lines.Add(new SceneLine(x0, y0, x1, y1, LegacyColor(recs, start, end))
            { Layer = LegacyLayer(recs, start, end) });
        return end;
    }

    private static int LegacyCircle(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float cx = 0, cy = 0, r = 0;
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: cx = ParseF(recs[i].Value); break;
                case 20: cy = ParseF(recs[i].Value); break;
                case 40: r  = ParseF(recs[i].Value); break;
            }
        if (r > 0)
            scene.Circles.Add(new SceneCircle(cx, cy, r, LegacyColor(recs, start, end))
                { Layer = LegacyLayer(recs, start, end) });
        return end;
    }

    private static int LegacyArc(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float cx = 0, cy = 0, r = 0, sa = 0, ea = 180;
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: cx = ParseF(recs[i].Value); break;
                case 20: cy = ParseF(recs[i].Value); break;
                case 40: r  = ParseF(recs[i].Value); break;
                case 50: sa = ParseF(recs[i].Value); break;
                case 51: ea = ParseF(recs[i].Value); break;
            }
        if (r > 0)
            scene.Arcs.Add(new SceneArc(cx, cy, r, sa, ea, LegacyColor(recs, start, end))
                { Layer = LegacyLayer(recs, start, end) });
        return end;
    }

    private static int LegacyPolyline(DxfScene scene, List<GrpRec> recs, int start)
    {
        int hdrEnd = NextCode0(recs, start);
        var color = LegacyColor(recs, start, hdrEnd);
        bool closed = false;
        for (int i = start; i < hdrEnd; i++)
            if (recs[i].Code == 70 && int.TryParse(recs[i].Value, out int flags))
                closed = (flags & 1) != 0;

        // Collect vertices with bulge values (code 42)
        var rawVerts = new List<(float x, float y, float bulge)>();
        int i2 = hdrEnd;
        while (i2 < recs.Count)
        {
            if (recs[i2].Code != 0) { i2++; continue; }
            if (recs[i2].Value == "VERTEX")
            {
                int vEnd = NextCode0(recs, i2 + 1);
                float vx = 0, vy = 0, vb = 0;
                for (int j = i2 + 1; j < vEnd; j++)
                {
                    if (recs[j].Code == 10)      vx = ParseF(recs[j].Value);
                    else if (recs[j].Code == 20) vy = ParseF(recs[j].Value);
                    else if (recs[j].Code == 42) vb = ParseF(recs[j].Value);
                }
                rawVerts.Add((vx, vy, vb));
                i2 = vEnd;
            }
            else if (recs[i2].Value == "SEQEND")
            {
                i2 = NextCode0(recs, i2 + 1);
                break;
            }
            else break;
        }

        if (rawVerts.Count >= 2)
        {
            var poly = new ScenePolyline { Color = color, Closed = closed };
            poly.Layer = LegacyLayer(recs, start, hdrEnd);
            poly.Points.Add(new SKPoint(rawVerts[0].x, rawVerts[0].y));

            int segCount = closed ? rawVerts.Count : rawVerts.Count - 1;
            for (int k = 0; k < segCount; k++)
            {
                var (x1, y1, bulge) = rawVerts[k];
                var (x2, y2, _)     = rawVerts[(k + 1) % rawVerts.Count];

                if (Math.Abs(bulge) > 1e-9)
                {
                    foreach (var bp in CadGeometry.BulgePoints((x1, y1), (x2, y2), bulge))
                        poly.Points.Add(new SKPoint((float)bp.X, (float)bp.Y));
                }
                else
                {
                    poly.Points.Add(new SKPoint(x2, y2));
                }
            }
            if (poly.Points.Count >= 2) scene.Polylines.Add(poly);
        }
        return i2;
    }

    private static int LegacyDimension(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        string text = "";
        string dimStyle = "";
        float dx = 0, dy = 0;   // dimension line definition point (10, 20)
        float tx = 0, ty = 0;   // text midpoint (11, 21)
        float e1x = 0, e1y = 0; // extension line 1 origin (13, 23)
        float e2x = 0, e2y = 0; // extension line 2 origin (14, 24)
        float angle = 0;         // dimension direction angle (50)

        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case  1: text     = recs[i].Value;          break;
                case  3: dimStyle = recs[i].Value;           break;
                case 10: dx       = ParseF(recs[i].Value);  break;
                case 20: dy       = ParseF(recs[i].Value);  break;
                case 11: tx       = ParseF(recs[i].Value);  break;
                case 21: ty       = ParseF(recs[i].Value);  break;
                case 13: e1x      = ParseF(recs[i].Value);  break;
                case 23: e1y      = ParseF(recs[i].Value);  break;
                case 14: e2x      = ParseF(recs[i].Value);  break;
                case 24: e2y      = ParseF(recs[i].Value);  break;
                case 50: angle    = ParseF(recs[i].Value);  break;
            }

        var color = LegacyColor(recs, start, end);
        var layer = LegacyLayer(recs, start, end);
        // A "ROUTEDIM"-styled dimension measures a CNC toolpath/routing distance rather
        // than a part edge -- its coordinates live in the machine's routing space, which
        // can be tens of units away from the part itself (seen directly: one measured 86
        // units below a part whose own geometry spanned under 8 units, blowing the fit
        // view out to almost nothing). The same dimstyle is also used for ordinary edge
        // dimensions that sit right on the part, so this can't just recolor/hide the
        // layer wholesale -- it only exempts these specific entities from the fit-view
        // bounds calculation (same treatment as a hidden layer), leaving rendering as-is.
        bool routeAnnotation = dimStyle.StartsWith("ROUTE", StringComparison.OrdinalIgnoreCase);
        // Same reasoning as the dimension text height below: a fixed tick size doesn't
        // scale with the drawing. 3 units was often comparable to (or bigger than) the
        // entire extension-line span it was decorating -- match the small label scale
        // these files consistently use instead.
        const float tick = 0.5f;
        bool vertical = Math.Abs(angle - 90f) < 1f;

        if (vertical)
        {
            // Extension lines run horizontally to dim line X
            scene.Lines.Add(new SceneLine(e1x, e1y, dx, e1y, color) { Layer = layer, BoundsExempt = routeAnnotation });
            scene.Lines.Add(new SceneLine(e2x, e2y, dx, e2y, color) { Layer = layer, BoundsExempt = routeAnnotation });
            // Vertical dim line
            scene.Lines.Add(new SceneLine(dx, e1y, dx, e2y, color) { Layer = layer, BoundsExempt = routeAnnotation });
            // Arrow ticks
            scene.Lines.Add(new SceneLine(dx - tick, e1y - tick, dx + tick, e1y + tick, color) { Layer = layer, BoundsExempt = routeAnnotation });
            scene.Lines.Add(new SceneLine(dx - tick, e2y - tick, dx + tick, e2y + tick, color) { Layer = layer, BoundsExempt = routeAnnotation });
        }
        else
        {
            // Extension lines run vertically to dim line Y
            scene.Lines.Add(new SceneLine(e1x, e1y, e1x, dy, color) { Layer = layer, BoundsExempt = routeAnnotation });
            scene.Lines.Add(new SceneLine(e2x, e2y, e2x, dy, color) { Layer = layer, BoundsExempt = routeAnnotation });
            // Horizontal dim line
            scene.Lines.Add(new SceneLine(e1x, dy, e2x, dy, color) { Layer = layer, BoundsExempt = routeAnnotation });
            // Arrow ticks
            scene.Lines.Add(new SceneLine(e1x - tick, dy - tick, e1x + tick, dy + tick, color) { Layer = layer, BoundsExempt = routeAnnotation });
            scene.Lines.Add(new SceneLine(e2x - tick, dy - tick, e2x + tick, dy + tick, color) { Layer = layer, BoundsExempt = routeAnnotation });
        }

        // DIMENSION entities carry no text-height group code of their own (that's normally
        // governed by a dimension style/$DIMTXT, unavailable in these headerless legacy
        // files) -- match the small label-text height these exports consistently use for
        // regular TEXT entities (0.5) rather than a value disproportionate to the drawing.
        if (!string.IsNullOrEmpty(text))
            scene.Texts.Add(new SceneText(tx, ty, 0.5f, angle, CadGeometry.DecodeDxfText(text), color) { Layer = layer, BoundsExempt = routeAnnotation });

        return end;
    }

    private static int LegacyText(DxfScene scene, List<GrpRec> recs, int start)
    {
        int end = NextCode0(recs, start);
        float tx = 0, ty = 0, height = 2.5f, rot = 0;
        string value = "";
        for (int i = start; i < end; i++)
            switch (recs[i].Code)
            {
                case 10: tx     = ParseF(recs[i].Value); break;
                case 20: ty     = ParseF(recs[i].Value); break;
                case 40: height = ParseF(recs[i].Value); break;
                case 50: rot    = ParseF(recs[i].Value); break;
                case  1: value  = CadGeometry.DecodeDxfText(recs[i].Value); break;
            }
        if (!string.IsNullOrWhiteSpace(value) && height > 0)
            scene.Texts.Add(new SceneText(tx, ty, height, rot, value, LegacyColor(recs, start, end))
                { Layer = LegacyLayer(recs, start, end) });
        return end;
    }
}
