using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SkiaSharp;

namespace DxfViewer;

public class LayerInfo : INotifyPropertyChanged
{
    public string Name       { get; }
    public Brush  ColorBrush { get; }

    // How many scene primitives sit on this layer. Shown in the panel so an empty or
    // near-empty layer is obvious without toggling it off to find out.
    public int EntityCount { get; set; }

    private bool _isVisible;
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }

    public LayerInfo(string name, SKColor color, int entityCount = 0)
    {
        Name = name;
        EntityCount = entityCount;
        ColorBrush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        _isVisible = DefaultVisible(name);
    }

    // Every layer, including ROUTE_* (CNC toolpath/routing annotations), starts visible --
    // matching Microvellum's own viewer, which shows them by default. Still toggleable
    // from the layer panel. Kept as a hook (shared with Program.cs's --render-test
    // harness) in case a future layer category needs a different default.
    public static bool DefaultVisible(string name) => true;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
