namespace CleanLens.Core.Safety;

public sealed class DeletionPathPolicy
{
    private readonly string[] protectedRoots;
    private readonly string[] allowedRoots;

    public IReadOnlyList<string> AllowedRoots => allowedRoots;

    public DeletionPathPolicy(IEnumerable<string> allowedRoots, IEnumerable<string>? protectedRoots = null)
    {
        this.allowedRoots = allowedRoots.Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        this.protectedRoots = (protectedRoots ?? []).Select(NormalizeRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public bool TryValidate(string path, out string canonicalPath, out string reason)
    {
        canonicalPath = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Path is empty.";
            return false;
        }

        try
        {
            canonicalPath = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "Path is not valid.";
            return false;
        }

        var root = Path.GetPathRoot(canonicalPath);
        if (string.IsNullOrWhiteSpace(root) || canonicalPath.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Drive roots cannot be changed.";
            return false;
        }

        var pathToCheck = canonicalPath;
        if (protectedRoots.Any(protectedRoot => IsSameOrChild(pathToCheck, protectedRoot) || IsSameOrChild(protectedRoot, pathToCheck)))
        {
            reason = "The path overlaps a protected system or user root.";
            return false;
        }

        if (!allowedRoots.Any(allowedRoot => IsSameOrChild(pathToCheck, allowedRoot) && !pathToCheck.Equals(allowedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "The path is outside the explicitly allowed cleanup roots.";
            return false;
        }

        reason = "Path is within an explicitly allowed root.";
        return true;
    }

    public static bool ContainsReparsePoint(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new IOException("Path has no root.");
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;

        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsReparsePointTree(string directoryPath)
    {
        if (ContainsReparsePoint(directoryPath))
        {
            return true;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(directoryPath));
        while (pending.Count > 0)
        {
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(pending.Pop(), "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return true;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
                {
                    return true;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }

        return false;
    }

    public static bool IsPathWithin(string path, string root)
    {
        return IsSameOrChild(path, root) && !Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoot(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Length == 0 ? Path.GetPathRoot(Path.GetFullPath(path))! : fullPath;
    }

    private static bool IsSameOrChild(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
