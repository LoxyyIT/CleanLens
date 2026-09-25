using CleanLens.Core.Models;
using CleanLens.Core.Safety;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

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
                    var reasonKey = publisherFolderMatches
                        ? "PublisherProductPath"
                        : "ProductFolderAfterUninstall";
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
                        false)
                    {
                        ReasonKey = reasonKey
                    });
                }
            }

            found.AddRange(ScanServicesAndStartup(application, cancellationToken));

            return found.DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);
    }

    private static IReadOnlyList<LeftoverCandidate> ScanServicesAndStartup(InstalledApplication application, CancellationToken cancellationToken)
    {
        var candidates = new List<LeftoverCandidate>();
        var identityTokens = new[] { application.Name, Regex.Replace(application.Name, @"(?:\s+\(?v?\d+(?:[._-]\d+)*\)?)$", string.Empty, RegexOptions.IgnoreCase), Path.GetFileName(application.InstallLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormalizeToken).Where(value => value.Length >= 4).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool MatchesName(string value) => identityTokens.Contains(NormalizeToken(value), StringComparer.OrdinalIgnoreCase);
        bool MatchesCommand(string value)
        {
            foreach (Match match in Regex.Matches(value, "(?:[A-Za-z]:\\\\|%[^%]+%\\\\)[^\\\"<>\\r\\n]*?\\.exe", RegexOptions.IgnoreCase))
            {
                var executable = match.Value.Trim().Trim('"');
                if (MatchesName(Path.GetFileNameWithoutExtension(executable))) return true;
                var components = executable.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                if (components.Any(component => identityTokens.Contains(NormalizeToken(Path.GetFileNameWithoutExtension(component)), StringComparer.OrdinalIgnoreCase))) return true;
            }
            return identityTokens.Any(token => Regex.IsMatch(value, $@"(?<![A-Za-z0-9]){Regex.Escape(token)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase));
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var services = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                foreach (var name in services?.GetSubKeyNames() ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = services!.OpenSubKey(name);
                        var image = Convert.ToString(key?.GetValue("ImagePath")) ?? string.Empty;
                        var display = Convert.ToString(key?.GetValue("DisplayName")) ?? string.Empty;
                        if (MatchesName(name) || MatchesCommand(image) || MatchesName(display))
                            candidates.Add(ReadOnlyArtifact($"Service: {name}", CandidateCategory.Service, "ServiceNamePathMatch", "A remaining Windows service name or executable path matches the registered app identity. CleanLens does not remove services automatically."));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }

            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    foreach (var runPath in new[]
                    {
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
                        @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce"
                    })
                    {
                        using var run = root.OpenSubKey(runPath);
                        foreach (var name in run?.GetValueNames() ?? [])
                        {
                            var command = Convert.ToString(run?.GetValue(name)) ?? string.Empty;
                            if (MatchesName(name) || MatchesCommand(command))
                                candidates.Add(ReadOnlyArtifact($"Startup registry: {hive}\\{runPath}\\{name}", CandidateCategory.StartupEntry, "StartupEntryMatch", "A startup command or value name matches the registered app identity. Review the source before changing it."));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { }
            }
        }

        foreach (var startup in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        }.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(startup, "*", SearchOption.TopDirectoryOnly))
                {
                    if (MatchesName(Path.GetFileNameWithoutExtension(file)))
                        candidates.Add(ReadOnlyArtifact(file, CandidateCategory.StartupEntry, "StartupEntryMatch", "A startup-folder shortcut name matches the registered app identity. The target is not executed or followed."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }

        if (!ScanRegisteredTasks(candidates, application, identityTokens, cancellationToken))
        {
            ScanTaskDefinitionFiles(candidates, application, identityTokens, cancellationToken);
        }

        return candidates;
    }

    private static bool ScanRegisteredTasks(List<LeftoverCandidate> candidates, InstalledApplication application, IReadOnlyCollection<string> identityTokens, CancellationToken cancellationToken)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null) return false;
        object? service = null;
        object? rootObject = null;
        try
        {
            service = Activator.CreateInstance(type);
            if (service is null) return false;
            ((dynamic)service).Connect();
            rootObject = ((dynamic)service).GetFolder("\\");
            ScanFolder((dynamic)rootObject, 0);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException)
        {
            return false;
        }
        finally
        {
            if (rootObject is not null && Marshal.IsComObject(rootObject)) Marshal.FinalReleaseComObject(rootObject);
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }

        void ScanFolder(dynamic folder, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 16 || candidates.Count >= 50000) return;
            var tasks = folder.GetTasks(1);
            var taskCount = Math.Min((int)tasks.Count, 50000 - candidates.Count);
            for (var index = 1; index <= taskCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object? taskObject = null;
                try
                {
                    taskObject = tasks.Item(index);
                    dynamic task = taskObject;
                    string taskPath = Convert.ToString(task.Path) ?? string.Empty;
                    string taskName = Convert.ToString(task.Name) ?? string.Empty;
                    string xml = Convert.ToString(task.Xml) ?? string.Empty;
                    if (identityTokens.Contains(NormalizeToken(taskName), StringComparer.OrdinalIgnoreCase) || MatchesTaskCommand(xml, identityTokens))
                        candidates.Add(ReadOnlyArtifact($"Scheduled task: {taskPath}", CandidateCategory.ScheduledTask, "ScheduledTaskMatch", "A registered task definition name or executable action matches the registered application identity. CleanLens does not change scheduled tasks."));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException) { }
                finally
                {
                    if (taskObject is not null && Marshal.IsComObject(taskObject)) Marshal.FinalReleaseComObject(taskObject);
                }
            }
            var folders = folder.GetFolders(0);
            var folderCount = Math.Min((int)folders.Count, 50000 - candidates.Count);
            for (var index = 1; index <= folderCount; index++)
            {
                object? childObject = null;
                try
                {
                    childObject = folders.Item(index);
                    ScanFolder((dynamic)childObject, depth + 1);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException) { }
                finally
                {
                    if (childObject is not null && Marshal.IsComObject(childObject)) Marshal.FinalReleaseComObject(childObject);
                }
            }
            if (Marshal.IsComObject(tasks)) Marshal.FinalReleaseComObject(tasks);
            if (Marshal.IsComObject(folders)) Marshal.FinalReleaseComObject(folders);
        }
    }

    private static bool MatchesTaskCommand(string xml, IReadOnlyCollection<string> identityTokens)
    {
        foreach (Match match in Regex.Matches(xml, "(?:[A-Za-z]:\\\\|%[^%]+%\\\\)[^\\\"<>\\r\\n]*?\\.exe", RegexOptions.IgnoreCase))
        {
            var executable = match.Value.Trim().Trim('"');
            if (identityTokens.Contains(NormalizeToken(Path.GetFileNameWithoutExtension(executable)), StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static void ScanTaskDefinitionFiles(List<LeftoverCandidate> candidates, InstalledApplication application, IReadOnlyCollection<string> identityTokens, CancellationToken cancellationToken)
    {
        bool Matches(string value) => identityTokens.Any(token => Regex.IsMatch(value, $@"(?<![A-Za-z0-9]){Regex.Escape(token)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase));
        var tasksRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        if (Directory.Exists(tasksRoot))
        {
            var pending = new Stack<string>();
            pending.Push(tasksRoot);
            var checkedFiles = 0;
            while (pending.Count > 0 && checkedFiles < 50000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        if (IsSafeDirectory(child)) pending.Push(child);
                    }
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        if (++checkedFiles > 50000) break;
                        var nameMatch = Matches(Path.GetFileNameWithoutExtension(file));
                        var contentMatch = false;
                        if (!nameMatch)
                        {
                            try
                            {
                                using var reader = new StreamReader(file);
                                var xml = reader.ReadToEnd();
                                contentMatch = MatchesTaskCommand(xml, identityTokens);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                        }
                        if (nameMatch || contentMatch)
                            candidates.Add(ReadOnlyArtifact($"Scheduled task: {file}", CandidateCategory.ScheduledTask, "ScheduledTaskMatch", "A task definition name or executable action matches the registered application identity. CleanLens does not change scheduled tasks."));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private static LeftoverCandidate ReadOnlyArtifact(string path, CandidateCategory category, string reasonKey, string reason) => new(
        path, category, ConfidenceLevel.Medium, reason, null, true, false) { ReasonKey = reasonKey };

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
