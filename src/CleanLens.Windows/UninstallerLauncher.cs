using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CleanLens.Windows;

public sealed class UninstallerLauncher
{
    public Process? Start(string registeredCommand)
    {
        var command = registeredCommand.Trim();
        if (command.Length == 0)
        {
            throw new InvalidOperationException("This application has no registered uninstaller command.");
        }

        var parsed = ParseExecutable(command);
        var fileName = parsed.FileName;
        if (IsMsiExecutable(fileName))
        {
            fileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        }
        else if (!Path.IsPathFullyQualified(fileName) ||
                 !Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                 !File.Exists(fileName))
        {
            throw new InvalidOperationException("Only an existing, fully qualified executable can be started.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = parsed.Arguments,
            UseShellExecute = true,
            WorkingDirectory = Environment.SystemDirectory
        };

        return Process.Start(startInfo);
    }

    public static (string FileName, string Arguments) ParseExecutable(string command)
    {
        var value = Environment.ExpandEnvironmentVariables(command.Trim());
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            if (closingQuote <= 1)
            {
                throw new InvalidOperationException("The registered uninstaller command has invalid quoting.");
            }

            return NormalizeMsiUninstall(value[1..closingQuote], value[(closingQuote + 1)..].Trim());
        }

        var executableEnd = FindExecutableEnd(value);
        if (executableEnd <= 0)
        {
            throw new InvalidOperationException("The registered uninstaller command has no executable path.");
        }

        return NormalizeMsiUninstall(value[..executableEnd], value[executableEnd..].Trim());
    }

    private static int FindExecutableEnd(string command)
    {
        var extension = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (extension >= 0)
        {
            return extension + 4;
        }

        var firstSpace = command.IndexOf(' ');
        return firstSpace < 0 ? command.Length : firstSpace;
    }

    private static (string FileName, string Arguments) NormalizeMsiUninstall(string fileName, string arguments)
    {
        if (IsMsiExecutable(fileName))
        {
            arguments = Regex.Replace(arguments, @"(?i)(?<!\S)/i(?=\s*\{)", "/x");
        }
        return (fileName, arguments);
    }

    private static bool IsMsiExecutable(string fileName)
    {
        var name = Path.GetFileName(fileName);
        return name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msiexec", StringComparison.OrdinalIgnoreCase);
    }
}
