using System.Diagnostics;

namespace CleanLens.Windows;

public sealed class AppxPackageManager
{
    public async Task RemoveForCurrentUserAsync(string packageFullName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageFullName) || packageFullName.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("The package identity is invalid.", nameof(packageFullName));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; Remove-AppxPackage -Package $env:CLEANLENS_PACKAGE");
        start.Environment["CLEANLENS_PACKAGE"] = packageFullName;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows PowerShell did not start.");
        using var registration = cancellationToken.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var error = await stderr;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows could not remove the package for the current user." : error.Trim());
        }
        _ = await stdout;
        _ = await stderr;
    }
}
