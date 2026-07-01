// dotnet script or use as-is with dotnet-script
#r "nuget: netDxf.netstandard, 2.4.0"

using netDxf;
using netDxf.Entities;
using netDxf.Tables;

var doc = new DxfDocument();

// Lines forming a box
doc.Entities.Add(new Line(new Vector3(0, 0, 0), new Vector3(100, 0, 0)));
doc.Entities.Add(new Line(new Vector3(100, 0, 0), new Vector3(100, 75, 0)));
doc.Entities.Add(new Line(new Vector3(100, 75, 0), new Vector3(0, 75, 0)));
doc.Entities.Add(new Line(new Vector3(0, 75, 0), new Vector3(0, 0, 0)));

// Circle in the middle
doc.Entities.Add(new Circle(new Vector3(50, 37.5, 0), 20));

// Arc
doc.Entities.Add(new Arc(new Vector3(50, 37.5, 0), 30, 0, 180));

// Text
var txt = new Text("DXF Viewer Test", new Vector3(5, 5, 0), 5);
doc.Entities.Add(txt);

// Diagonal cross lines
doc.Entities.Add(new Line(new Vector3(0, 0, 0), new Vector3(100, 75, 0)));
doc.Entities.Add(new Line(new Vector3(100, 0, 0), new Vector3(0, 75, 0)));

doc.Save("C:\\Users\\grant.rogers\\My Apps\\DEV\\DXF_Viewer\\samples\\test_generated.dxf");
Console.WriteLine("Saved test_generated.dxf");
