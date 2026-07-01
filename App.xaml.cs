using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;

namespace DxfViewer;

public partial class App : Application
{
    private WinForms.NotifyIcon? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InitTrayIcon();
        if (MainWindow != null)
            MainWindow.Icon = CreateWpfIcon();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

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

        // Dark navy background (matches app tab strip)
        g.Clear(System.Drawing.Color.FromArgb(255, 18, 18, 30));

        // Panel outline — light gray, representing a PCB/panel outline
        using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(215, 215, 230), 1.6f))
            g.DrawRectangle(pen, 2.5f, 4f, 26f, 20f);

        // Mounting holes at four corners — accent blue (#4FA8E8)
        using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(79, 168, 232)))
        {
            g.FillEllipse(brush,  4f,  6f, 4.5f, 4.5f); // top-left
            g.FillEllipse(brush, 23f,  6f, 4.5f, 4.5f); // top-right
            g.FillEllipse(brush,  4f, 17f, 4.5f, 4.5f); // bottom-left
            g.FillEllipse(brush, 23f, 17f, 4.5f, 4.5f); // bottom-right
        }

        // Center horizontal groove line
        using (var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(110, 140, 200), 1f))
            g.DrawLine(pen, 9f, 14f, 22f, 14f);

        return bmp;
    }

    // Encodes as PNG-in-ICO (Windows Vista+) — no HICON resource leak.
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bmp = DrawIconBitmap();
        using var pngStream = new MemoryStream();
        bmp.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
        var pngBytes = pngStream.ToArray();

        // ICO: 6-byte header + 16-byte directory entry + PNG payload
        using var ico = new MemoryStream();
        using var bw  = new BinaryWriter(ico);
        bw.Write((short)0);             // reserved
        bw.Write((short)1);             // type = ICO
        bw.Write((short)1);             // image count
        bw.Write((byte)32);             // width
        bw.Write((byte)32);             // height
        bw.Write((byte)0);              // colour count
        bw.Write((byte)0);              // reserved
        bw.Write((short)1);             // colour planes
        bw.Write((short)32);            // bits per pixel
        bw.Write(pngBytes.Length);      // image data size
        bw.Write(22);                   // data offset (6 header + 16 dir = 22)
        bw.Write(pngBytes);
        bw.Flush();
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    // BitmapSource for the WPF window chrome / taskbar button.
    private static BitmapSource CreateWpfIcon()
    {
        using var bmp = DrawIconBitmap();
        using var ms  = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        return BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
