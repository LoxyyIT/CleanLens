using CleanLens.Core.Models;
using CleanLens.Core.Safety;

namespace CleanLens.Windows;

public sealed class LeftoverScanner
{
    private readonly string[] roots;

    public LeftoverScanner(IEnumerable<string>? roots = null)
    {
        this.roots = (roots ??
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        ]).Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<IReadOnlyList<LeftoverCandidate>> ScanAsync(InstalledApplication application, CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<LeftoverCandidate>>(() =>
        {
            var publisher = NormalizeToken(application.Publisher);
            var product = NormalizeToken(application.Name);
            if (publisher.Length < 3 || product.Length < 3)
            {
                return [];
            }

            var found = new List<LeftoverCandidate>();
            foreach (var root in roots.Where(IsSafeDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IEnumerable<string> publishers;
                try
                {
                    publishers = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    continue;
                }

                foreach (var publisherPath in publishers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (NormalizeToken(Path.GetFileName(publisherPath)) != publisher || !IsSafeDirectory(publisherPath))
                    {
                        continue;
                    }

                    IEnumerable<string> products;
                    try
                    {
                        products = Directory.EnumerateDirectories(publisherPath, "*", SearchOption.TopDirectoryOnly).ToArray();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        continue;
                    }

                    foreach (var path in products)
                    {
                        if (NormalizeToken(Path.GetFileName(path)) != product || !IsSafeDirectory(path))
                        {
                            continue;
                        }

                        var size = MeasureTopLevel(path, cancellationToken);
                        found.Add(new LeftoverCandidate(
                            path,
                            root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase) ? CandidateCategory.ProgramData : CandidateCategory.ApplicationData,
                            ConfidenceScorer.Score(2, exactProductIdentity: true, isUserData: false),
                            "Exact publisher and product directory components match the registered application identity under a standard application-data root.",
                            size,
                            false,
                            false));
                    }
                }
            }

            return found;
        }, cancellationToken);
    }

    private static long MeasureTopLevel(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current, "*", SearchOption.TopDirectoryOnly).ToArray();
                directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    {
                        total = checked(total + new FileInfo(file).Length);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OverflowException)
                {
                }
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(directory);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }

        return total;
    }

    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && !DeletionPathPolicy.ContainsReparsePoint(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }
}
