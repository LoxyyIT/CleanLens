using System.Diagnostics;

namespace CleanLens.Windows;

public sealed record DefenderScanTarget(string Name, string Path);

public sealed record DefenderScanResult(string TargetPath, int ExitCode, string Output)
{
    public bool NeedsReview => ExitCode == 2;
    public bool Succeeded => ExitCode is 0 or 2;
}

public sealed class DefenderQuickScanService
{
    public async Task<IReadOnlyList<DefenderScanResult>> ScanAsync(
        IEnumerable<DefenderScanTarget> targets,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var executable = FindMpCmdRun();
        if (executable is null)
        {
            throw new InvalidOperationException("Microsoft Defender Antivirus could not be found on this device.");
        }

        var scanTargets = targets
            .Where(target => !string.IsNullOrWhiteSpace(target.Path) && (Directory.Exists(target.Path) || File.Exists(target.Path)))
            .Select(target => new DefenderScanTarget(target.Name, Path.GetFullPath(target.Path)))
            .DistinctBy(target => target.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (scanTargets.Length == 0)
        {
            throw new InvalidOperationException("There are no available folders to scan.");
        }

        var results = new List<DefenderScanResult>(scanTargets.Length);
        for (var index = 0; index < scanTargets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = scanTargets[index];
            progress?.Report($"{index + 1}/{scanTargets.Length}|{target.Name}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.StartInfo.ArgumentList.Add("-Scan");
            process.StartInfo.ArgumentList.Add("-ScanType");
            process.StartInfo.ArgumentList.Add("3");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(target.Path);
            process.StartInfo.ArgumentList.Add("-DisableRemediation");

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Microsoft Defender could not start the scan.");
                }

                var standardOutput = process.StandardOutput.ReadToEndAsync();
                var standardError = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(cancellationToken);
                var output = (await standardOutput) + Environment.NewLine + (await standardError);
                results.Add(new DefenderScanResult(target.Path, process.ExitCode, LimitOutput(output)));
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                throw;
            }
        }

        return results;
    }

    private static string? FindMpCmdRun()
    {
        var platformRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows Defender", "Platform");
        if (Directory.Exists(platformRoot))
        {
            var latest = Directory.EnumerateDirectories(platformRoot)
                .Select(path => new { Path = path, Version = ParsePlatformVersion(Path.GetFileName(path)) })
                .Where(item => item.Version is not null)
                .OrderByDescending(item => item.Version)
                .Select(item => Path.Combine(item.Path, "MpCmdRun.exe"))
                .FirstOrDefault(File.Exists);
            if (latest is not null) return latest;
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        return File.Exists(fallback) ? fallback : null;
    }

    private static Version? ParsePlatformVersion(string directoryName)
    {
        var versionPart = directoryName.Split('-', 2)[0];
        return Version.TryParse(versionPart, out var version) ? version : null;
    }

    private static string LimitOutput(string output)
    {
        const int maximumCharacters = 24_000;
        var trimmed = output.Trim();
        return trimmed.Length <= maximumCharacters
            ? trimmed
            : trimmed[..maximumCharacters] + Environment.NewLine + "…";
    }
}
