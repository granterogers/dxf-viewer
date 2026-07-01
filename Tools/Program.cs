using netDxf;
using netDxf.Entities;

var doc = new DxfDocument();

// Outer rectangle
doc.AddEntity(new Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
doc.AddEntity(new Line(new Vector3(100, 0, 0), new Vector3(100, 75, 0)));
doc.AddEntity(new Line(new Vector3(100, 75, 0), new Vector3(0, 75, 0)));
doc.AddEntity(new Line(new Vector3(0, 75, 0), new Vector3(0, 0, 0)));

// Circle centered in rectangle
doc.AddEntity(new Circle(new Vector3(50, 37.5, 0), 20));

// Arc (top half of a smaller circle)
doc.AddEntity(new Arc(new Vector3(50, 37.5, 0), 30, 0, 180));

// Diagonal cross
doc.AddEntity(new Line(new Vector3(0, 0, 0), new Vector3(100, 75, 0)));
doc.AddEntity(new Line(new Vector3(100, 0, 0), new Vector3(0, 75, 0)));

// Text label
doc.AddEntity(new Text("DXF Viewer Test", new Vector3(5, 5, 0), 4));

// LW polyline (5-pointed star shape, simplified as a polygon)
var lwp = new LwPolyline();
lwp.Vertexes.Add(new LwPolylineVertex(50, 70));
lwp.Vertexes.Add(new LwPolylineVertex(57, 52));
lwp.Vertexes.Add(new LwPolylineVertex(75, 52));
lwp.Vertexes.Add(new LwPolylineVertex(62, 42));
lwp.Vertexes.Add(new LwPolylineVertex(68, 24));
lwp.Vertexes.Add(new LwPolylineVertex(50, 33));
lwp.Vertexes.Add(new LwPolylineVertex(32, 24));
lwp.Vertexes.Add(new LwPolylineVertex(38, 42));
lwp.Vertexes.Add(new LwPolylineVertex(25, 52));
lwp.Vertexes.Add(new LwPolylineVertex(43, 52));
lwp.IsClosed = true;
doc.AddEntity(lwp);

var outPath = Path.Combine(
    @"C:\Users\grant.rogers\My Apps\DEV\DXF_Viewer\samples",
    "test_generated.dxf");

doc.Save(outPath);
Console.WriteLine($"Saved: {outPath}");
