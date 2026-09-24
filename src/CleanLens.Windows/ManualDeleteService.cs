using CleanLens.Core.Models;
using CleanLens.Core.Safety;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CleanLens.Windows;

public sealed record ManualDeleteCandidate(string Path, string Source, bool IsDirectory);

public sealed class ManualDeleteService
{
    private static readonly HashSet<string> SharedLauncherTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "STEAM", "EPICGAMESLAUNCHER", "UBISOFTCONNECT", "EAAPP", "EADESKTOP", "BATTLENET", "GOGGALAXY",
        "RIOTCLIENTSERVICES", "RIOTCLIENTUX", "ROCKSTARLAUNCHER", "ROCKSTARGAMESLAUNCHER", "XBOXPCAPP"
    };

    public Task<IReadOnlyList<ManualDeleteCandidate>> FindExactNameMatchesAsync(InstalledApplication application, CancellationToken cancellationToken = default, IProgress<int>? progress = null) =>
        Task.Run<IReadOnlyList<ManualDeleteCandidate>>(() => FindExactNameMatches(application, cancellationToken, progress), cancellationToken);

    public Task DeleteSelectedAsync(InstalledApplication application, IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var policy = CreatePolicy(application);
            var validatedPaths = new List<string>();
            foreach (var path in selectedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!policy.TryValidate(path, out var canonicalPath, out _) || !IsKnownCandidate(canonicalPath, application) || (!Directory.Exists(canonicalPath) && !File.Exists(canonicalPath)))
                {
                    throw new IOException($"The selected path is outside supported application or personal-data folders: {path}");
                }
                var containsLink = Directory.Exists(canonicalPath)
                    ? DeletionPathPolicy.ContainsReparsePointTree(canonicalPath)
                    : DeletionPathPolicy.ContainsReparsePoint(canonicalPath);
                if (containsLink)
                {
                    throw new IOException($"The selected folder contains a junction or symbolic link and was not deleted: {canonicalPath}");
                }
                validatedPaths.Add(canonicalPath);
            }
            foreach (var canonicalPath in validatedPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(canonicalPath))
                {
                    DeleteTreeWithoutFollowingLinks(canonicalPath, cancellationToken);
                }
                else if (File.Exists(canonicalPath))
                {
                    if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException($"The selected file became a junction or symbolic link: {canonicalPath}");
                    }
                    File.Delete(canonicalPath);
                }
            }
        }, cancellationToken);

    private static IReadOnlyList<ManualDeleteCandidate> FindExactNameMatches(InstalledApplication application, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var productTokens = GetProductTokens(application);
        if (productTokens.Count == 0)
        {
            return [];
        }

        AddRegisteredLocation(application.InstallLocation, "Windows registered install location; verify it belongs to this app", application, productTokens, results, allowNameMismatch: true);
        AddRegisteredLocation(GetMsiInstallLocation(application), "Windows Installer registered install location; verify it belongs to this app", application, productTokens, results, allowNameMismatch: true);
        AddExecutableLocation(application.DisplayIcon, "Windows registered app icon folder", application, productTokens, results);
        AddExecutableLocation(application.UninstallCommand, "App-specific Windows uninstaller folder", application, productTokens, results);
        AddSteamGameCandidates(application, results, cancellationToken);

        foreach (var root in GetApplicationDataRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddApplicationDataMatches(root, application.Publisher, productTokens, results);
        }

        var visitedDirectories = 0;
        var resultsLock = new object();
        Parallel.ForEach(GetPersonalRoots(), new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount)
        }, root =>
        {
            var rootResults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ScanPersonalTree(root, productTokens, rootResults, cancellationToken, () =>
            {
                var visited = Interlocked.Increment(ref visitedDirectories);
                if ((visited & 127) == 0)
                {
                    progress?.Report(visited);
                }
            });
            lock (resultsLock)
            {
                foreach (var candidate in rootResults)
                {
                    if (!results.ContainsKey(candidate.Key))
                    {
                        results.Add(candidate.Key, candidate.Value);
                    }
                }
            }
        });
        progress?.Report(visitedDirectories);

        return results.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new ManualDeleteCandidate(pair.Key, pair.Value, Directory.Exists(pair.Key)))
            .ToArray();
    }

    private static void AddRegisteredLocation(string? path, string source, InstalledApplication application, IReadOnlySet<string> productTokens, IDictionary<string, string> results, bool allowNameMismatch = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            var canonicalPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            var folderToken = NormalizeToken(Path.GetFileName(canonicalPath));
            if (Directory.Exists(canonicalPath) && (allowNameMismatch || productTokens.Contains(folderToken)) && !IsSharedLauncherDirectory(canonicalPath) && !IsGenericSharedDirectory(canonicalPath) && IsAllowedCandidatePath(canonicalPath, application) && IsSafeDirectory(canonicalPath))
            {
                AddCandidate(canonicalPath, source, results);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static void AddExecutableLocation(string commandOrIcon, string source, InstalledApplication application, IReadOnlySet<string> productTokens, IDictionary<string, string> results)
    {
        var executable = ResolveExecutablePath(commandOrIcon);
        if (executable is null)
        {
            return;
        }
        if (IsSharedLauncherExecutable(executable))
        {
            return;
        }
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        var folderToken = NormalizeToken(Path.GetFileName(directory));
        var executableToken = NormalizeToken(Path.GetFileNameWithoutExtension(executable));
        var dedicatedUninstaller = IsDedicatedUninstaller(Path.GetFileNameWithoutExtension(executable));
        if (productTokens.Contains(folderToken) || productTokens.Contains(executableToken) || dedicatedUninstaller)
        {
            AddRegisteredLocation(directory, source, application, productTokens, results, allowNameMismatch: dedicatedUninstaller);
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

    private static void ScanPersonalTree(string root, IReadOnlySet<string> productTokens, IDictionary<string, string> results, CancellationToken cancellationToken, Action visitedDirectory)
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
                        if (!IsTraversableDirectory(child))
                        {
                            continue;
                        }
                        visitedDirectory();
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

    private static bool IsTraversableDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlySet<string> GetProductTokens(InstalledApplication application)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddToken(application.Name);
        AddToken(Regex.Replace(application.Name, @"(?:\s+\(?v?\d+(?:[._-]\d+)*\)?)$", string.Empty, RegexOptions.IgnoreCase));
        var versionToken = NormalizeToken(application.Version);
        var nameToken = NormalizeToken(application.Name);
        if (versionToken.Length > 0 && nameToken.EndsWith(versionToken, StringComparison.OrdinalIgnoreCase) && nameToken.Length - versionToken.Length >= 3)
        {
            AddToken(nameToken[..^versionToken.Length]);
        }
        var iconPath = ResolveExecutablePath(application.DisplayIcon);
        if (iconPath is not null && !IsSharedLauncherExecutable(iconPath))
        {
            AddToken(Path.GetFileNameWithoutExtension(iconPath));
        }
        foreach (var location in GetRegisteredInstallLocations(application))
        {
            var folderName = Path.GetFileName(location.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!IsSharedLauncherDirectory(folderName) && !IsGenericSharedDirectory(folderName))
            {
                AddToken(folderName);
            }
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

    private static string[] GetRegisteredInstallLocations(InstalledApplication application)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in new[] { application.InstallLocation, GetMsiInstallLocation(application) })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            try
            {
                paths.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
            }
        }
        return paths.ToArray();
    }

    private static string? GetMsiInstallLocation(InstalledApplication application)
    {
        var productCode = Regex.Match(application.UninstallCommand + " " + application.RegistryKeyPath, @"\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}");
        if (!productCode.Success)
        {
            return null;
        }
        try
        {
            var value = new StringBuilder(32768);
            var length = (uint)value.Capacity;
            return MsiGetProductInfoW(productCode.Value, "InstallLocation", value, ref length) == 0
                ? value.ToString().Trim()
                : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoW(string productCode, string property, StringBuilder valueBuffer, ref uint valueBufferLength);

    private static void AddSteamGameCandidates(InstalledApplication application, IDictionary<string, string> results, CancellationToken cancellationToken)
    {
        foreach (var candidate in GetSteamGameCandidatePaths(application))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((Directory.Exists(candidate.Path) || File.Exists(candidate.Path)) && IsAllowedCandidatePath(candidate.Path, application) && IsSafeCandidatePath(candidate.Path))
            {
                AddCandidate(candidate.Path, candidate.Source, results);
            }
        }
    }

    private static IReadOnlyList<SteamCandidate> GetSteamGameCandidatePaths(InstalledApplication application)
    {
        var appId = GetSteamAppId(application);
        if (appId is null)
        {
            return [];
        }

        var productTokens = GetProductTokens(application);
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in GetSteamLibraryRoots(application))
        {
            var manifestPath = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            string manifest;
            try
            {
                if (!File.Exists(manifestPath))
                {
                    continue;
                }
                manifest = File.ReadAllText(manifestPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                continue;
            }

            var manifestAppId = ReadVdfValue(manifest, "appid");
            var manifestName = ReadVdfValue(manifest, "name");
            var installDirectory = ReadVdfValue(manifest, "installdir");
            if (manifestAppId != appId || string.IsNullOrWhiteSpace(installDirectory) || !productTokens.Contains(NormalizeToken(manifestName)))
            {
                continue;
            }

            var gameDirectory = Path.Combine(library, "steamapps", "common", installDirectory);
            AddSteamCandidate(gameDirectory, $"Steam game install · app {appId}", candidates);
            if (!Directory.Exists(gameDirectory))
            {
                AddSteamCandidate(manifestPath, $"Steam game manifest · app {appId}", candidates);
            }
            AddSteamCandidate(Path.Combine(library, "steamapps", "shadercache", appId), $"Steam shader cache · app {appId}", candidates);
            AddSteamCandidate(Path.Combine(library, "steamapps", "workshop", "content", appId), $"Steam Workshop data · app {appId}", candidates);
            var steamRoot = GetSteamRoot(application);
            if (steamRoot is not null)
            {
                var userDataRoot = Path.Combine(steamRoot, "userdata");
                try
                {
                    foreach (var accountFolder in Directory.EnumerateDirectories(userDataRoot, "*", SearchOption.TopDirectoryOnly))
                    {
                        AddSteamCandidate(Path.Combine(accountFolder, appId), $"Steam account data · app {appId}", candidates);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                }
            }
        }
        return candidates.Select(pair => new SteamCandidate(pair.Key, pair.Value)).ToArray();
    }

    private static string? GetSteamAppId(InstalledApplication application)
    {
        foreach (var value in new[] { application.UninstallCommand, application.RegistryKeyPath })
        {
            var match = Regex.Match(value, @"(?i)steam://(?:uninstall|run|install)/(?<id>\d{4,})");
            if (!match.Success)
            {
                match = Regex.Match(value, @"(?i)(?:--?uninstall(?:appid)?|/uninstall)\s+(?:app/)?(?<id>\d{4,})");
            }
            if (!match.Success)
            {
                match = Regex.Match(value, @"(?i)steam\s+app\s+(?<id>\d{4,})");
            }
            if (match.Success)
            {
                return match.Groups["id"].Value;
            }
        }
        return null;
    }

    private static string[] GetSteamLibraryRoots(InstalledApplication application)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steamRoot = GetSteamRoot(application);
        if (steamRoot is null)
        {
            return [];
        }
        roots.Add(steamRoot);
        var libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(libraryFile))
            {
                var contents = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(contents, @"""path""\s*""([^""]+)""", RegexOptions.IgnoreCase))
                {
                    var path = UnescapeVdfPath(match.Groups[1].Value);
                    if (Directory.Exists(path))
                    {
                        roots.Add(Path.GetFullPath(path));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
        }
        return roots.ToArray();
    }

    private static string? GetSteamRoot(InstalledApplication application)
    {
        foreach (var command in new[] { application.UninstallCommand, application.DisplayIcon })
        {
            var executable = ResolveExecutablePath(command);
            if (executable is not null && NormalizeToken(Path.GetFileNameWithoutExtension(executable)).Equals("STEAM", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(executable);
            }
        }
        return null;
    }

    private static string ReadVdfValue(string document, string key)
    {
        var match = Regex.Match(document, $@"""{Regex.Escape(key)}""\s*""([^""]*)""", RegexOptions.IgnoreCase);
        return match.Success ? UnescapeVdfPath(match.Groups[1].Value) : string.Empty;
    }

    private static string UnescapeVdfPath(string value) => value.Replace(@"\\", @"\").Replace("\\\"", "\"");

    private static void AddSteamCandidate(string path, string source, IDictionary<string, string> candidates)
    {
        try
        {
            var canonicalPath = Path.GetFullPath(path);
            if ((Directory.Exists(canonicalPath) || File.Exists(canonicalPath)) && !candidates.ContainsKey(canonicalPath))
            {
                candidates.Add(canonicalPath, source);
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
        }
    }

    private static bool IsKnownCandidate(string path, InstalledApplication application)
    {
        var productTokens = GetProductTokens(application);
        if (Directory.Exists(path) && GetRegisteredInstallLocations(application).Any(location => SamePath(path, location)) && !IsSharedLauncherDirectory(path) && !IsGenericSharedDirectory(path))
        {
            return IsAllowedCandidatePath(path, application);
        }
        if (IsRegisteredExecutableParent(path, application.DisplayIcon, productTokens) && productTokens.Contains(NormalizeToken(Path.GetFileName(path))))
        {
            return IsAllowedCandidatePath(path, application) && !IsSharedLauncherDirectory(path) && !IsGenericSharedDirectory(path);
        }
        if (IsRegisteredExecutableParent(path, application.UninstallCommand, productTokens))
        {
            return IsAllowedCandidatePath(path, application);
        }
        if (GetSteamGameCandidatePaths(application).Any(candidate => SamePath(candidate.Path, path)))
        {
            return true;
        }
        if (GetApplicationDataRoots().Any(root => DeletionPathPolicy.IsPathWithin(path, root)) || GetPersonalRoots().Any(root => DeletionPathPolicy.IsPathWithin(path, root)))
        {
            return productTokens.Contains(NormalizeToken(Path.GetFileName(path)));
        }
        return false;
    }

    private static bool IsAllowedCandidatePath(string path, InstalledApplication application)
    {
        var policy = CreatePolicy(application);
        return policy.TryValidate(path, out _, out _) && !IsProtectedWindowsPath(path);
    }

    private static bool IsRegisteredExecutableParent(string path, string commandOrIcon, IReadOnlySet<string> productTokens)
    {
        var executable = ResolveExecutablePath(commandOrIcon);
        if (executable is null || IsSharedLauncherExecutable(executable))
        {
            return false;
        }
        var directory = Path.GetDirectoryName(executable) ?? string.Empty;
        var executableName = Path.GetFileNameWithoutExtension(executable);
        return SamePath(path, directory) && !IsSharedLauncherDirectory(directory) && !IsGenericSharedDirectory(directory) &&
            (productTokens.Contains(NormalizeToken(Path.GetFileName(directory))) || productTokens.Contains(NormalizeToken(executableName)) || IsDedicatedUninstaller(executableName));
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

    private static DeletionPathPolicy CreatePolicy(InstalledApplication application) => new(GetAllowedRoots(application), [Environment.GetFolderPath(Environment.SpecialFolder.Windows)]);

    private static string[] GetAllowedRoots(InstalledApplication application) => GetPersonalRoots()
        .Concat(GetApplicationDataRoots())
        .Concat([Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)])
        .Concat(GetSteamLibraryRoots(application))
        .Concat(GetRegisteredInstallLocations(application).Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!))
        .Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string[] GetApplicationDataRoots() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")
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

    private static bool IsSafeCandidatePath(string path)
    {
        if (Directory.Exists(path))
        {
            return IsSafeDirectory(path);
        }
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            return !DeletionPathPolicy.ContainsReparsePoint(path);
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

    private static bool IsSharedLauncherExecutable(string path) => SharedLauncherTokens.Contains(NormalizeToken(Path.GetFileNameWithoutExtension(path)));

    private static bool IsSharedLauncherDirectory(string path) => SharedLauncherTokens.Contains(NormalizeToken(Path.GetFileName(path)));

    private static bool IsGenericSharedDirectory(string path) => NormalizeToken(Path.GetFileName(path)) is "COMMONFILES" or "COMMONAPPLICATIONFILES" or "INSTALLSHIELD" or "WINDOWSINSTALLER" or "PACKAGECACHE" or "STEAMAPPS";

    private static bool IsDedicatedUninstaller(string executableName) =>
        Regex.IsMatch(NormalizeToken(executableName), @"^(?:UNINS\d*|UNINSTALL(?:ER)?|UNINST|UNWISE\d*)$", RegexOptions.IgnoreCase);

    private static string NormalizeToken(string value) => new(value.Where(char.IsLetterOrDigit).ToArray());

    private sealed record SteamCandidate(string Path, string Source);

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
