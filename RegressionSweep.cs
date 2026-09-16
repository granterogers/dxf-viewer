using System.IO;
using System.Text.Json;

namespace DxfViewer;

// Turns CLAUDE.md's manual "sweep all samples and diff the bounds output" ritual into one
// command that fails loudly. That sweep has caught real, widespread regressions twice, but
// only because someone remembered to eyeball a diff; this makes drift impossible to miss.
//
//   DxfViewer.exe --verify            compare every sample against the committed baseline
//   DxfViewer.exe --verify --update   re-bless the baseline after an intended change
internal static class RegressionSweep
{
    private sealed record Fingerprint(
        int Circles, int Arcs, int Lines, int Polylines, int Texts, int Wires3D,
        string Bounds, string Pages);

    public static int Run(bool update)
    {
        string root = FindSamplesRoot();
        if (root == null!)
        {
            Console.Error.WriteLine("samples/ directory not found; run from the repo root.");
            return 2;
        }

        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = new SortedDictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var file in files)
        {
            string key = Path.GetRelativePath(Directory.GetCurrentDirectory(), file).Replace('\\', '/');
            try
            {
                var pages = file.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)
                    ? DwgParser.Parse(file)
                    : DxfParser.Parse(file);
                var scene = pages[0].Scene;
                var b = scene.Bounds;
                current[key] = new Fingerprint(
                    scene.Circles.Count, scene.Arcs.Count, scene.Lines.Count,
                    scene.Polylines.Count, scene.Texts.Count, scene.Wires3D.Count,
                    $"{b.Left:F1},{b.Top:F1},{b.Right:F1},{b.Bottom:F1}",
                    string.Join(",", pages.Select(p => p.Name)));
            }
            catch (Exception ex)
            {
                failures.Add($"{key}: {ex.Message}");
            }
        }

        string baselineFile = Path.GetFullPath(Path.Combine(root, "baseline.json"));

        if (update)
        {
            File.WriteAllText(baselineFile,
                JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"baseline updated: {current.Count} files, {failures.Count} parse failures");
            foreach (var f in failures) Console.WriteLine("  FAILED " + f);
            return failures.Count == 0 ? 0 : 1;
        }

        if (!File.Exists(baselineFile))
        {
            Console.Error.WriteLine($"No baseline at {baselineFile}. Run: --verify --update");
            return 2;
        }

        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, Fingerprint>>(
            File.ReadAllText(baselineFile)) ?? new();

        var drift = new List<string>();
        foreach (var (key, now) in current)
        {
            if (!baseline.TryGetValue(key, out var was)) { drift.Add($"NEW      {key}"); continue; }
            if (was != now)
                drift.Add($"CHANGED  {key}\n           was {was}\n           now {now}");
        }
        foreach (var key in baseline.Keys)
            if (!current.ContainsKey(key)) drift.Add($"MISSING  {key}");

        Console.WriteLine($"swept {current.Count} files  ·  {failures.Count} parse failures  ·  {drift.Count} changed");
        foreach (var f in failures) Console.WriteLine("  FAILED   " + f);
        foreach (var d in drift)    Console.WriteLine("  " + d);

        return failures.Count == 0 && drift.Count == 0 ? 0 : 1;
    }

    private static string FindSamplesRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null!;
    }
}
