using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace DxfViewer;

public partial class App : Application
{
    private WinForms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterFileAssociation();
        InitTrayIcon();
        if (MainWindow != null)
            MainWindow.Icon = CreateWpfIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    // ─── File association + icon registration ────────────────────────────────

    private static void RegisterFileAssociation()
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? "";
            if (string.IsNullOrEmpty(exePath)) return;

            const string progId = "DxfViewer.DXF.1";
            // DefaultIcon: no extra quotes needed — shell splits on trailing ",index"
            var iconVal   = $"{exePath},0";
            var cmdVal    = $"\"{exePath}\" \"%1\"";

            // Applications\DxfViewer.exe keys — Windows reads these for the
            // "Open with" list icon AND for UserChoice-based associations
            // (when user picked "Always use this app", Windows sets UserChoice
            // ProgId = "Applications\DxfViewer.exe", so DefaultIcon here is key)
            SetValue(@"Software\Classes\Applications\DxfViewer.exe\DefaultIcon",        iconVal);
            SetValue(@"Software\Classes\Applications\DxfViewer.exe\shell\open\command", cmdVal);
            SetValue(@"Software\Classes\Applications\DxfViewer.exe\SupportedTypes",     ""); // lets .dxf show in Open With
            using (var st = Registry.CurrentUser.CreateSubKey(
                       @"Software\Classes\Applications\DxfViewer.exe\SupportedTypes"))
                st.SetValue(".dxf", "");

            // ProgID — used when the extension is explicitly mapped to us via
            // HKCU\Software\Classes\.dxf (default) = progId
            SetValue($@"Software\Classes\{progId}",                        "DXF Drawing");
            SetValue($@"Software\Classes\{progId}\DefaultIcon",             iconVal);
            SetValue($@"Software\Classes\{progId}\shell\open\command",      cmdVal);

            // Register .dxf → our ProgID in OpenWithProgids so we always appear
            // in the "Open with" list with the right icon, without forcing ownership
            using (var owp = Registry.CurrentUser.CreateSubKey(
                       @"Software\Classes\.dxf\OpenWithProgids"))
                owp.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.Binary);

            // Take ownership of .dxf only if it isn't already claimed by another app
            using var extKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.dxf", false);
            var current = extKey?.GetValue("") as string ?? "";
            if (string.IsNullOrEmpty(current) || current == progId)
                SetValue(@"Software\Classes\.dxf", progId);

            // Tell the shell to refresh all file-type icons and the Open-With list
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */,
                           0x0000     /* SHCNF_IDLIST */,
                           nint.Zero, nint.Zero);
        }
        catch { }
    }

    private static void SetValue(string subKey, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue("", value);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, nint dwItem1, nint dwItem2);

    // ─── Tray icon ────────────────────────────────────────────────────────────

    private void InitTrayIcon()
    {
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = AppVersion.Full,
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(new WinForms.ToolStripMenuItem(AppVersion.Full) { Enabled = false });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Open File…", null, (_, _) =>
        {
            RestoreMainWindow();
            (MainWindow?.DataContext as MainViewModel)?.OpenFileCommand.Execute(null);
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => RestoreMainWindow();
    }

    private void RestoreMainWindow()
    {
        var win = MainWindow;
        if (win == null) return;
        win.Show();
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;
        win.Activate();
    }

    // ─── Icon drawing ────────────────────────────────────────────────────────

    private static System.Drawing.Bitmap DrawIconBitmap()
    {
        var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode   = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        g.Clear(System.Drawing.Color.FromArgb(255, 10, 10, 24));

        using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0, 55, 110)))
            g.FillRectangle(b, 3f, 2f, 26f, 23f);

        using var cPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(230, 255, 255, 255), 1.2f);
        g.DrawLine(cPen,  3f,  7f,  3f,  2f); g.DrawLine(cPen,  3f,  2f,  8f,  2f);
        g.DrawLine(cPen, 21f,  2f, 29f,  2f); g.DrawLine(cPen, 29f,  2f, 29f,  7f);
        g.DrawLine(cPen,  3f, 18f,  3f, 25f); g.DrawLine(cPen,  3f, 25f,  8f, 25f);
        g.DrawLine(cPen, 21f, 25f, 29f, 25f); g.DrawLine(cPen, 29f, 18f, 29f, 25f);

        using var dPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(80, 255, 255, 255), 0.7f);
        g.DrawLine(dPen,  5f, 13.5f, 27f, 13.5f);
        g.DrawLine(dPen, 16f,  4f,   16f, 23f);

        using var oPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(255, 224, 118, 24), 1.4f);
        g.DrawEllipse(oPen, 9f, 7f, 14f, 13f);

        using (var b = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 224, 118, 24)))
            g.FillRectangle(b, 3f, 27f, 26f, 4f);

        return bmp;
    }

    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = DrawIconBitmap();
        using var pngStream = new MemoryStream();
        bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
        var pngBytes = pngStream.ToArray();

        using var ico = new MemoryStream();
        using var bw  = new BinaryWriter(ico);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)1);
        bw.Write((byte)32); bw.Write((byte)32); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((short)1); bw.Write((short)32);
        bw.Write(pngBytes.Length);
        bw.Write(22);
        bw.Write(pngBytes);
        bw.Flush();
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    private static BitmapSource CreateWpfIcon()
    {
        using var bmp = DrawIconBitmap();
        using var ms  = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
