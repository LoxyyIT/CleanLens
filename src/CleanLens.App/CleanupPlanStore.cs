using System.IO;
using System.Text.Json;
using CleanLens.Windows;

namespace CleanLens.App;

public sealed record CleanupPlanItem(string Path, long? SizeBytes, bool IsDirectory, string Risk);

public sealed record CleanupPlan(string Id, string Name, string RootPath, DateTimeOffset CreatedAt, IReadOnlyList<CleanupPlanItem> Items)
{
    public string Summary => $"{CreatedAt.ToLocalTime():g} · {Items.Count} items · {RootPath}";
    public string SizeText => DiskSizeFormatter.Format(Items.Sum(item => item.SizeBytes ?? 0));
}

internal static class DiskSizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)Math.Max(0, bytes);
        var index = 0;
        while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
        return $"{size:0.#} {units[index]}";
    }
}

public sealed class CleanupPlanStore
{
    private readonly string directory;

    public CleanupPlanStore(string localDataPath)
    {
        directory = Path.Combine(localDataPath, "CleanupPlans");
        Directory.CreateDirectory(directory);
    }

    public CleanupPlan Save(string name, DiskScanSummary summary, IReadOnlyList<DiskScanEntry> entries)
    {
        if (entries.Count == 0) throw new InvalidOperationException("Select at least one item.");
        var items = entries.GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(item => new CleanupPlanItem(item.Path, item.SizeBytes, item.IsDirectory, GetRisk(item.Path, summary.RootPath))).ToArray();
        if (items.Any(item => !IsWithin(item.Path, summary.RootPath))) throw new InvalidOperationException("A selected path is outside the scan root.");
        var plan = new CleanupPlan(Guid.NewGuid().ToString("N"), name.Trim(), summary.RootPath, DateTimeOffset.UtcNow, items);
        var path = Path.Combine(directory, plan.Id + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan));
        File.Move(temporary, path);
        return plan;
    }

    public IReadOnlyList<CleanupPlan> Load()
    {
        var plans = new List<CleanupPlan>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var plan = JsonSerializer.Deserialize<CleanupPlan>(File.ReadAllText(path));
                if (plan is not null && Guid.TryParseExact(plan.Id, "N", out _) &&
                    Path.GetFileNameWithoutExtension(path).Equals(plan.Id, StringComparison.OrdinalIgnoreCase) &&
                    plan.Items.All(item => IsWithin(item.Path, plan.RootPath))) plans.Add(plan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        }
        return plans.OrderByDescending(plan => plan.CreatedAt).ToArray();
    }

    public void Delete(CleanupPlan plan)
    {
        if (!Guid.TryParseExact(plan.Id, "N", out _)) throw new ArgumentException("Invalid plan id.");
        var path = Path.Combine(directory, plan.Id + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRisk(string path, string root)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "Critical · Windows";
        if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return "High · Program files";
        if (Directory.Exists(path)) return "Review · Folder contents";
        return "Review · File";
    }
}
