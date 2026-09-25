using CleanLens.Core.Models;
using CleanLens.Core.Safety;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CleanLens.Windows;

public sealed record ManualDeleteCandidate(string Path, string Source, bool IsDirectory);
public sealed record DiskUsageMeasurement(long Bytes, int Locations, int Files, int Skipped, bool IsIncomplete);

public sealed class ManualDeleteService
{
    private static readonly HashSet<string> SharedLauncherTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "STEAM", "EPICGAMESLAUNCHER", "UBISOFTCONNECT", "EAAPP", "EADESKTOP", "BATTLENET", "GOGGALAXY",
        "RIOTCLIENTSERVICES", "RIOTCLIENTUX", "ROCKSTARLAUNCHER", "ROCKSTARGAMESLAUNCHER", "XBOXPCAPP"
    };

    public Task<IReadOnlyList<ManualDeleteCandidate>> FindExactNameMatchesAsync(InstalledApplication application, CancellationToken cancellationToken = default, IProgress<int>? progress = null, IEnumerable<string>? additionalRoots = null, IEnumerable<string>? enabledDefaultRoots = null, bool includeRegisteredLocations = true, bool includeSteamLocations = true) =>
        Task.Run<IReadOnlyList<ManualDeleteCandidate>>(() => FindExactNameMatches(application, cancellationToken, progress, additionalRoots, enabledDefaultRoots, includeRegisteredLocations, includeSteamLocations), cancellationToken);

    public async Task<DiskUsageMeasurement> MeasureApplicationFootprintAsync(InstalledApplication application, CancellationToken cancellationToken = default, IEnumerable<string>? additionalRoots = null, IProgress<int>? progress = null, IEnumerable<string>? enabledDefaultRoots = null, bool includeRegisteredLocations = true, bool includeSteamLocations = true)
    {
        var candidates = await FindExactNameMatchesAsync(application, cancellationToken, progress, additionalRoots, enabledDefaultRoots, includeRegisteredLocations, includeSteamLocations);
        return await Task.Run(() =>
        {
            long bytes = 0;
            var fileCount = 0;
            var skipped = 0;
            var visited = 0;
            var visitedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!candidate.IsDirectory)
                {
                    try
                    {
                        if (visitedFiles.Add(candidate.Path)) { bytes = checked(bytes + new FileInfo(candidate.Path).Length); fileCount++; }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OverflowException) { skipped++; }
                    continue;
                }
                var pending = new Stack<string>();
                pending.Push(candidate.Path);
                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = pending.Pop();
                    IEnumerable<string> files;
                    IEnumerable<string> directories;
                    try { files = Directory.EnumerateFiles(current).ToArray(); directories = Directory.EnumerateDirectories(current).ToArray(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; continue; }
                    foreach (var file in files)
                    {
                        if ((++visited & 127) == 0) progress?.Report(visited);
                        try
                        {
                            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && visitedFiles.Add(Path.GetFullPath(file)))
                            {
                                bytes = checked(bytes + new FileInfo(file).Length);
                                fileCount++;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or OverflowException) { skipped++; }
                    }
                    foreach (var directory in directories)
                    {
                        try
                        {
                            if ((File.GetAttributes(directory) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory) pending.Push(directory);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { skipped++; }
                    }
                }
            }
            progress?.Report(visited);
            return new DiskUsageMeasurement(bytes, candidates.Count, fileCount, skipped, skipped > 0);
        }, cancellationToken);
    }

    public Task<string> MoveCandidateToQuarantineAsync(
        InstalledApplication application,
        string candidatePath,
        QuarantineService quarantineService,
        string operationText,
        string resultText,
        CancellationToken cancellationToken = default,
        IEnumerable<string>? additionalRoots = null,
        IEnumerable<string>? enabledDefaultRoots = null,
        bool includeRegisteredLocations = true,
        bool includeSteamLocations = true)
    {
        var roots = NormalizeAdditionalRoots(additionalRoots);
        var candidatePolicy = CreatePolicy(application, roots);
        if (!candidatePolicy.TryValidate(candidatePath, out var canonicalPath, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        if (!IsKnownCandidate(canonicalPath, application, roots, enabledDefaultRoots, includeRegisteredLocations, includeSteamLocations))
        {
            throw new InvalidOperationException("The selected path is no longer a supported candidate.");
        }
        return quarantineService.MoveManualCandidateAsync(
            canonicalPath,
            application.Name,
            candidatePolicy,
            path => candidatePolicy.TryValidate(path, out var latestPath, out _) &&
                latestPath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase) &&
                IsKnownCandidate(latestPath, application, roots, enabledDefaultRoots, includeRegisteredLocations, includeSteamLocations),
            operationText,
            resultText,
            cancellationToken);
    }

    public Task DeleteSelectedAsync(InstalledApplication application, IEnumerable<string> selectedPaths, CancellationToken cancellationToken = default, IEnumerable<string>? additionalRoots = null, IEnumerable<string>? enabledDefaultRoots = null, bool includeRegisteredLocations = true, bool includeSteamLocations = true) =>
        Task.Run(() =>
        {
            var roots = NormalizeAdditionalRoots(additionalRoots);
            var policy = CreatePolicy(application, roots);
            var validatedPaths = new List<string>();
            foreach (var path in selectedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!policy.TryValidate(path, out var canonicalPath, out _) || !IsKnownCandidate(canonicalPath, application, roots, enabledDefaultRoots, includeRegisteredLocations, includeSteamLocations) || (!Directory.Exists(canonicalPath) && !File.Exists(canonicalPath)))
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

    private static IReadOnlyList<ManualDeleteCandidate> FindExactNameMatches(InstalledApplication application, CancellationToken cancellationToken, IProgress<int>? progress, IEnumerable<string>? additionalRoots, IEnumerable<string>? enabledDefaultRoots, bool includeRegisteredLocations, bool includeSteamLocations)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var productTokens = GetProductTokens(application);
        if (productTokens.Count == 0)
        {
            return [];
        }

        if (includeRegisteredLocations && !application.IsAppxPackage)
        {
            AddRegisteredLocation(application.InstallLocation, "Windows registered install location; verify it belongs to this app", application, productTokens, results, allowNameMismatch: true);
            AddRegisteredLocation(GetMsiInstallLocation(application), "Windows Installer registered install location; verify it belongs to this app", application, productTokens, results, allowNameMismatch: true);
            AddExecutableLocation(application.DisplayIcon, "Windows registered app icon folder", application, productTokens, results);
        }
        if (includeRegisteredLocations) AddExecutableLocation(application.UninstallCommand, "App-specific Windows uninstaller folder", application, productTokens, results);
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (FilterEnabledRoots([localAppDataRoot], enabledDefaultRoots).Length > 0)
        {
            AddAppxDataCandidate(application, productTokens, results);
        }
        if (includeSteamLocations) AddSteamGameCandidates(application, results, cancellationToken);

        foreach (var root in FilterEnabledRoots(GetApplicationDataRoots(), enabledDefaultRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExactDirectoryMatches(root, application.Publisher, productTokens, results,
                "Exact app-name folder in an application-data root",
                "Exact publisher/product folders in an application-data root");
        }

        foreach (var root in FilterEnabledRoots(GetProgramRoots(), enabledDefaultRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExactDirectoryMatches(root, application.Publisher, productTokens, results,
                "Exact app-name folder in a Program Files root",
                "Exact publisher/product folders in a Program Files root");
        }

        var visitedDirectories = 0;
        foreach (var root in FilterEnabledRoots(GetUserProfileRoots(), enabledDefaultRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExactDirectoryMatches(root, application.Publisher, productTokens, results,
                "Exact app-name folder under a user profile",
                "Exact publisher/product folders under a user profile");
        }

        foreach (var root in NormalizeAdditionalRoots(additionalRoots))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddExactDirectoryMatches(root, application.Publisher, productTokens, results,
                "Exact app-name folder under a user-added search root",
                "Exact publisher/product folders under a user-added search root");
            ScanPersonalTree(root, productTokens, results, cancellationToken, () =>
            {
                var visited = Interlocked.Increment(ref visitedDirectories);
                if ((visited & 127) == 0) progress?.Report(visited);
            }, "Exact app-name folder under a user-added search root");
        }

        var resultsLock = new object();
        Parallel.ForEach(FilterEnabledRoots(GetPersonalRoots(), enabledDefaultRoots), new ParallelOptions
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

    private static void AddExactDirectoryMatches(
        string root,
        string publisher,
        IReadOnlySet<string> productTokens,
        IDictionary<string, string> results,
        string appNameSource,
        string publisherProductSource)
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
                    AddCandidate(folder, appNameSource, results);
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
                        AddCandidate(productFolder, publisherProductSource, results);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
        }
    }

    private static void ScanPersonalTree(string root, IReadOnlySet<string> productTokens, IDictionary<string, string> results, CancellationToken cancellationToken, Action visitedDirectory, string candidateSource = "Exact registered app-name folder in a personal library")
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
                            AddCandidate(child, candidateSource, results);
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
        AddToken(application.PackageFamilyName);
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
        if (application.IsAppxPackage) return [];
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

    private static bool IsKnownCandidate(string path, InstalledApplication application, IEnumerable<string>? additionalRoots = null, IEnumerable<string>? enabledDefaultRoots = null, bool includeRegisteredLocations = true, bool includeSteamLocations = true)
    {
        var productTokens = GetProductTokens(application);
        if (application.IsAppxPackage && FilterEnabledRoots([Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)], enabledDefaultRoots).Length > 0 &&
            SamePath(path, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", application.PackageFamilyName))) return true;
        if (includeRegisteredLocations && Directory.Exists(path) && GetRegisteredInstallLocations(application).Any(location => SamePath(path, location)) && !IsSharedLauncherDirectory(path) && !IsGenericSharedDirectory(path))
        {
            return IsAllowedCandidatePath(path, application);
        }
        if (includeRegisteredLocations && IsRegisteredExecutableParent(path, application.DisplayIcon, productTokens) && productTokens.Contains(NormalizeToken(Path.GetFileName(path))))
        {
            return IsAllowedCandidatePath(path, application) && !IsSharedLauncherDirectory(path) && !IsGenericSharedDirectory(path);
        }
        if (includeRegisteredLocations && IsRegisteredExecutableParent(path, application.UninstallCommand, productTokens))
        {
            return IsAllowedCandidatePath(path, application);
        }
        if (includeSteamLocations && GetSteamGameCandidatePaths(application).Any(candidate => SamePath(candidate.Path, path)))
        {
            return true;
        }
        var activeDefaultRoots = enabledDefaultRoots is null ? GetDefaultSearchRoots() : NormalizeDefaultRoots(enabledDefaultRoots);
        if (FilterEnabledRoots(GetApplicationDataRoots(), activeDefaultRoots).Concat(FilterEnabledRoots(GetProgramRoots(), activeDefaultRoots)).Concat(FilterEnabledRoots(GetUserProfileRoots(), activeDefaultRoots)).Concat(NormalizeAdditionalRoots(additionalRoots)).Any(root => DeletionPathPolicy.IsPathWithin(path, root)) ||
            FilterEnabledRoots(GetPersonalRoots(), activeDefaultRoots).Any(root => DeletionPathPolicy.IsPathWithin(path, root)))
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

    private static DeletionPathPolicy CreatePolicy(InstalledApplication application, IEnumerable<string>? additionalRoots = null) => new(GetAllowedRoots(application, additionalRoots), [Environment.GetFolderPath(Environment.SpecialFolder.Windows)]);

    private static string[] GetAllowedRoots(InstalledApplication application, IEnumerable<string>? additionalRoots = null) => GetPersonalRoots()
        .Concat(GetApplicationDataRoots())
        .Concat(GetProgramRoots())
        .Concat(GetUserProfileRoots())
        .Concat(GetSteamLibraryRoots(application))
        .Concat(NormalizeAdditionalRoots(additionalRoots))
        .Concat(GetRegisteredInstallLocations(application).Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!))
        .Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal))
        .Select(Path.GetFullPath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string[] NormalizeAdditionalRoots(IEnumerable<string>? paths)
    {
        if (paths is null) return [];
        return paths.Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal))
            .Select(path => Path.GetFullPath(path))
            .Where(path => Directory.Exists(path) && !IsProtectedWindowsPath(path) &&
                !path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Equals(Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && IsSafeDirectory(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> GetDefaultSearchRoots()
    {
        var profiles = GetUserProfileRoots(verifySafe: false);
        return NormalizeRoots(GetApplicationDataRoots(profiles).Concat(GetProgramRoots()).Concat(profiles).Concat(GetPersonalRoots(profiles)));
    }

    private static string[] FilterEnabledRoots(IEnumerable<string> roots, IEnumerable<string>? enabledRoots)
    {
        var materialized = roots.ToArray();
        if (enabledRoots is null) return materialized;
        var enabled = NormalizeDefaultRoots(enabledRoots);
        return materialized.Where(root => enabled.Contains(Path.GetFullPath(root), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    private static string[] NormalizeDefaultRoots(IEnumerable<string> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path) && !path.StartsWith("\\\\", StringComparison.Ordinal))
        .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static string[] GetApplicationDataRoots(IEnumerable<string>? profileRoots = null)
    {
        var paths = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
        };
        foreach (var profile in profileRoots ?? GetUserProfileRoots())
        {
            var appData = Path.Combine(profile, "AppData");
            paths.Add(Path.Combine(appData, "Roaming"));
            paths.Add(Path.Combine(appData, "Local"));
            paths.Add(Path.Combine(appData, "LocalLow"));
            paths.Add(Path.Combine(appData, "Local", "Programs"));
        }
        return NormalizeRoots(paths);
    }

    private static string[] GetProgramRoots() => NormalizeRoots(
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetEnvironmentVariable("ProgramW6432"),
        Environment.GetEnvironmentVariable("ProgramFiles"),
        Environment.GetEnvironmentVariable("ProgramFiles(x86)")
    ]);

    private static string[] GetPersonalRoots(IEnumerable<string>? profileRoots = null)
    {
        var paths = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games")
        };
        foreach (var profile in profileRoots ?? GetUserProfileRoots())
        {
            paths.Add(Path.Combine(profile, "Documents"));
            paths.Add(Path.Combine(profile, "Desktop"));
            paths.Add(Path.Combine(profile, "Pictures"));
            paths.Add(Path.Combine(profile, "Videos"));
            paths.Add(Path.Combine(profile, "Music"));
            paths.Add(Path.Combine(profile, "Downloads"));
            paths.Add(Path.Combine(profile, "Saved Games"));
        }
        return NormalizeRoots(paths);
    }

    private static string[] GetUserProfileRoots(bool verifySafe = true)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddProfile(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var profileList = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
                if (profileList is null)
                {
                    continue;
                }
                foreach (var sid in profileList.GetSubKeyNames())
                {
                    try
                    {
                        using var profileKey = profileList.OpenSubKey(sid);
                        if (profileKey is null || Convert.ToInt32(profileKey.GetValue("Special", 0), System.Globalization.CultureInfo.InvariantCulture) != 0)
                        {
                            continue;
                        }
                        AddProfile(profileKey.GetValue("ProfileImagePath") as string);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException or InvalidCastException or OverflowException)
                    {
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
            }
        }
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        void AddProfile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
                if (!fullPath.StartsWith("\\\\", StringComparison.Ordinal) && Directory.Exists(fullPath) && (!verifySafe || IsSafeDirectory(fullPath)))
                {
                    roots.Add(fullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
            }
        }
    }

    private static string[] NormalizeRoots(IEnumerable<string?> paths)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            {
                continue;
            }
            try
            {
                roots.Add(Path.GetFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
            }
        }
        return roots.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

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
        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        return (!string.IsNullOrWhiteSpace(windows) && DeletionPathPolicy.IsPathWithin(path, windows)) ||
            (!string.IsNullOrWhiteSpace(windowsApps) && DeletionPathPolicy.IsPathWithin(path, windowsApps));
    }

    private static void AddAppxDataCandidate(InstalledApplication application, IReadOnlySet<string> productTokens, IDictionary<string, string> results)
    {
        if (!application.IsAppxPackage || string.IsNullOrWhiteSpace(application.PackageFamilyName)) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages", application.PackageFamilyName);
        if (Directory.Exists(path) && productTokens.Contains(NormalizeToken(application.PackageFamilyName)) && IsAllowedCandidatePath(path, application) && IsSafeDirectory(path))
            AddCandidate(path, "Current-user AppX package data folder; review saves and settings before cleanup", results);
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
