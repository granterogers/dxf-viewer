using System.IO;
using System.Text.Json;

namespace DxfViewer;

internal static class AppSettings
{
    private static readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DxfViewer", "settings.json");

    private static string? _lastOpenedDirectory;
    private static string? _microvellumExePath;
    private static double _volume = 100;
    private static bool _muted;

    public static string? LastOpenedDirectory
    {
        get => _lastOpenedDirectory;
        set { _lastOpenedDirectory = value; Save(); }
    }

    public static string? MicrovellumExePath
    {
        get => _microvellumExePath;
        set { _microvellumExePath = value; Save(); }
    }

    public static double Volume
    {
        get => _volume;
        set { _volume = value; Save(); }
    }

    public static bool Muted
    {
        get => _muted;
        set { _muted = value; Save(); }
    }

    static AppSettings()
    {
        try
        {
            if (File.Exists(_file))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file));
                if (dict?.TryGetValue("LastOpenedDirectory", out var v) == true)
                    _lastOpenedDirectory = v;
                if (dict?.TryGetValue("MicrovellumExePath", out var mv) == true)
                    _microvellumExePath = mv;
                if (dict?.TryGetValue("Volume", out var vol) == true && double.TryParse(vol, out var volNum))
                    _volume = volNum;
                if (dict?.TryGetValue("Muted", out var m) == true && bool.TryParse(m, out var muted))
                    _muted = muted;
            }
        }
        catch { }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var dict = new Dictionary<string, string?>();
            if (_lastOpenedDirectory != null) dict["LastOpenedDirectory"] = _lastOpenedDirectory;
            if (_microvellumExePath != null) dict["MicrovellumExePath"] = _microvellumExePath;
            dict["Volume"] = _volume.ToString();
            dict["Muted"] = _muted.ToString();
            File.WriteAllText(_file, JsonSerializer.Serialize(dict));
        }
        catch { }
    }
}
