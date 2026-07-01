using System.IO;
using System.Text.Json;

namespace DxfViewer;

internal static class AppSettings
{
    private static readonly string _file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DxfViewer", "settings.json");

    private static string? _lastOpenedDirectory;

    public static string? LastOpenedDirectory
    {
        get => _lastOpenedDirectory;
        set { _lastOpenedDirectory = value; Save(); }
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
            File.WriteAllText(_file, JsonSerializer.Serialize(dict));
        }
        catch { }
    }
}
