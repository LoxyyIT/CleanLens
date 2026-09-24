using CleanLens.Core.Models;
using CleanLens.Core.Safety;

namespace CleanLens.Windows;

public sealed record ManualDeleteCandidate(string Path, string Source);

public sealed class ManualDeleteService
{
    public Task<IReadOnlyList<ManualDeleteCandidate>> FindExactNameMatchesAsync(InstalledApplication application, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ManualDeleteCandidate>>(() => FindExactNameMatches(application, cancellationToken), cancellationToken);

    public Task DeleteSelectedAsync(InstalledApplication application, IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var policy = CreatePolicy();
            var validatedPaths = new List<string>();
            foreach (var path in selectedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!policy.TryValidate(path, out var canonicalPath, out _) || !IsKnownCandidate(canonicalPath, application) || !Directory.Exists(canonicalPath))
                {
                    throw new IOException($"The selected path is outside supported application or personal-data folders: {path}");
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

    private static IReadOnlyList<ManualDeleteCandidate> FindExactNameMatches(InstalledApplication application, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var productTokens = GetProductTokens(application);
        if (productTokens.Count == 0)
        {
            return [];
        }

        AddRegisteredLocation(application.InstallLocation, "Windows registered install location", application, results);
        AddExecutableLocation(application.DisplayIcon, "Windows registered app icon folder", application, productTokens, results);
        AddExecutableLocation(application.UninstallCommand, "Folder containing the Windows registered uninstaller command", application, productTokens, results, requireProductName: false);

        foreach (var root in GetApplicationDataRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddApplicationDataMatches(root, application.Publisher, productTokens, results);
        }

        foreach (var root in GetPersonalRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanPersonalTree(root, productTokens, results, cancellationToken);
        }

        return results.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ManualDeleteCandidate(pair.Key, pair.Value))
            .ToArray();
    }

    private static void AddRegisteredLocation(string path, string source, InstalledApplication application, IDictionary<string, string> results)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            var canonicalPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (Directory.Exists(canonicalPath) && IsAllowedCandidatePath(canonicalPath) && IsSafeDirectory(canonicalPath))
            {
                AddCandidate(canonicalPath, source, results);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void AddExecutableLocation(string commandOrIcon, string source, InstalledApplication application, IReadOnlySet<string> productTokens, IDictionary<string, string> results, bool requireProductName = true)
    {
        var executable = ResolveExecutablePath(commandOrIcon);
        if (executable is null)
        {
            return;
        }
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        var folderToken = NormalizeToken(Path.GetFileName(directory));
        if (!requireProductName || productTokens.Contains(folderToken) || SamePath(directory, application.InstallLocation))
        {
            AddRegisteredLocation(directory, source, application, results);
        }
    }

    private static void AddApplicationDataMatches(string root, string publisher, IReadOnlySet<string> productTokens, IDictionary<string, string> results)
    {
        if (!Directory.Exists(root) || !IsSafeDirectory(root))
        {
            return;
        }

        var publisherToken = NormalizeToken(publisher);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsSafeDirectory(folder))
                {
                    continue;
                }
                var folderToken = NormalizeToken(Path.GetFileName(folder));
                if (productTokens.Contains(folderToken))
                {
                    AddCandidate(folder, "Exact app-name folder in an application-data root", results);
                    continue;
                }
                if (publisherToken.Length < 3 || !folderToken.Equals(publisherToken, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                foreach (var productFolder in Directory.EnumerateDirectories(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsSafeDirectory(productFolder) && productTokens.Contains(NormalizeToken(Path.GetFileName(productFolder))))
                    {
                        AddCandidate(productFolder, "Exact publisher/product folders in an application-data root", results);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
        }
    }

    private static void ScanPersonalTree(string root, IReadOnlySet<string> productTokens, IDictionary<string, string> results, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root) || !IsSafeDirectory(root))
        {
            return;
        }
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            try
            {
                foreach (var child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!IsSafeDirectory(child))
                        {
                            continue;
                        }
                        if (productTokens.Contains(NormalizeToken(Path.GetFileName(child))))
                        {
                            AddCandidate(child, "Exact registered app-name folder in a personal library", results);
                            continue;
                        }
                        pending.Push(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
            }
        }
    }

    private static IReadOnlySet<string> GetProductTokens(InstalledApplication application)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddToken(application.Name);
        AddToken(Path.GetFileName(application.InstallLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var iconPath = ResolveExecutablePath(application.DisplayIcon);
        if (iconPath is not null)
        {
            AddToken(Path.GetFileNameWithoutExtension(iconPath));
        }
        return tokens;

        void AddToken(string value)
        {
            var token = NormalizeToken(value);
            if (token.Length >= 3 && !IsGenericFolderToken(token))
            {
                tokens.Add(token);
            }
        }
    }

    private static bool IsKnownCandidate(string path, InstalledApplication application)
    {
        var productTokens = GetProductTokens(application);
        if (SamePath(path, application.InstallLocation) || IsRegisteredExecutableParent(path, application.DisplayIcon, application, productTokens) || IsRegisteredExecutableParent(path, application.UninstallCommand, application, productTokens, requireProductName: false))
        {
            return IsAllowedCandidatePath(path);
        }
        if (GetApplicationDataRoots().Any(root => DeletionPathPolicy.IsPathWithin(path, root)) || GetPersonalRoots().Any(root => DeletionPathPolicy.IsPathWithin(path, root)))
        {
            return GetProductTokens(application).Contains(NormalizeToken(Path.GetFileName(path)));
        }
        return false;
    }

    private static bool IsAllowedCandidatePath(string path)
    {
        var policy = CreatePolicy();
        return policy.TryValidate(path, out _, out _) && !IsProtectedWindowsPath(path);
    }

    private static bool IsRegisteredExecutableParent(string path, string commandOrIcon, InstalledApplication application, IReadOnlySet<string> productTokens, bool requireProductName = true)
    {
        var executable = ResolveExecutablePath(commandOrIcon);
        if (executable is null)
        {
            return false;
        }
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        return SamePath(path, directory) && (!requireProductName || productTokens.Contains(NormalizeToken(Path.GetFileName(directory))) || SamePath(directory, application.InstallLocation));
    }

    private static string? ResolveExecutablePath(string commandOrIcon)
    {
        if (string.IsNullOrWhiteSpace(commandOrIcon))
        {
            return null;
        }
        var value = Environment.ExpandEnvironmentVariables(commandOrIcon.Trim());
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end <= 1)
            {
                return null;
            }
            value = value[1..end];
        }
        else
        {
            var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (executableEnd >= 0)
            {
                value = value[..(executableEnd + 4)];
            }
            else
            {
                var comma = value.LastIndexOf(',');
                if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
                {
                    value = value[..comma].Trim();
                }
            }
        }
        try
        {
            var path = Path.GetFullPath(value);
            return !path.StartsWith("\\\\", StringComparison.Ordinal) && File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static DeletionPathPolicy CreatePolicy() => new(GetAllowedRoots(), [Environment.GetFolderPath(Environment.SpecialFolder.Windows)]);

    private static string[] GetAllowedRoots() => GetPersonalRoots()
        .Concat(GetApplicationDataRoots())
        .Concat([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)])
        .Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string[] GetApplicationDataRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
    }.Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

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

    private static void AddCandidate(string path, string source, IDictionary<string, string> results)
    {
        var canonicalPath = Path.GetFullPath(path);
        if (!results.ContainsKey(canonicalPath))
        {
            results.Add(canonicalPath, source);
        }
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return Path.GetFullPath(left.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(Path.GetFullPath(right.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsProtectedWindowsPath(string path)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(windows) && DeletionPathPolicy.IsPathWithin(path, windows);
    }

    private static bool IsGenericFolderToken(string token) => token is "BIN" or "X64" or "X86" or "WIN32" or "COMMON" or "PROGRAM";

    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private static void DeleteTreeWithoutFollowingLinks(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The selected path became a junction or symbolic link: {path}");
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
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
}
