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
            File.WriteAllText(_file, JsonSerializer.Serialize(dict));
        }
        catch { }
    }
}
