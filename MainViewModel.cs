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
    }

    private void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DXF Files (*.dxf)|*.dxf|All Files (*.*)|*.*",
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

    public void TryOpenFile(string path)
    {
        if (!File.Exists(path)) return;
        if (!path.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase)) return;

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
