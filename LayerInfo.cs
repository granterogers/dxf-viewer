using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SkiaSharp;

namespace DxfViewer;

public class LayerInfo : INotifyPropertyChanged
{
    public string Name       { get; }
    public Brush  ColorBrush { get; }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }

    public LayerInfo(string name, SKColor color)
    {
        Name = name;
        ColorBrush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
