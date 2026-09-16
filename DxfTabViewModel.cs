using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DxfViewer;

public enum TabState { Empty, Loading, Loaded, Error }

public class DxfTabViewModel : INotifyPropertyChanged
{
    public string FilePath { get; private set; }
    public string Title => Path.GetFileName(FilePath);

    private TabState _state = TabState.Empty;
    public TabState State
    {
        get => _state;
        private set
        {
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(IsError));
        }
    }

    public bool IsLoading => _state == TabState.Loading;
    public bool IsLoaded  => _state == TabState.Loaded;
    public bool IsError   => _state == TabState.Error;

    private string _errorMessage = "";
    public string ErrorMessage
    {
        get => _errorMessage;
        private set { _errorMessage = value; OnPropertyChanged(); }
    }

    public List<DxfPage> Pages { get; private set; } = new();
    public System.Collections.ObjectModel.ObservableCollection<string> PageNames { get; } = new();
    public bool HasMultiplePages => Pages.Count > 1;

    private int _currentPageIndex;
    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            if (Pages.Count == 0) return;
            var clamped = Math.Clamp(value, 0, Pages.Count - 1);
            if (clamped == _currentPageIndex) return;
            _currentPageIndex = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Scene));
            OnPropertyChanged(nameof(IsScene3D));
            OnPropertyChanged(nameof(SceneReadout));
            RefreshLayersFromScene();
            FitAction?.Invoke();
            RenderAction?.Invoke();
        }
    }

    public DxfScene? Scene => _currentPageIndex >= 0 && _currentPageIndex < Pages.Count
        ? Pages[_currentPageIndex].Scene : null;
    public bool IsScene3D => Scene?.Is3D == true;

    public System.Collections.ObjectModel.ObservableCollection<LayerInfo> Layers { get; } = new();
    public Action? RenderAction { get; set; }

    private string _cursorReadout = "";
    public string CursorReadout
    {
        get => _cursorReadout;
        set { _cursorReadout = value; OnPropertyChanged(); }
    }

    private string _zoomReadout = "";
    public string ZoomReadout
    {
        get => _zoomReadout;
        set { _zoomReadout = value; OnPropertyChanged(); }
    }

    public string SceneReadout => Scene == null ? "" : Scene.DiagnosticsSummary;

    private List<string> _dirFiles = new();
    private int _dirIndex = -1;
    public bool CanNavPrev => _dirFiles.Count > 1;
    public bool CanNavNext => _dirFiles.Count > 1;

    private readonly Action<DxfTabViewModel> _closeCallback;
    public Action? FitAction { get; set; }

    public ICommand NavPrevCommand { get; }
    public ICommand NavNextCommand { get; }
    public ICommand AllLayersOnCommand  { get; }
    public ICommand AllLayersOffCommand { get; }
    public ICommand SoloLayerCommand    { get; }

    public DxfTabViewModel(string filePath, Action<DxfTabViewModel> closeCallback)
    {
        FilePath = filePath;
        _closeCallback = closeCallback;
        NavPrevCommand = new RelayCommand(_ => NavigatePrev(), _ => CanNavPrev);
        NavNextCommand = new RelayCommand(_ => NavigateNext(), _ => CanNavNext);
        AllLayersOnCommand  = new RelayCommand(_ => { foreach (var l in Layers) l.IsVisible = true; });
        AllLayersOffCommand = new RelayCommand(_ => { foreach (var l in Layers) l.IsVisible = false; });
        SoloLayerCommand    = new RelayCommand(p => { if (p is LayerInfo li) SoloLayer(li); });
        RefreshDirList();
    }

    private void RefreshDirList()
    {
        var dir = Path.GetDirectoryName(FilePath) ?? "";
        try
        {
            _dirFiles = Directory.GetFiles(dir, "*.dxf", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(dir, "*.dwg", SearchOption.TopDirectoryOnly))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch { _dirFiles = new(); }
        _dirIndex = _dirFiles.IndexOf(FilePath);
        OnPropertyChanged(nameof(CanNavPrev));
        OnPropertyChanged(nameof(CanNavNext));
    }

    public void Load() => LoadFile(FilePath);

    private async void LoadFile(string path)
    {
        State = TabState.Loading;
        Pages = new();
        _currentPageIndex = 0;
        ErrorMessage = "";

        try
        {
            var pages = await Task.Run(() => path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                ? DwgParser.Parse(path)
                : DxfParser.Parse(path));
            Pages = pages;
            _currentPageIndex = 0;
            PageNames.Clear();
            foreach (var p in pages) PageNames.Add(p.Name);
            OnPropertyChanged(nameof(Pages));
            OnPropertyChanged(nameof(HasMultiplePages));
            OnPropertyChanged(nameof(CurrentPageIndex));
            OnPropertyChanged(nameof(Scene));
            OnPropertyChanged(nameof(IsScene3D));
            RefreshLayersFromScene();
            OnPropertyChanged(nameof(SceneReadout));
            State = TabState.Loaded;
            FitAction?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = TabState.Error;
        }
    }

    private void RefreshLayersFromScene()
    {
        Layers.Clear();
        if (Scene == null) return;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Bump(string l) => counts[l] = counts.TryGetValue(l, out var n) ? n + 1 : 1;
        foreach (var c in Scene.Circles)   Bump(c.Layer);
        foreach (var a in Scene.Arcs)      Bump(a.Layer);
        foreach (var l in Scene.Lines)     Bump(l.Layer);
        foreach (var pl in Scene.Polylines) Bump(pl.Layer);
        foreach (var t in Scene.Texts)     Bump(t.Layer);
        foreach (var w in Scene.Wires3D)   Bump(w.Layer);

        foreach (var (name, color) in Scene.Layers)
        {
            var info = new LayerInfo(name, color, counts.TryGetValue(name, out var n) ? n : 0);
            info.PropertyChanged += (_, _) => RenderAction?.Invoke();
            Layers.Add(info);
        }
    }

    // Show only this layer -- the "solo"/isolate action every CAD layer manager has.
    // Clicking solo on the only visible layer restores all of them, so it round-trips.
    public void SoloLayer(LayerInfo target)
    {
        bool alreadySolo = Layers.All(l => l.IsVisible == ReferenceEquals(l, target));
        foreach (var l in Layers) l.IsVisible = alreadySolo || ReferenceEquals(l, target);
    }

    public void FitToWindow() => FitAction?.Invoke();

    public void NavigatePrev()
    {
        if (_dirFiles.Count <= 1) return;
        _dirIndex = (_dirIndex - 1 + _dirFiles.Count) % _dirFiles.Count;
        NavigateTo(_dirFiles[_dirIndex]);
    }

    public void NavigateNext()
    {
        if (_dirFiles.Count <= 1) return;
        _dirIndex = (_dirIndex + 1) % _dirFiles.Count;
        NavigateTo(_dirFiles[_dirIndex]);
    }

    private void NavigateTo(string path)
    {
        if (!File.Exists(path))
        {
            ErrorMessage = $"File not found: {Path.GetFileName(path)}";
            State = TabState.Error;
            return;
        }
        FilePath = path;
        OnPropertyChanged(nameof(FilePath));
        OnPropertyChanged(nameof(Title));
        RefreshDirList();
        LoadFile(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
