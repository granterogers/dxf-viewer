using SkiaSharp;

namespace DxfViewer;

// Single source of truth for the colors the Skia render paths use. The canvas background
// in particular was previously written out as a literal in both DxfTabControl and
// Program.cs, where the two had to be kept in sync by hand or the --render-test harness
// would quietly render on a different backdrop than the app -- exactly the kind of drift
// CLAUDE.md warns about. XAML reads the same values from App.xaml's token block.
internal static class Theme
{
    public static bool LightBackground;

    public static SKColor CanvasBackground =>
        LightBackground ? new SKColor(255, 255, 255) : new SKColor(18, 18, 30);

    // On a light background a near-white entity is invisible. CAD's long-standing rule is
    // that the "default" color inverts with the background rather than being drawn as-is,
    // so only near-white is flipped -- every deliberately colored entity is left alone.
    public static SKColor ForBackground(SKColor c)
    {
        if (!LightBackground) return c;
        return c.Red > 200 && c.Green > 200 && c.Blue > 200
            ? new SKColor(0, 0, 0, c.Alpha)
            : c;
    }
}
