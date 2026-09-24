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
            if (product.Length < 3)
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

                var candidatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in publishers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsSafeDirectory(path))
                    {
                        continue;
                    }

                    var folderName = NormalizeToken(Path.GetFileName(path));
                    if (folderName.Equals(product, StringComparison.OrdinalIgnoreCase))
                    {
                        candidatePaths.Add(path);
                    }

                    if (publisher.Length < 3 || !folderName.Equals(publisher, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    IEnumerable<string> products;
                    try
                    {
                        products = Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).ToArray();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        continue;
                    }
                    foreach (var productPath in products)
                    {
                        if (NormalizeToken(Path.GetFileName(productPath)) == product && IsSafeDirectory(productPath))
                        {
                            candidatePaths.Add(productPath);
                        }
                    }
                }

                foreach (var path in candidatePaths)
                {
                    var publisherFolderMatches = NormalizeToken(Path.GetFileName(Path.GetDirectoryName(path)!)).Equals(publisher, StringComparison.OrdinalIgnoreCase);
                    var reason = publisherFolderMatches
                        ? "Exact publisher and product directory components match the registered application identity under a standard application-data root."
                        : "Exact product folder under a standard application-data root after the registered uninstall entry disappeared. Review the path before moving it.";
                    var size = MeasureTopLevel(path, cancellationToken);
                    found.Add(new LeftoverCandidate(
                        path,
                        root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StringComparison.OrdinalIgnoreCase) ? CandidateCategory.ProgramData : CandidateCategory.ApplicationData,
                        ConfidenceScorer.Score(2, exactProductIdentity: true, isUserData: false),
                        reason,
                        size,
                        false,
                        false));
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
