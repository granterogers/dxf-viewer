using System.Windows;
using System.Windows.Input;

using System.Runtime.InteropServices;

namespace DxfViewer;

public partial class MainWindow : Window
{
    // Without this the system paints a light caption bar directly above the app's dark UI.
    // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on current Windows 10/11; 19 was the pre-20H1
    // value, so both are attempted and failures are ignored on older builds.
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { }
    }

    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppVersion.Full;
        _vm = new MainViewModel();
        DataContext = _vm;

        Drop += OnDrop;
        DragOver += OnDragOver;

        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Loaded += (_, _) => Focus();

        // The export renders through the live tab control, which owns the scene picture
        // and camera state; the view model only decides where the file goes.
        _vm.ExportRequested = path =>
        {
            var tabControl = FindTabControl(this);
            tabControl?.ExportPng(path);
        };
    }

    private static DxfTabControl? FindTabControl(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is DxfTabControl tc) return tc;
            var found = FindTabControl(child);
            if (found != null) return found;
        }
        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var path in paths)
            _vm.TryOpenFile(path);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F)
        {
            _vm.ActiveTab?.FitToWindow();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.ActiveTab?.NavigatePrev();
            e.Handled = true;
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.None)
        {
            _vm.ActiveTab?.NavigateNext();
            e.Handled = true;
        }
    }
}
