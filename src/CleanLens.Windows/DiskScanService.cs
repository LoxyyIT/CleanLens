using System.IO.Enumeration;
using System.Security;
using System.Text;
using CleanLens.Core.Safety;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace CleanLens.Windows;

public enum DiskListingMode
{
    CurrentFolder,
    LargestItems,
    LargestFiles
}

public sealed record DiskScanRootOption(string DisplayName, string Path)
{
    public override string ToString() => DisplayName;
}

public sealed record DiskScanProgress(long EntriesVisited, long BytesMeasured, long SkippedEntries, bool BuildingIndex = false);

public sealed record DiskScanSummary(string RootPath, long TotalBytes, long FileCount, long FolderCount, long SkippedCount)
{
    public bool IsIncomplete => SkippedCount > 0;
    public string SizeText => $"{(IsIncomplete ? "≥ " : string.Empty)}{FormatBytes(TotalBytes)}";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed record DiskScanEntry(
    string Name,
    string Path,
    string ParentPath,
    bool IsDirectory,
    bool IsReparsePoint,
    long? SizeBytes,
    string Extension,
    long Attributes,
    long? LastWriteUtcTicks,
    long FileCount,
    long FolderCount,
    long SkippedCount,
    bool IsIncomplete)
{
    public string SizeText => SizeBytes is null ? "—" : $"{(IsIncomplete ? "≥ " : string.Empty)}{FormatBytes(SizeBytes.Value)}";
    public DateTimeOffset? LastWriteUtc => LastWriteUtcTicks is null ? null : new DateTimeOffset(LastWriteUtcTicks.Value, TimeSpan.Zero);
    public string LastWriteText => LastWriteUtc?.ToLocalTime().ToString("g") ?? string.Empty;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed record DiskScanPage(IReadOnlyList<DiskScanEntry> Entries, long TotalCount, long Offset, int PageSize);

public sealed record DiskExtensionStat(string Extension, long FileCount, long Bytes, double Percent)
{
    public string SizeText => DiskScanSummaryFormatter.Format(Bytes);
}

public sealed record DiskDeletionFailure(string Path, string Error);

public sealed record DiskDeletionReport(int DeletedItems, IReadOnlyList<DiskDeletionFailure> Failures, DiskScanSummary? UpdatedSummary, bool RequiresRescan);

internal static class DiskScanSummaryFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed class DiskScanService : IDisposable
{
    private const int TransactionBatchSize = 4000;
    private const int MaximumTreeDepth = 256;
    private readonly string cacheRoot;
    private readonly string connectionString;
    private readonly string cacheExclusionPath;
    private readonly Dictionary<string, IReadOnlyList<DiskExtensionStat>> extensionStatsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object extensionStatsSync = new();
    private string? currentIndexPath;
    private string? currentRootPath;
    private DiskScanSummary? currentSummary;
    private bool currentScanIsStale;

    public bool HasScan => currentIndexPath is not null && currentRootPath is not null && currentSummary is not null;
    public bool IsStale => currentScanIsStale;
    public DiskScanSummary? CurrentSummary => currentSummary;
    public string? CurrentRootPath => currentRootPath;

    public DiskScanService(string localDataPath)
    {
        cacheRoot = Path.GetFullPath(Path.Combine(localDataPath, "DiskScanCache"));
        cacheExclusionPath = cacheRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Directory.CreateDirectory(cacheRoot);
        connectionString = new SqliteConnectionStringBuilder
        {
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 10
        }.ToString();
        RemoveAbandonedIndexes();
    }

    public IReadOnlyList<DiskScanRootOption> GetScanRoots()
    {
        var roots = new List<DiskScanRootOption>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.Name} · {drive.VolumeLabel}";
                roots.Add(new DiskScanRootOption(label, Path.GetFullPath(drive.RootDirectory.FullName)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.Security.SecurityException or ArgumentException)
            {
            }
        }
        return roots.OrderBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string ValidateRoot(string path)
    {
        var canonicalPath = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(canonicalPath)) throw new DirectoryNotFoundException(canonicalPath);
        if ((File.GetAttributes(canonicalPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Choose the target directory itself. CleanLens does not scan through junctions or symbolic links.");
        if (IsPathWithinOrEqual(canonicalPath, cacheExclusionPath)) throw new InvalidOperationException("The CleanLens scan cache cannot be scanned.");
        return canonicalPath;
    }

    public async Task<DiskScanSummary> ScanAsync(string rootPath, IProgress<DiskScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = ValidateRoot(rootPath);
        DeleteCurrentIndex();
        lock (extensionStatsSync) extensionStatsCache.Clear();
        var indexPath = Path.Combine(cacheRoot, $"scan-{Guid.NewGuid():N}.db");
        try
        {
            var summary = await Task.Run(() => ScanCore(root, indexPath, progress, cancellationToken), cancellationToken);
            currentIndexPath = indexPath;
            currentRootPath = root;
            currentSummary = summary;
            currentScanIsStale = false;
            return summary;
        }
        catch
        {
            TryDelete(indexPath);
            throw;
        }
    }

    public async Task<DiskScanPage> GetPageAsync(DiskListingMode mode, string currentDirectory, long offset, int pageSize, string search, CancellationToken cancellationToken = default)
    {
        EnsureCurrentIndex();
        var indexPath = currentIndexPath!;
        var root = currentRootPath!;
        var directory = Path.GetFullPath(currentDirectory);
        if (!IsPathWithinOrEqual(directory, root)) throw new InvalidOperationException("The requested folder is outside the selected scan root.");
        return await Task.Run(() => QueryPage(indexPath, root, directory, mode, Math.Max(0, offset), Math.Clamp(pageSize, 1, 5000), search.Trim(), cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<DiskExtensionStat>> GetExtensionStatsAsync(string? directoryPath = null, CancellationToken cancellationToken = default)
    {
        EnsureCurrentIndex();
        var indexPath = currentIndexPath!;
        var root = currentRootPath!;
        var directory = Path.GetFullPath(directoryPath ?? root);
        if (!IsPathWithinOrEqual(directory, root)) throw new InvalidOperationException("The requested folder is outside the selected scan root.");
        lock (extensionStatsSync)
        {
            if (extensionStatsCache.TryGetValue(directory, out var cached)) return cached;
        }
        var result = await Task.Run(() => QueryExtensions(indexPath, directory, cancellationToken), cancellationToken);
        lock (extensionStatsSync)
        {
            if (extensionStatsCache.Count >= 64) extensionStatsCache.Clear();
            extensionStatsCache[directory] = result;
        }
        return result;
    }

    public async Task<DiskDeletionReport> DeleteSelectedAsync(IEnumerable<DiskScanEntry> selection, CancellationToken cancellationToken = default)
    {
        EnsureCurrentIndex();
        if (currentScanIsStale) throw new InvalidOperationException("Scan this disk or folder again before deleting more items.");
        var selected = selection.GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        if (selected.Length == 0) return new DiskDeletionReport(0, [], currentSummary, false);
        lock (extensionStatsSync) extensionStatsCache.Clear();
        var root = currentRootPath!;
        var cache = cacheExclusionPath;
        var indexPath = currentIndexPath!;
        return await Task.Run(() => DeleteSelectedCore(indexPath, root, cache, selected, cancellationToken), cancellationToken);
    }

    public void Dispose()
    {
        DeleteCurrentIndex();
    }

    private DiskScanSummary ScanCore(string root, string indexPath, IProgress<DiskScanProgress>? progress, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { DataSource = indexPath };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-24000;";
            setup.ExecuteNonQuery();
            setup.CommandText = """
                CREATE TABLE disk_entries (
                    path TEXT PRIMARY KEY COLLATE NOCASE,
                    parent_path TEXT NOT NULL COLLATE NOCASE,
                    name TEXT NOT NULL,
                    is_directory INTEGER NOT NULL,
                    is_reparse INTEGER NOT NULL,
                    size_bytes INTEGER NULL,
                    extension TEXT NOT NULL,
                    attributes INTEGER NOT NULL,
                    last_write_ticks INTEGER NULL,
                    file_count INTEGER NOT NULL,
                    folder_count INTEGER NOT NULL,
                    skipped_count INTEGER NOT NULL,
                    is_incomplete INTEGER NOT NULL
                ) WITHOUT ROWID;
                """;
            setup.ExecuteNonQuery();
        }

        long visited = 0;
        long skipped = 0;
        long bytesMeasured = 0;
        long lastReported = 0;
        var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO disk_entries(path,parent_path,name,is_directory,is_reparse,size_bytes,extension,attributes,last_write_ticks,file_count,folder_count,skipped_count,is_incomplete) VALUES($path,$parent,$name,$directory,$reparse,$size,$extension,$attributes,$modified,$files,$folders,$skipped,$incomplete)";
        var parameters = new Dictionary<string, SqliteParameter>(StringComparer.Ordinal)
        {
            ["$path"] = insert.Parameters.Add("$path", SqliteType.Text),
            ["$parent"] = insert.Parameters.Add("$parent", SqliteType.Text),
            ["$name"] = insert.Parameters.Add("$name", SqliteType.Text),
            ["$directory"] = insert.Parameters.Add("$directory", SqliteType.Integer),
            ["$reparse"] = insert.Parameters.Add("$reparse", SqliteType.Integer),
            ["$size"] = insert.Parameters.Add("$size", SqliteType.Integer),
            ["$extension"] = insert.Parameters.Add("$extension", SqliteType.Text),
            ["$attributes"] = insert.Parameters.Add("$attributes", SqliteType.Integer),
            ["$modified"] = insert.Parameters.Add("$modified", SqliteType.Integer),
            ["$files"] = insert.Parameters.Add("$files", SqliteType.Integer),
            ["$folders"] = insert.Parameters.Add("$folders", SqliteType.Integer),
            ["$skipped"] = insert.Parameters.Add("$skipped", SqliteType.Integer),
            ["$incomplete"] = insert.Parameters.Add("$incomplete", SqliteType.Integer)
        };
        var pendingWrites = 0;
        void CommitBatch()
        {
            transaction.Commit();
            transaction.Dispose();
            transaction = connection.BeginTransaction();
            insert.Transaction = transaction;
            pendingWrites = 0;
        }
        void StoreEntry(DiskScannedItem item, string parentPath, long? sizeBytes, long fileCount, long folderCount, long skippedCount, bool incomplete)
        {
            parameters["$path"].Value = item.Path;
            parameters["$parent"].Value = parentPath;
            parameters["$name"].Value = item.Name;
            parameters["$directory"].Value = item.IsDirectory ? 1 : 0;
            parameters["$reparse"].Value = item.IsReparsePoint ? 1 : 0;
            parameters["$size"].Value = sizeBytes is null ? DBNull.Value : sizeBytes.Value;
            parameters["$extension"].Value = item.IsDirectory ? string.Empty : GetExtension(item.Name);
            parameters["$attributes"].Value = (long)item.Attributes;
            parameters["$modified"].Value = item.LastWriteUtcTicks;
            parameters["$files"].Value = fileCount;
            parameters["$folders"].Value = folderCount;
            parameters["$skipped"].Value = skippedCount;
            parameters["$incomplete"].Value = incomplete ? 1 : 0;
            insert.ExecuteNonQuery();
            pendingWrites++;
            if (pendingWrites >= TransactionBatchSize) CommitBatch();
        }
        void ReportProgress(bool force = false)
        {
            if (force || visited - lastReported >= 512)
            {
                progress?.Report(new DiskScanProgress(visited, bytesMeasured, skipped));
                lastReported = visited;
            }
        }

        var stack = new Stack<ScanFrame>();
        var rootItem = GetRootItem(root);
        stack.Push(CreateFrame(rootItem, string.Empty, 0, true));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = stack.Peek();
            bool hasNext;
            try
            {
                var errorsBefore = frame.Enumerator.ErrorCount;
                hasNext = frame.Enumerator.MoveNext();
                var errors = frame.Enumerator.ErrorCount - errorsBefore;
                if (errors > 0)
                {
                    frame.SkippedCount += errors;
                    frame.IsIncomplete = true;
                    skipped += errors;
                }
            }
            catch (Exception ex) when (IsScanAccessException(ex))
            {
                frame.SkippedCount++;
                frame.IsIncomplete = true;
                skipped++;
                hasNext = false;
            }

            if (!hasNext)
            {
                frame.Enumerator.Dispose();
                stack.Pop();
                var folderItem = frame.Item with { IsDirectory = true };
                StoreEntry(folderItem, frame.ParentPath, frame.IsIncomplete && frame.SkippedCount > 0 && frame.FileCount == 0 && frame.FolderCount == 0 ? null : frame.Bytes,
                    frame.FileCount, frame.FolderCount, frame.SkippedCount, frame.IsIncomplete);
                if (frame.IsRoot)
                {
                    ReportProgress(force: true);
                    transaction.Commit();
                    transaction.Dispose();
                    progress?.Report(new DiskScanProgress(visited, bytesMeasured, skipped, BuildingIndex: true));
                    using var indexes = connection.CreateCommand();
                    indexes.CommandText = "CREATE INDEX ix_disk_parent_size ON disk_entries(parent_path, size_bytes DESC, name COLLATE NOCASE); CREATE INDEX ix_disk_size ON disk_entries(size_bytes DESC, path COLLATE NOCASE); CREATE INDEX ix_disk_extension ON disk_entries(extension, size_bytes);";
                    indexes.ExecuteNonQuery();
                    cancellationToken.ThrowIfCancellationRequested();
                    return new DiskScanSummary(root, frame.Bytes, frame.FileCount, frame.FolderCount, skipped);
                }

                var parent = stack.Peek();
                parent.Bytes = SafeAdd(parent.Bytes, frame.Bytes);
                parent.FileCount = SafeAdd(parent.FileCount, frame.FileCount);
                parent.FolderCount = SafeAdd(parent.FolderCount, frame.FolderCount + 1);
                parent.SkippedCount = SafeAdd(parent.SkippedCount, frame.SkippedCount);
                parent.IsIncomplete |= frame.IsIncomplete;
                visited++;
                ReportProgress();
                continue;
            }

            var entry = frame.Enumerator.Current;
            if (IsPathWithinOrEqual(entry.Path, cacheExclusionPath)) continue;
            visited++;
            try
            {
                if (!DeletionPathPolicy.IsPathWithin(entry.Path, root))
                {
                    frame.SkippedCount++;
                    frame.IsIncomplete = true;
                    skipped++;
                    continue;
                }

                if (entry.IsDirectory)
                {
                    if (entry.IsReparsePoint)
                    {
                        StoreEntry(entry, frame.Item.Path, null, 0, 0, 1, true);
                        frame.FolderCount++;
                        frame.SkippedCount++;
                        frame.IsIncomplete = true;
                        skipped++;
                        ReportProgress();
                        continue;
                    }
                    if (stack.Count >= MaximumTreeDepth)
                    {
                        StoreEntry(entry, frame.Item.Path, null, 0, 0, 1, true);
                        frame.FolderCount++;
                        frame.SkippedCount++;
                        frame.IsIncomplete = true;
                        skipped++;
                        ReportProgress();
                        continue;
                    }

                    ScanFrame child;
                    try
                    {
                        child = CreateFrame(entry, frame.Item.Path, stack.Count, false);
                    }
                    catch (Exception ex) when (IsScanAccessException(ex))
                    {
                        StoreEntry(entry, frame.Item.Path, null, 0, 0, 1, true);
                        frame.FolderCount++;
                        frame.SkippedCount++;
                        frame.IsIncomplete = true;
                        skipped++;
                        ReportProgress();
                        continue;
                    }
                    stack.Push(child);
                    continue;
                }

                if (entry.IsReparsePoint)
                {
                    StoreEntry(entry, frame.Item.Path, null, 1, 0, 1, true);
                    frame.FileCount++;
                    frame.SkippedCount++;
                    frame.IsIncomplete = true;
                    skipped++;
                    ReportProgress();
                    continue;
                }

                StoreEntry(entry, frame.Item.Path, entry.Length, 1, 0, 0, false);
                frame.FileCount++;
                frame.Bytes = SafeAdd(frame.Bytes, entry.Length);
                bytesMeasured = SafeAdd(bytesMeasured, entry.Length);
                ReportProgress();
            }
            catch (Exception ex) when (IsScanAccessException(ex) || ex is OverflowException)
            {
                frame.SkippedCount++;
                frame.IsIncomplete = true;
                skipped++;
                ReportProgress();
            }
        }
        throw new InvalidOperationException("The disk scan ended unexpectedly.");
    }

    private DiskScanPage QueryPage(string indexPath, string rootPath, string currentDirectory, DiskListingMode mode, long offset, int pageSize, string search, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OpenIndex(indexPath);
        var where = mode switch
        {
            DiskListingMode.CurrentFolder => "parent_path=$location",
            DiskListingMode.LargestFiles => "is_directory=0 AND is_reparse=0 AND size_bytes IS NOT NULL AND path<>$root",
            _ => "size_bytes IS NOT NULL AND path<>$root"
        };
        if (!string.IsNullOrWhiteSpace(search)) where += " AND (instr(lower(name),lower($search))>0 OR instr(lower(path),lower($search))>0)";
        using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM disk_entries WHERE {where}";
        count.Parameters.AddWithValue("$location", currentDirectory);
        count.Parameters.AddWithValue("$root", rootPath);
        if (!string.IsNullOrWhiteSpace(search)) count.Parameters.AddWithValue("$search", search);
        var total = Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);

        using var query = connection.CreateCommand();
        query.CommandText = $"""
            SELECT name,path,parent_path,is_directory,is_reparse,size_bytes,extension,attributes,last_write_ticks,file_count,folder_count,skipped_count,is_incomplete
            FROM disk_entries WHERE {where}
            ORDER BY (size_bytes IS NULL), size_bytes DESC, name COLLATE NOCASE, path COLLATE NOCASE
            LIMIT $limit OFFSET $offset
            """;
        query.Parameters.AddWithValue("$location", currentDirectory);
        query.Parameters.AddWithValue("$root", rootPath);
        query.Parameters.AddWithValue("$limit", pageSize);
        query.Parameters.AddWithValue("$offset", offset);
        if (!string.IsNullOrWhiteSpace(search)) query.Parameters.AddWithValue("$search", search);
        using var reader = query.ExecuteReader();
        var entries = new List<DiskScanEntry>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(ReadEntry(reader));
        }
        return new DiskScanPage(entries, total, offset, pageSize);
    }

    private static IReadOnlyList<DiskExtensionStat> QueryExtensions(string indexPath, string directoryPath, CancellationToken cancellationToken)
    {
        using var connection = OpenIndex(indexPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE folders(path) AS (
                SELECT path FROM disk_entries WHERE path=$folder AND is_directory=1 AND is_reparse=0
                UNION ALL
                SELECT child.path FROM disk_entries AS child JOIN folders AS parent ON child.parent_path=parent.path
                WHERE child.is_directory=1 AND child.is_reparse=0
            )
            SELECT file.extension,COUNT(*),SUM(file.size_bytes)
            FROM disk_entries AS file JOIN folders ON file.parent_path=folders.path
            WHERE file.is_directory=0 AND file.is_reparse=0 AND file.size_bytes IS NOT NULL AND file.extension<>''
            GROUP BY file.extension ORDER BY SUM(file.size_bytes) DESC LIMIT 8
            """;
        command.Parameters.AddWithValue("$folder", directoryPath);
        using var reader = command.ExecuteReader();
        var values = new List<DiskExtensionStat>();
        long totalBytes = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
            totalBytes = SafeAdd(totalBytes, bytes);
            values.Add(new DiskExtensionStat(reader.GetString(0), reader.GetInt64(1), bytes, 0));
        }
        if (totalBytes == 0) return values;
        return values.Select(value => value with { Percent = (double)value.Bytes / totalBytes * 100 }).ToArray();
    }

    private DiskDeletionReport DeleteSelectedCore(string indexPath, string rootPath, string excludedCache, IReadOnlyList<DiskScanEntry> selection, CancellationToken cancellationToken)
    {
        using var connection = OpenIndex(indexPath);
        var selected = selection.Where(item => !selection.Any(parent => !parent.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && DeletionPathPolicy.IsPathWithin(item.Path, parent.Path)))
            .OrderBy(item => item.Path.Length).ToArray();
        var failures = new List<DiskDeletionFailure>();
        var deleted = 0;
        var invalidate = false;

        foreach (var item in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(item.Path);
            if (!DeletionPathPolicy.IsPathWithin(path, rootPath) || IsPathWithinOrEqual(path, excludedCache))
            {
                failures.Add(new DiskDeletionFailure(path, "The item is outside the selected scan root or belongs to the CleanLens scan index."));
                invalidate = true;
                continue;
            }
            if (Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(new DiskDeletionFailure(path, "The selected scan root cannot be deleted."));
                continue;
            }

            var indexed = FindEntry(connection, path);
            if (indexed is null)
            {
                failures.Add(new DiskDeletionFailure(path, "The path is no longer in this scan. Scan again to refresh the results."));
                invalidate = true;
                continue;
            }
            if (indexed.IsIncomplete) invalidate = true;
            DeleteOutcome outcome;
            try { outcome = DeleteTree(path, rootPath, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsScanAccessException(ex) || ex is InvalidOperationException)
            {
                failures.Add(new DiskDeletionFailure(path, ex.Message));
                invalidate = true;
                continue;
            }
            if (outcome.Failures.Count > 0)
            {
                failures.AddRange(outcome.Failures);
                invalidate = true;
                continue;
            }

            deleted++;
            using var transaction = connection.BeginTransaction();
            RemoveIndexedSubtree(connection, transaction, path);
            if (!indexed.IsIncomplete && indexed.SizeBytes is long knownBytes)
            {
                UpdateAncestors(connection, transaction, rootPath, path, knownBytes, indexed.FileCount, indexed.IsDirectory ? indexed.FolderCount + 1 : 0, indexed.SkippedCount);
            }
            else
            {
                invalidate = true;
            }
            transaction.Commit();
        }

        if (failures.Count > 0) invalidate = true;
        currentScanIsStale = invalidate;
        currentSummary = invalidate ? currentSummary : ReadRootSummary(connection, rootPath);
        return new DiskDeletionReport(deleted, failures, currentSummary, invalidate);
    }

    private static DeleteOutcome DeleteTree(string selectedPath, string scanRoot, CancellationToken cancellationToken)
    {
        var failures = new List<DiskDeletionFailure>();
        var rootAttributes = File.GetAttributes(selectedPath);
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0)
        {
            DeleteLink(selectedPath, rootAttributes);
            return new DeleteOutcome(failures);
        }
        if ((rootAttributes & FileAttributes.Directory) == 0)
        {
            File.Delete(selectedPath);
            return new DeleteOutcome(failures);
        }

        var frames = new Stack<DeleteFrame>();
        if ((File.GetAttributes(selectedPath) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(selectedPath, recursive: false);
            return new DeleteOutcome(failures);
        }
        frames.Push(new DeleteFrame(selectedPath, new DiskEntryEnumerator(selectedPath)));
        while (frames.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames.Peek();
            bool hasNext;
            try { hasNext = frame.Enumerator.MoveNext(); }
            catch (Exception ex) when (IsScanAccessException(ex))
            {
                failures.Add(new DiskDeletionFailure(frame.Path, ex.Message));
                frame.Enumerator.Dispose();
                frames.Pop();
                continue;
            }
            if (!hasNext)
            {
                frame.Enumerator.Dispose();
                frames.Pop();
                try { Directory.Delete(frame.Path, recursive: false); }
                catch (Exception ex) when (IsScanAccessException(ex) || ex is InvalidOperationException) { failures.Add(new DiskDeletionFailure(frame.Path, ex.Message)); }
                continue;
            }

            var entry = frame.Enumerator.Current;
            if (!DeletionPathPolicy.IsPathWithin(entry.Path, scanRoot))
            {
                failures.Add(new DiskDeletionFailure(entry.Path, "The item is outside the selected scan root."));
                continue;
            }
            try
            {
                var currentAttributes = File.GetAttributes(entry.Path);
                if ((currentAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == (FileAttributes.Directory | FileAttributes.ReparsePoint))
                {
                    Directory.Delete(entry.Path, recursive: false);
                }
                else if ((currentAttributes & FileAttributes.Directory) != 0)
                {
                    if ((File.GetAttributes(entry.Path) & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(entry.Path, recursive: false);
                        continue;
                    }
                    frames.Push(new DeleteFrame(entry.Path, new DiskEntryEnumerator(entry.Path)));
                }
                else
                {
                    File.Delete(entry.Path);
                }
            }
            catch (Exception ex) when (IsScanAccessException(ex) || ex is InvalidOperationException)
            {
                failures.Add(new DiskDeletionFailure(entry.Path, ex.Message));
            }
        }
        return new DeleteOutcome(failures);
    }

    private static void DeleteLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    private static DiskScanEntry? FindEntry(SqliteConnection connection, string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,path,parent_path,is_directory,is_reparse,size_bytes,extension,attributes,last_write_ticks,file_count,folder_count,skipped_count,is_incomplete FROM disk_entries WHERE path=$path";
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    private static void RemoveIndexedSubtree(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH RECURSIVE descendants(path) AS (
                SELECT path FROM disk_entries WHERE path=$path
                UNION ALL
                SELECT child.path FROM disk_entries AS child JOIN descendants AS parent ON child.parent_path=parent.path
            )
            DELETE FROM disk_entries WHERE path IN (SELECT path FROM descendants)
            """;
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private static void UpdateAncestors(SqliteConnection connection, SqliteTransaction transaction, string rootPath, string path, long bytes, long files, long folders, long skipped)
    {
        var ancestor = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(ancestor))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE disk_entries SET size_bytes=MAX(0,COALESCE(size_bytes,0)-$bytes),file_count=MAX(0,file_count-$files),folder_count=MAX(0,folder_count-$folders),skipped_count=MAX(0,skipped_count-$skipped) WHERE path=$path";
            command.Parameters.AddWithValue("$bytes", bytes);
            command.Parameters.AddWithValue("$files", files);
            command.Parameters.AddWithValue("$folders", folders);
            command.Parameters.AddWithValue("$skipped", skipped);
            command.Parameters.AddWithValue("$path", ancestor);
            command.ExecuteNonQuery();
            if (ancestor.Equals(rootPath, StringComparison.OrdinalIgnoreCase)) break;
            ancestor = Path.GetDirectoryName(ancestor);
        }
    }

    private static DiskScanSummary? ReadRootSummary(SqliteConnection connection, string rootPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT size_bytes,file_count,folder_count,skipped_count FROM disk_entries WHERE path=$path";
        command.Parameters.AddWithValue("$path", rootPath);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new DiskScanSummary(rootPath, reader.IsDBNull(0) ? 0 : reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static ScanFrame CreateFrame(DiskScannedItem item, string parentPath, int depth, bool isRoot) =>
        new(item, parentPath, depth, isRoot, new DiskEntryEnumerator(item.Path));

    private static DiskScannedItem GetRootItem(string path)
    {
        var info = new DirectoryInfo(path);
        var attributes = File.GetAttributes(path);
        return new DiskScannedItem(path, GetPathName(path), true, (attributes & FileAttributes.ReparsePoint) != 0, attributes, 0, info.LastWriteTimeUtc.Ticks);
    }

    private static DiskScanEntry ReadEntry(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4) != 0,
        reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetString(6), reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
        reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12) != 0);

    private static string GetExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrWhiteSpace(extension) ? "[no extension]" : extension.ToLowerInvariant();
    }

    private static string GetPathName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed) is { Length: > 0 } name ? name : path;
    }

    private static bool IsPathWithinOrEqual(string path, string parent)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase) || normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeAdd(long left, long right) => right > 0 && left > long.MaxValue - right ? long.MaxValue : left + right;

    private static bool IsScanAccessException(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;

    private static SqliteConnection OpenIndex(string indexPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = indexPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 10
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private void EnsureCurrentIndex()
    {
        if (!HasScan || currentIndexPath is null) throw new InvalidOperationException("Scan a disk or folder first.");
    }

    private void DeleteCurrentIndex()
    {
        if (currentIndexPath is not null) TryDelete(currentIndexPath);
        currentIndexPath = null;
        currentRootPath = null;
        currentSummary = null;
        currentScanIsStale = false;
    }

    private void RemoveAbandonedIndexes()
    {
        foreach (var path in Directory.EnumerateFiles(cacheRoot, "scan-*.db", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class ScanFrame(DiskScannedItem item, string parentPath, int depth, bool isRoot, DiskEntryEnumerator enumerator)
    {
        public DiskScannedItem Item { get; } = item;
        public string ParentPath { get; } = parentPath;
        public int Depth { get; } = depth;
        public bool IsRoot { get; } = isRoot;
        public DiskEntryEnumerator Enumerator { get; } = enumerator;
        public long Bytes { get; set; }
        public long FileCount { get; set; }
        public long FolderCount { get; set; }
        public long SkippedCount { get; set; }
        public bool IsIncomplete { get; set; }
    }

    private sealed record DiskScannedItem(string Path, string Name, bool IsDirectory, bool IsReparsePoint, FileAttributes Attributes, long Length, long LastWriteUtcTicks);

    private sealed class DiskEntryEnumerator : FileSystemEnumerator<DiskScannedItem>
    {
        public long ErrorCount { get; private set; }

        public DiskEntryEnumerator(string directory)
            : base(directory, new EnumerationOptions
            {
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                BufferSize = 32 * 1024
            })
        {
        }

        protected override DiskScannedItem TransformEntry(ref FileSystemEntry entry)
        {
            var attributes = entry.Attributes;
            return new DiskScannedItem(
                entry.ToFullPath(),
                entry.FileName.ToString(),
                entry.IsDirectory,
                (attributes & FileAttributes.ReparsePoint) != 0,
                attributes,
                entry.Length,
                entry.LastWriteTimeUtc.UtcDateTime.Ticks);
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry) => false;

        protected override bool ContinueOnError(int error)
        {
            ErrorCount++;
            return true;
        }
    }

    private sealed class DeleteFrame(string path, DiskEntryEnumerator enumerator)
    {
        public string Path { get; } = path;
        public DiskEntryEnumerator Enumerator { get; } = enumerator;
    }

    private sealed record DeleteOutcome(IReadOnlyList<DiskDeletionFailure> Failures);
}
