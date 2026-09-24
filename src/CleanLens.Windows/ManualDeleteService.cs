using CleanLens.Core.Models;
using CleanLens.Core.Safety;

namespace CleanLens.Windows;

public sealed class ManualDeleteService
{
    private const int MaximumDirectoriesPerRoot = 20000;
    private const int MaximumDepth = 3;

    public Task<IReadOnlyList<string>> FindExactNameMatchesAsync(InstalledApplication application, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<string>>(() => FindExactNameMatches(application, cancellationToken), cancellationToken);

    public Task DeleteSelectedAsync(InstalledApplication application, IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var allowedRoots = GetPersonalRoots();
            var policy = new DeletionPathPolicy(allowedRoots);
            var validatedPaths = new List<string>();
            foreach (var path in selectedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!policy.TryValidate(path, out var canonicalPath, out _) || !IsExactProductFolder(canonicalPath, application.Name) || !Directory.Exists(canonicalPath))
                {
                    throw new IOException($"The selected path is outside the supported personal folders or no longer matches the application: {path}");
                }
                if (DeletionPathPolicy.ContainsReparsePointTree(canonicalPath))
                {
                    throw new IOException($"The selected folder contains a junction or symbolic link and was not deleted: {canonicalPath}");
                }
                validatedPaths.Add(canonicalPath);
            }
            foreach (var canonicalPath in validatedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteTreeWithoutFollowingLinks(canonicalPath, cancellationToken);
            }
        }, cancellationToken);

    private static IReadOnlyList<string> FindExactNameMatches(InstalledApplication application, CancellationToken cancellationToken)
    {
        var product = NormalizeToken(application.Name);
        if (product.Length < 3)
        {
            return [];
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in GetPersonalRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root) || DeletionPathPolicy.ContainsReparsePoint(root))
            {
                continue;
            }

            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            var visited = 0;
            while (pending.Count > 0 && visited < MaximumDirectoriesPerRoot)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (current, depth) = pending.Pop();
                if (depth >= MaximumDepth)
                {
                    continue;
                }
                string[] children;
                try
                {
                    children = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).Take(MaximumDirectoriesPerRoot - visited).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    visited++;
                    try
                    {
                        if (DeletionPathPolicy.ContainsReparsePoint(child))
                        {
                            continue;
                        }
                        if (NormalizeToken(Path.GetFileName(child)).Equals(product, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(Path.GetFullPath(child));
                            continue;
                        }
                        pending.Push((child, depth + 1));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                    {
                    }
                }
            }
        }
        return results.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] GetPersonalRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games")
    }.Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static bool IsExactProductFolder(string path, string applicationName) =>
        NormalizeToken(Path.GetFileName(path)).Equals(NormalizeToken(applicationName), StringComparison.OrdinalIgnoreCase);

    private static void DeleteTreeWithoutFollowingLinks(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The selected path became a junction or symbolic link: {path}");
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileAttributes = File.GetAttributes(file);
            if ((fileAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"The selected folder contains a junction or symbolic link: {file}");
            }
            File.Delete(file);
        }
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly))
        {
            DeleteTreeWithoutFollowingLinks(directory, cancellationToken);
        }
        Directory.Delete(path, false);
    }

    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());
}
