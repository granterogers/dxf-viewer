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
    private static bool _lightBackground;
    private static UnitSystem _unitSystem = UnitSystem.AsDrawn;
    private static bool _iconsOnlyToolbar;

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

    public static bool LightBackground
    {
        get => _lightBackground;
        set { _lightBackground = value; Save(); }
    }

    public static UnitSystem UnitSystem
    {
        get => _unitSystem;
        set { _unitSystem = value; Save(); }
    }

    public static bool IconsOnlyToolbar
    {
        get => _iconsOnlyToolbar;
        set { _iconsOnlyToolbar = value; Save(); }
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
                if (dict?.TryGetValue("LightBackground", out var lb) == true && bool.TryParse(lb, out var lbv))
                    _lightBackground = lbv;
                if (dict?.TryGetValue("UnitSystem", out var us) == true && Enum.TryParse<UnitSystem>(us, out var usv))
                    _unitSystem = usv;
                if (dict?.TryGetValue("IconsOnlyToolbar", out var io) == true && bool.TryParse(io, out var iov))
                    _iconsOnlyToolbar = iov;
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
            dict["LightBackground"] = _lightBackground.ToString();
            dict["UnitSystem"] = _unitSystem.ToString();
            dict["IconsOnlyToolbar"] = _iconsOnlyToolbar.ToString();
            File.WriteAllText(_file, JsonSerializer.Serialize(dict));
        }
        catch { }
    }
}
