using System.IO;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Header;
using netDxf.Tables;

namespace DxfViewer;

// Authors a synthetic *modern* ($ACADVER-header) DXF exercising the netDxf code paths that
// the real sample corpus barely touches -- only 1 of 313 sample DXFs has a header at all,
// so nested blocks, block-local text, justified text, POINT, DIMENSION and HATCH would
// otherwise go completely unverified by the regression sweep.
internal static class TestFixture
{
    public static int Write(string outPath)
    {
        try
        {
            var doc = new DxfDocument(DxfVersion.AutoCad2010);
            // Without this netDxf writes the DIMENSION's measurement points but no geometry
            // block, so the dimension would be invisible and the fixture would silently
            // stop covering the dimension path.
            doc.BuildDimensionBlocks = true;
            var layer = new Layer("BORDER_FIXTURE") { Color = new AciColor(7) };
            doc.Layers.Add(layer);

            // Inner block: a square plus a label. If block-local text ignores the INSERT
            // transform, this label renders at the origin instead of with its square.
            var inner = new Block("INNER");
            inner.Entities.Add(new Line(new Vector2(0, 0), new Vector2(10, 0)) { Layer = layer });
            inner.Entities.Add(new Line(new Vector2(10, 0), new Vector2(10, 10)) { Layer = layer });
            inner.Entities.Add(new Line(new Vector2(10, 10), new Vector2(0, 10)) { Layer = layer });
            inner.Entities.Add(new Line(new Vector2(0, 10), new Vector2(0, 0)) { Layer = layer });
            inner.Entities.Add(new Text("INNER", new Vector2(5, 4), 2.0) { Layer = layer });
            doc.Blocks.Add(inner);

            // Outer block contains an INSERT of the inner block -- the nesting case that
            // used to be dropped entirely.
            var outer = new Block("OUTER");
            outer.Entities.Add(new Insert(inner, new Vector2(20, 0)) { Layer = layer });
            outer.Entities.Add(new Circle(new Vector2(5, 5), 4) { Layer = layer });
            doc.Blocks.Add(outer);

            doc.AddEntity(new Insert(outer, new Vector2(50, 50)) { Layer = layer });
            doc.AddEntity(new Insert(outer, new Vector2(100, 50)) { Layer = layer, Rotation = 45 });

            // Justified text: all three anchor at the same X, so mis-handled alignment is
            // immediately visible as a horizontal stagger.
            doc.AddEntity(new Text("LEFT",   new Vector2(0, 100), 3.0) { Layer = layer, Alignment = TextAlignment.BaselineLeft });
            doc.AddEntity(new Text("CENTER", new Vector2(0, 110), 3.0) { Layer = layer, Alignment = TextAlignment.BaselineCenter });
            doc.AddEntity(new Text("RIGHT",  new Vector2(0, 120), 3.0) { Layer = layer, Alignment = TextAlignment.BaselineRight });

            doc.AddEntity(new MText("WRAPPED MTEXT THAT SHOULD BREAK ACROSS SEVERAL LINES",
                new Vector2(0, 140), 3.0, 30.0) { Layer = layer });

            // Bulge arcs both ways: a positive bulge must arc counter-clockwise in DXF
            // space and a negative one clockwise. Getting the sign wrong reflects the arc
            // to the far side of its circle -- the old "bowtie" artifact -- so both signs
            // and a >180-degree case are covered here deliberately.
            doc.AddEntity(new LwPolyline(new[]
            {
                new LwPolylineVertex(150, 0)  { Bulge =  0.5 },
                new LwPolylineVertex(180, 0)  { Bulge = -0.5 },
                new LwPolylineVertex(210, 0)  { Bulge =  1.0 },
                new LwPolylineVertex(240, 0)  { Bulge = -2.0 },
                new LwPolylineVertex(270, 0),
            }, false) { Layer = layer });

            // One line per stock linetype, so a regression that silently drops dashes is
            // visible as a block of identical solid lines.
            var linetypes = new[]
            {
                Linetype.Dashed, Linetype.Center, Linetype.Dot, Linetype.DashDot,
            };
            for (int i = 0; i < linetypes.Length; i++)
            {
                doc.Linetypes.Add(linetypes[i]);
                doc.AddEntity(new Line(new Vector2(150, 40 + i * 8), new Vector2(270, 40 + i * 8))
                    { Layer = layer, Linetype = linetypes[i], LinetypeScale = 4.0 });
            }

            doc.AddEntity(new Point(new Vector2(-10, -10)) { Layer = layer });
            doc.AddEntity(new Point(new Vector2(-20, -10)) { Layer = layer });

            doc.AddEntity(new LinearDimension(
                new Vector2(0, 0), new Vector2(40, 0), 8, 0) { Layer = layer });

            var boundary = new LwPolyline(new[]
            {
                new LwPolylineVertex(60, 100),
                new LwPolylineVertex(80, 100),
                new LwPolylineVertex(80, 120),
                new LwPolylineVertex(60, 120),
            }, true);
            doc.AddEntity(new Hatch(HatchPattern.Line,
                new[] { new HatchBoundaryPath(new EntityObject[] { boundary }) }, true) { Layer = layer });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            doc.Save(outPath);
            Console.WriteLine($"fixture written: {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fixture failed: {ex.Message}");
            return 1;
        }
    }
}
