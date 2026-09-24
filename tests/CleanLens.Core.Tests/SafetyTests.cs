using CleanLens.Core.Models;
using CleanLens.Core.Safety;
using CleanLens.Data;
using CleanLens.Windows;
using System.Diagnostics;
using Xunit;

namespace CleanLens.Core.Tests;

public sealed class SafetyTests
{
    [Fact]
    public void Path_policy_accepts_only_descendants_of_allowed_root()
    {
        using var sandbox = new TestSandbox();
        var allowed = Path.Combine(sandbox.Root, "allowed");
        var policy = new DeletionPathPolicy([allowed]);
        Directory.CreateDirectory(allowed);
        var target = Path.Combine(allowed, "Publisher", "Product");
        Assert.True(policy.TryValidate(target, out var canonical, out _));
        Assert.Equal(Path.GetFullPath(target), canonical);
        Assert.False(policy.TryValidate(allowed, out _, out _));
        Assert.False(policy.TryValidate(Path.Combine(sandbox.Root, "other"), out _, out _));
    }

    [Fact]
    public void Path_policy_rejects_traversal_and_protected_roots()
    {
        using var sandbox = new TestSandbox();
        var allowed = Path.Combine(sandbox.Root, "allowed");
        var documents = Path.Combine(sandbox.Root, "Documents");
        var policy = new DeletionPathPolicy([allowed], [documents]);
        Directory.CreateDirectory(allowed);
        Directory.CreateDirectory(documents);
        Assert.False(policy.TryValidate(Path.Combine(allowed, "..", "Documents", "work"), out _, out _));
        Assert.False(policy.TryValidate(documents, out _, out _));
        Assert.False(policy.TryValidate(Path.GetPathRoot(sandbox.Root)!, out _, out _));
    }

    [Fact]
    public void Confidence_never_defaults_user_data_or_low_confidence_to_selected()
    {
        Assert.Equal(ConfidenceLevel.Low, ConfidenceScorer.Score(3, true, true));
        Assert.Equal(ConfidenceLevel.Low, ConfidenceScorer.Score(1, true, false));
        Assert.False(ConfidenceScorer.SelectByDefault(ConfidenceLevel.Low, false));
        Assert.False(ConfidenceScorer.SelectByDefault(ConfidenceLevel.High, true));
        Assert.True(ConfidenceScorer.SelectByDefault(ConfidenceLevel.High, false));
    }

    [Fact]
    public void Registry_uninstaller_command_parser_preserves_quoted_executable_and_arguments()
    {
        var parsed = UninstallerLauncher.ParseExecutable("\"C:\\Program Files\\Vendor\\uninstall.exe\" /remove /quiet");
        Assert.Equal("C:\\Program Files\\Vendor\\uninstall.exe", parsed.FileName);
        Assert.Equal("/remove /quiet", parsed.Arguments);
    }

    [Fact]
    public void Registry_uninstaller_command_parser_handles_unquoted_executable_path()
    {
        var parsed = UninstallerLauncher.ParseExecutable("C:\\Program Files\\Vendor\\uninstall.exe /remove");
        Assert.Equal("C:\\Program Files\\Vendor\\uninstall.exe", parsed.FileName);
        Assert.Equal("/remove", parsed.Arguments);
    }

    [Fact]
    public void Registry_uninstaller_command_parser_rejects_unclosed_quotes()
    {
        Assert.Throws<InvalidOperationException>(() => UninstallerLauncher.ParseExecutable("\"C:\\Vendor\\uninstall.exe /remove"));
    }

    [Fact]
    public void Msi_install_command_is_converted_to_interactive_uninstall()
    {
        var parsed = UninstallerLauncher.ParseExecutable("MsiExec.exe /I{12345678-1234-1234-1234-123456789ABC}");
        Assert.Equal("MsiExec.exe", parsed.FileName);
        Assert.Equal("/x{12345678-1234-1234-1234-123456789ABC}", parsed.Arguments);
    }

    [Fact]
    public void Msi_command_without_extension_is_converted_to_interactive_uninstall()
    {
        var parsed = UninstallerLauncher.ParseExecutable("msiexec /I {12345678-1234-1234-1234-123456789ABC}");
        Assert.Equal("msiexec", parsed.FileName);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789ABC}", parsed.Arguments);
    }

    [Fact]
    public async Task Leftover_scanner_requires_exact_publisher_and_product_path_components()
    {
        using var sandbox = new TestSandbox();
        var root = Path.Combine(sandbox.Root, "appdata");
        var productOnly = Path.Combine(root, "SampleProduct");
        var matched = Path.Combine(root, "AcmePublisher", "SampleProduct");
        Directory.CreateDirectory(productOnly);
        Directory.CreateDirectory(matched);
        await File.WriteAllTextAsync(Path.Combine(matched, "settings.json"), "{}");
        var application = new InstalledApplication(
            "test-id",
            "Sample Product",
            "Acme Publisher",
            "1.0",
            null,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            "test-registry-key",
            false,
            null,
            "Sandbox fixture");

        var candidates = await new LeftoverScanner([root]).ScanAsync(application);
        var candidate = Assert.Single(candidates);
        Assert.Equal(Path.GetFullPath(matched), candidate.Path);
        Assert.Equal(ConfidenceLevel.Medium, candidate.Confidence);
        Assert.False(candidate.IsSelectedByDefault);
        Assert.Equal(2, candidate.SizeBytes);
    }

    [Fact]
    public void Reparse_point_detection_identifies_directory_junctions()
    {
        using var sandbox = new TestSandbox();
        var target = Path.Combine(sandbox.Root, "target");
        var link = Path.Combine(sandbox.Root, "link");
        Directory.CreateDirectory(target);
        sandbox.CreateJunction(link, target);
        Assert.True(DeletionPathPolicy.ContainsReparsePoint(link));
    }

    [Fact]
    public async Task Quarantine_refuses_a_folder_that_contains_a_junction()
    {
        using var sandbox = new TestSandbox();
        var allowed = Path.Combine(sandbox.Root, "allowed");
        var source = Path.Combine(allowed, "Vendor", "Product");
        var target = Path.Combine(sandbox.Root, "outside");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        var importantFile = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(importantFile, "keep");
        sandbox.CreateJunction(Path.Combine(source, "linked"), target);
        Assert.True(DeletionPathPolicy.ContainsReparsePointTree(source));

        var database = new CleanLensDatabase(Path.Combine(sandbox.Root, "store", "cleanlens.db"));
        var service = new QuarantineService(Path.Combine(sandbox.Root, "quarantine"), database, new DeletionPathPolicy([allowed]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MoveAsync(source, "Product"));
        Assert.True(Directory.Exists(source));
        Assert.Equal("keep", await File.ReadAllTextAsync(importantFile));
        Assert.Empty(await database.GetQuarantineAsync());
    }

    [Fact]
    public async Task Local_database_can_be_initialized_again_without_losing_records()
    {
        using var sandbox = new TestSandbox();
        var databasePath = Path.Combine(sandbox.Root, "store", "cleanlens.db");
        var database = new CleanLensDatabase(databasePath);
        await database.RecordOperationAsync("Sample", "Scan", "Complete");

        var reopened = new CleanLensDatabase(databasePath);
        var entry = Assert.Single(await reopened.GetHistoryAsync());
        Assert.Equal("Sample", entry.ApplicationName);
        Assert.Equal("Complete", entry.Result);
    }

    [Fact]
    public async Task Quarantine_move_and_restore_preserve_fixture_without_deleting_it()
    {
        using var sandbox = new TestSandbox();
        var allowed = Path.Combine(sandbox.Root, "allowed");
        var source = Path.Combine(allowed, "Vendor", "Product");
        var quarantineRoot = Path.Combine(sandbox.Root, "quarantine");
        var database = new CleanLensDatabase(Path.Combine(sandbox.Root, "store", "cleanlens.db"));
        var service = new QuarantineService(quarantineRoot, database, new DeletionPathPolicy([allowed]));
        Directory.CreateDirectory(source);
        var fixture = Path.Combine(source, "settings.json");
        await File.WriteAllTextAsync(fixture, "{\"preserve\":true}");

        var operationId = await service.MoveAsync(source, "Product");
        var rows = await database.GetQuarantineAsync();
        Assert.False(Directory.Exists(source));
        Assert.Single(rows);
        Assert.Equal(operationId, rows[0].OperationId);
        Assert.Equal("{\"preserve\":true}", await File.ReadAllTextAsync(Path.Combine(rows[0].QuarantinePath, "settings.json")));

        await service.RestoreAsync(rows[0]);
        Assert.Equal("{\"preserve\":true}", await File.ReadAllTextAsync(fixture));
        Assert.Empty(await database.GetQuarantineAsync());
    }

    [Fact]
    public async Task Quarantine_restore_refuses_to_overwrite_an_existing_original_path()
    {
        using var sandbox = new TestSandbox();
        var allowed = Path.Combine(sandbox.Root, "allowed");
        var source = Path.Combine(allowed, "Vendor", "Product");
        var database = new CleanLensDatabase(Path.Combine(sandbox.Root, "store", "cleanlens.db"));
        var service = new QuarantineService(Path.Combine(sandbox.Root, "quarantine"), database, new DeletionPathPolicy([allowed]));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "before.txt"), "before");
        await service.MoveAsync(source, "Product");
        var entry = Assert.Single(await database.GetQuarantineAsync());
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "new.txt"), "new");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(entry));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(source, "new.txt")));
        Assert.True(File.Exists(Path.Combine(entry.QuarantinePath, "before.txt")));
    }

    private sealed class TestSandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CleanLens.Tests", Guid.NewGuid().ToString("N"));

        public TestSandbox()
        {
            Directory.CreateDirectory(Root);
        }

        public void CreateJunction(string link, string target)
        {
            SafetyTests.CreateDirectoryJunction(link, target);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                try
                {
                    Directory.Delete(Root, recursive: true);
                }
                catch (UnauthorizedAccessException)
                {
                    var sandboxParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CleanLens.Tests"));
                    if (!Path.GetFullPath(Root).StartsWith(sandboxParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw;
                    }
                    var startInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
                    startInfo.ArgumentList.Add("/d");
                    startInfo.ArgumentList.Add("/c");
                    startInfo.ArgumentList.Add("rmdir");
                    startInfo.ArgumentList.Add("/s");
                    startInfo.ArgumentList.Add("/q");
                    startInfo.ArgumentList.Add(Root);
                    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not remove the isolated test sandbox.");
                    process.WaitForExit();
                    Assert.Equal(0, process.ExitCode);
                }
            }
        }
    }

    private static void CreateDirectoryJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not create the temporary junction fixture.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

}
