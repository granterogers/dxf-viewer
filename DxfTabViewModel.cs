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

    public DxfScene? Scene { get; private set; }

    private List<string> _dirFiles = new();
    private int _dirIndex = -1;
    public bool CanNavPrev => _dirFiles.Count > 1;
    public bool CanNavNext => _dirFiles.Count > 1;

    private readonly Action<DxfTabViewModel> _closeCallback;
    public Action? FitAction { get; set; }

    public ICommand NavPrevCommand { get; }
    public ICommand NavNextCommand { get; }

    public DxfTabViewModel(string filePath, Action<DxfTabViewModel> closeCallback)
    {
        FilePath = filePath;
        _closeCallback = closeCallback;
        NavPrevCommand = new RelayCommand(_ => NavigatePrev(), _ => CanNavPrev);
        NavNextCommand = new RelayCommand(_ => NavigateNext(), _ => CanNavNext);
        RefreshDirList();
    }

    private void RefreshDirList()
    {
        var dir = Path.GetDirectoryName(FilePath) ?? "";
        try
        {
            _dirFiles = Directory.GetFiles(dir, "*.dxf", SearchOption.TopDirectoryOnly)
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
        Scene = null;
        ErrorMessage = "";

        try
        {
            var scene = await Task.Run(() => DxfParser.Parse(path));
            Scene = scene;
            State = TabState.Loaded;
            FitAction?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = TabState.Error;
        }
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
