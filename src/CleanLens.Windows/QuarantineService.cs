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

    public Task<string> MoveAsync(
        string sourcePath,
        string applicationName,
        string operationText = "Quarantine",
        string resultText = "Moved to local quarantine",
        CancellationToken cancellationToken = default) =>
        MoveCoreAsync(sourcePath, applicationName, pathPolicy, null, operationText, resultText, cancellationToken);

    public Task<string> MoveManualCandidateAsync(
        string sourcePath,
        string applicationName,
        DeletionPathPolicy candidatePolicy,
        Func<string, bool> candidateValidator,
        string operationText,
        string resultText,
        CancellationToken cancellationToken = default) =>
        MoveCoreAsync(sourcePath, applicationName, candidatePolicy, candidateValidator, operationText, resultText, cancellationToken);

    private async Task<string> MoveCoreAsync(
        string sourcePath,
        string applicationName,
        DeletionPathPolicy sourcePolicy,
        Func<string, bool>? candidateValidator,
        string operationText,
        string resultText,
        CancellationToken cancellationToken)
    {
        if (!sourcePolicy.TryValidate(sourcePath, out var source, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        var sourceIsDirectory = Directory.Exists(source);
        var sourceIsFile = File.Exists(source);
        if ((!sourceIsDirectory && !sourceIsFile) || HasReparsePoint(source, sourceIsDirectory) || (candidateValidator is not null && !candidateValidator(source)))
        {
            throw new InvalidOperationException("The selected path is missing, unsafe or no longer matches the selected application.");
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
        var metadata = JsonSerializer.Serialize(new QuarantineMetadata(operationId, applicationName, source, DateTimeOffset.UtcNow, candidateValidator is null ? null : sourcePolicy.AllowedRoots.ToArray()));
        await File.WriteAllTextAsync(Path.Combine(operationDirectory, "metadata.json"), metadata, cancellationToken);
        try
        {
            if ((sourceIsDirectory && !Directory.Exists(source)) || (sourceIsFile && !File.Exists(source)) ||
                !sourcePolicy.TryValidate(source, out var latestSource, out _) ||
                !latestSource.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                HasReparsePoint(source, sourceIsDirectory) ||
                (candidateValidator is not null && !candidateValidator(source)))
            {
                throw new InvalidOperationException("The source path changed or gained a reparse point before quarantine.");
            }
            MovePath(source, payload, sourceIsDirectory);
            await database.RecordQuarantineAsync(operationId, source, payload, applicationName, cancellationToken);
            await database.RecordOperationAsync(applicationName, operationText, resultText, cancellationToken);
            return operationId;
        }
        catch
        {
            if ((Directory.Exists(payload) || File.Exists(payload)) && !Directory.Exists(source) && !File.Exists(source))
            {
                MovePath(payload, source, sourceIsDirectory);
            }
            await database.RemoveQuarantineAsync(operationId);
            RemoveOperationDirectory(operationDirectory);
            throw;
        }
    }

    public async Task RestoreAsync(
        QuarantineEntry entry,
        string operationText = "Restore",
        string resultText = "Restored from local quarantine",
        CancellationToken cancellationToken = default)
    {
        var payload = Path.GetFullPath(entry.QuarantinePath);
        var original = Path.GetFullPath(entry.OriginalPath);
        var payloadIsDirectory = Directory.Exists(payload);
        var payloadIsFile = File.Exists(payload);
        if (!DeletionPathPolicy.IsPathWithin(payload, quarantineRoot) ||
            (!payloadIsDirectory && !payloadIsFile) ||
            Directory.Exists(original) ||
            File.Exists(original) ||
            HasReparsePoint(payload, payloadIsDirectory))
        {
            throw new InvalidOperationException("Restore was refused because the saved path is invalid, occupied or contains a reparse point.");
        }

        var restorePolicy = GetRestorePolicy(entry, payload, original);
        if (!restorePolicy.TryValidate(original, out _, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        var originalParent = Path.GetDirectoryName(original)!;
        if (!Directory.Exists(originalParent) || DeletionPathPolicy.ContainsReparsePoint(originalParent))
        {
            throw new InvalidOperationException("Restore was refused because the original parent folder is missing or contains a reparse point.");
        }
        MovePath(payload, original, payloadIsDirectory);
        await database.RemoveQuarantineAsync(entry.OperationId, cancellationToken);
        await database.RecordOperationAsync(entry.ApplicationName, operationText, resultText, cancellationToken);
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

    private DeletionPathPolicy GetRestorePolicy(QuarantineEntry entry, string payload, string original)
    {
        var metadataPath = Path.Combine(Path.GetDirectoryName(payload)!, "metadata.json");
        try
        {
            if (!File.Exists(metadataPath) || DeletionPathPolicy.ContainsReparsePoint(metadataPath))
            {
                return pathPolicy;
            }
            var metadata = JsonSerializer.Deserialize<QuarantineMetadata>(File.ReadAllText(metadataPath));
            if (metadata is null || metadata.OperationId != entry.OperationId ||
                !string.Equals(metadata.OriginalPath, original, StringComparison.OrdinalIgnoreCase) ||
                metadata.AllowedRoots is not { Length: > 0 } roots)
            {
                return pathPolicy;
            }
            return new DeletionPathPolicy(roots, [Environment.GetFolderPath(Environment.SpecialFolder.Windows)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException)
        {
            return pathPolicy;
        }
    }

    private static bool HasReparsePoint(string path, bool isDirectory) =>
        isDirectory ? DeletionPathPolicy.ContainsReparsePointTree(path) : DeletionPathPolicy.ContainsReparsePoint(path);

    private static void MovePath(string source, string destination, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private sealed record QuarantineMetadata(string OperationId, string ApplicationName, string OriginalPath, DateTimeOffset CreatedAt, string[]? AllowedRoots = null);
}
