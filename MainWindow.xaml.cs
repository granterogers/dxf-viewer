using System.Windows;
using System.Windows.Input;

namespace DxfViewer;

public partial class MainWindow : Window
{
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
