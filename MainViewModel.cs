using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace DxfViewer;

public class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<DxfTabViewModel> Tabs { get; } = new();

    private DxfTabViewModel? _activeTab;
    public DxfTabViewModel? ActiveTab
    {
        get => _activeTab;
        set { _activeTab = value; OnPropertyChanged(); OnPropertyChanged(nameof(EmptyStateVisibility)); OnPropertyChanged(nameof(TabsVisibility)); }
    }

    public Visibility EmptyStateVisibility => Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TabsVisibility => Tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _alwaysOnTop;
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set { _alwaysOnTop = value; OnPropertyChanged(); }
    }

    public ICommand OpenFileCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand NextTabCommand { get; }
    public ICommand PrevTabCommand { get; }
    public ICommand FitCommand { get; }
    public ICommand ToggleAlwaysOnTopCommand { get; }
    public ICommand NavPrevCommand { get; }
    public ICommand NavNextCommand { get; }
    public ICommand OpenWithMicrovellumCommand { get; }
    public ICommand ToggleLightBackgroundCommand { get; }
    public ICommand ExportPngCommand { get; }
    public ICommand ToggleMeasureCommand { get; }

    public bool MeasureMode
    {
        get => ActiveTab?.MeasureMode == true;
        set { if (ActiveTab != null) { ActiveTab.MeasureMode = value; OnPropertyChanged(); } }
    }

    private bool _lightBackground = AppSettings.LightBackground;
    public bool LightBackground
    {
        get => _lightBackground;
        set
        {
            _lightBackground = value;
            Theme.LightBackground = value;
            AppSettings.LightBackground = value;
            OnPropertyChanged();
            ActiveTab?.RenderAction?.Invoke();
        }
    }

    public MainViewModel()
    {
        OpenFileCommand = new RelayCommand(_ => OpenFile());
        CloseTabCommand = new RelayCommand(p => CloseTab(p as DxfTabViewModel ?? ActiveTab));
        NextTabCommand = new RelayCommand(_ => CycleTab(1));
        PrevTabCommand = new RelayCommand(_ => CycleTab(-1));
        FitCommand = new RelayCommand(_ => ActiveTab?.FitToWindow());
        ToggleAlwaysOnTopCommand = new RelayCommand(_ => AlwaysOnTop = !AlwaysOnTop);
        NavPrevCommand = new RelayCommand(_ => ActiveTab?.NavigatePrev(), _ => ActiveTab != null);
        NavNextCommand = new RelayCommand(_ => ActiveTab?.NavigateNext(), _ => ActiveTab != null);
        OpenWithMicrovellumCommand = new RelayCommand(_ => OpenWithMicrovellum(), _ => ActiveTab?.IsLoaded == true);
        ToggleLightBackgroundCommand = new RelayCommand(_ => LightBackground = !LightBackground);
        ExportPngCommand = new RelayCommand(_ => ExportPng(), _ => ActiveTab?.IsLoaded == true);
        ToggleMeasureCommand = new RelayCommand(_ => MeasureMode = !MeasureMode, _ => ActiveTab?.IsLoaded == true);
        Theme.LightBackground = _lightBackground;
    }

    private void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "CAD Files (*.dxf;*.dwg)|*.dxf;*.dwg|DXF Files (*.dxf)|*.dxf|DWG Files (*.dwg)|*.dwg|All Files (*.*)|*.*",
            Multiselect = true
        };
        var last = AppSettings.LastOpenedDirectory;
        if (!string.IsNullOrEmpty(last) && Directory.Exists(last))
            dlg.InitialDirectory = last;
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
            TryOpenFile(f);
        var dir = Path.GetDirectoryName(dlg.FileNames[0]);
        if (!string.IsNullOrEmpty(dir)) AppSettings.LastOpenedDirectory = dir;
    }

    private void OpenWithMicrovellum()
    {
        var exePath = AppSettings.MicrovellumExePath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Locate Microvellum Application",
                Filter = "Executables (*.exe)|*.exe|All Files (*.*)|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            };
            if (dlg.ShowDialog() != true) return;
            exePath = dlg.FileName;
            AppSettings.MicrovellumExePath = exePath;
        }

        var filePath = ActiveTab?.FilePath;
        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo(exePath)
            {
                Arguments = $"\"{filePath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to launch Microvellum:\n{ex.Message}",
                "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // Renders the active page to a PNG at a fixed high resolution, independent of the
    // on-screen window size, so an exported image is usable as a reference rather than a
    // screenshot of whatever the window happened to be.
    public Action<string>? ExportRequested { get; set; }

    private void ExportPng()
    {
        var tab = ActiveTab;
        if (tab?.Scene == null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG Image (*.png)|*.png",
            FileName = Path.GetFileNameWithoutExtension(tab.FilePath) + ".png",
            InitialDirectory = AppSettings.LastOpenedDirectory ?? "",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            ExportRequested?.Invoke(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to export image:" + Environment.NewLine + ex.Message,
                "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void TryOpenFile(string path)
    {
        if (!File.Exists(path)) return;
        if (!path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) return;

        var existing = Tabs.FirstOrDefault(t => t.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ActiveTab = existing; return; }

        var tab = new DxfTabViewModel(path, CloseThisTab);
        Tabs.Add(tab);
        ActiveTab = tab;
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(TabsVisibility));
        tab.Load();
    }

    private void CloseThisTab(DxfTabViewModel tab) => CloseTab(tab);

    private void CloseTab(DxfTabViewModel? tab)
    {
        if (tab == null) return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        if (Tabs.Count > 0)
            ActiveTab = Tabs[Math.Max(0, Math.Min(idx, Tabs.Count - 1))];
        else
            ActiveTab = null;
        OnPropertyChanged(nameof(EmptyStateVisibility));
        OnPropertyChanged(nameof(TabsVisibility));
    }

    private void CycleTab(int dir)
    {
        if (Tabs.Count == 0) return;
        var idx = ActiveTab == null ? 0 : Tabs.IndexOf(ActiveTab);
        idx = (idx + dir + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[idx];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
