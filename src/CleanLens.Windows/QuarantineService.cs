using System.Text.Json;
using CleanLens.Core.Safety;
using CleanLens.Data;

namespace CleanLens.Windows;

public sealed class QuarantineService
{
    private readonly string quarantineRoot;
    private readonly CleanLensDatabase database;
    private readonly DeletionPathPolicy pathPolicy;

    public QuarantineService(string quarantineRoot, CleanLensDatabase database, DeletionPathPolicy pathPolicy)
    {
        this.quarantineRoot = Path.GetFullPath(quarantineRoot);
        this.database = database;
        this.pathPolicy = pathPolicy;
        var parent = Path.GetDirectoryName(this.quarantineRoot) ?? throw new InvalidOperationException("The quarantine location has no parent directory.");
        if (Directory.Exists(this.quarantineRoot))
        {
            if (DeletionPathPolicy.ContainsReparsePoint(this.quarantineRoot))
            {
                throw new InvalidOperationException("The quarantine location contains a reparse point.");
            }
        }
        else
        {
            if (Directory.Exists(parent) && DeletionPathPolicy.ContainsReparsePoint(parent))
            {
                throw new InvalidOperationException("The quarantine parent contains a reparse point.");
            }
            Directory.CreateDirectory(this.quarantineRoot);
            if (DeletionPathPolicy.ContainsReparsePoint(this.quarantineRoot))
            {
                throw new InvalidOperationException("The created quarantine location contains a reparse point.");
            }
        }
    }

    public async Task<string> MoveAsync(string sourcePath, string applicationName, CancellationToken cancellationToken = default)
    {
        if (!pathPolicy.TryValidate(sourcePath, out var source, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        if (!Directory.Exists(source) || DeletionPathPolicy.ContainsReparsePointTree(source))
        {
            throw new InvalidOperationException("Only existing directories without reparse points can be moved to quarantine.");
        }

        var operationId = Guid.NewGuid().ToString("N");
        var operationDirectory = Path.Combine(quarantineRoot, operationId);
        var payload = Path.Combine(operationDirectory, "payload");
        Directory.CreateDirectory(operationDirectory);
        if (DeletionPathPolicy.ContainsReparsePoint(quarantineRoot) || DeletionPathPolicy.ContainsReparsePoint(operationDirectory))
        {
            if (!DeletionPathPolicy.ContainsReparsePoint(quarantineRoot))
            {
                Directory.Delete(operationDirectory);
            }
            throw new InvalidOperationException("The quarantine path contains a reparse point.");
        }
        var metadata = JsonSerializer.Serialize(new QuarantineMetadata(operationId, applicationName, source, DateTimeOffset.UtcNow));
        await File.WriteAllTextAsync(Path.Combine(operationDirectory, "metadata.json"), metadata, cancellationToken);
        try
        {
            if (!Directory.Exists(source) ||
                !pathPolicy.TryValidate(source, out var latestSource, out _) ||
                !latestSource.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                DeletionPathPolicy.ContainsReparsePointTree(source))
            {
                throw new InvalidOperationException("The source path changed or gained a reparse point before quarantine.");
            }
            Directory.Move(source, payload);
            await database.RecordQuarantineAsync(operationId, source, payload, applicationName, cancellationToken);
            await database.RecordOperationAsync(applicationName, "Quarantine", "Moved to local quarantine", cancellationToken);
            return operationId;
        }
        catch
        {
            if (Directory.Exists(payload) && !Directory.Exists(source))
            {
                Directory.Move(payload, source);
            }
            await database.RemoveQuarantineAsync(operationId);
            RemoveOperationDirectory(operationDirectory);
            throw;
        }
    }

    public async Task RestoreAsync(QuarantineEntry entry, CancellationToken cancellationToken = default)
    {
        var payload = Path.GetFullPath(entry.QuarantinePath);
        var original = Path.GetFullPath(entry.OriginalPath);
        if (!DeletionPathPolicy.IsPathWithin(payload, quarantineRoot) ||
            !Directory.Exists(payload) ||
            Directory.Exists(original) ||
            File.Exists(original) ||
            DeletionPathPolicy.ContainsReparsePointTree(payload))
        {
            throw new InvalidOperationException("Restore was refused because the saved path is invalid, occupied or contains a reparse point.");
        }

        if (!pathPolicy.TryValidate(original, out _, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        var originalParent = Path.GetDirectoryName(original)!;
        if (!Directory.Exists(originalParent) || DeletionPathPolicy.ContainsReparsePoint(originalParent))
        {
            throw new InvalidOperationException("Restore was refused because the original parent folder is missing or contains a reparse point.");
        }
        Directory.Move(payload, original);
        await database.RemoveQuarantineAsync(entry.OperationId, cancellationToken);
        await database.RecordOperationAsync(entry.ApplicationName, "Restore", "Restored from local quarantine", cancellationToken);
        RemoveOperationDirectory(Path.GetDirectoryName(payload)!);
    }

    private static void RemoveOperationDirectory(string operationDirectory)
    {
        if (!Directory.Exists(operationDirectory) || DeletionPathPolicy.ContainsReparsePoint(operationDirectory))
        {
            return;
        }
        var entries = Directory.EnumerateFileSystemEntries(operationDirectory).ToArray();
        foreach (var entry in entries)
        {
            if (Path.GetFileName(entry).Equals("metadata.json", StringComparison.OrdinalIgnoreCase) &&
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(entry);
            }
        }
        if (!Directory.EnumerateFileSystemEntries(operationDirectory).Any())
        {
            Directory.Delete(operationDirectory);
        }
    }

    private sealed record QuarantineMetadata(string OperationId, string ApplicationName, string OriginalPath, DateTimeOffset CreatedAt);
}
