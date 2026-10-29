using System.IO.Compression;
using System.Security.Cryptography;

namespace PHPUpdater;

public static class SafeFiles
{
    public static string Resolve(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            throw new InvalidDataException("Unsafe relative path");
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Path escapes managed directory");
        }
        RejectLinks(path);
        return path;
    }

    public static void RejectLinks(string path)
    {
        for (var item = Path.GetFullPath(path); item != null;
             item = Path.GetDirectoryName(item))
        {
            if ((File.Exists(item) || Directory.Exists(item)) &&
                File.GetAttributes(item).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Reparse points are not allowed");
            }
        }
    }

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void Verify(string path, string hash)
    {
        if (!string.Equals(Hash(path), hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Checksum mismatch: " +
                Path.GetFileName(path));
        }
    }

    public static void Extract(string archive, string target)
    {
        using var zip = ZipFile.OpenRead(archive);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            var path = Resolve(target, entry.FullName);
            size += entry.Length;
            if (size > 2L * 1024 * 1024 * 1024 ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                throw new InvalidDataException("Unsafe archive entry");
            }
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
        }
    }

    public static Dictionary<string, string> Inventory(string root)
    {
        var result = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in Walk(root))
        {
            result.Add(Path.GetRelativePath(root, file), Hash(file));
        }
        return result;
    }

    public static List<string> RemoveOwned(string root,
        Dictionary<string, string> owned)
    {
        EnsureRemovable(root, owned);
        foreach (var (relative, hash) in owned)
        {
            var file = Resolve(root, relative);
            if (!File.Exists(file)) { continue; }
            if (Hash(file).Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }
        foreach (var relative in owned.Keys)
        {
            var directory = Path.GetDirectoryName(Resolve(root, relative));
            while (directory != null && !PathChange.Equivalent(directory, root))
            {
                if (Directory.Exists(directory) &&
                    !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
                directory = Path.GetDirectoryName(directory);
            }
        }
        return Walk(root).Select(p => Path.GetRelativePath(root, p)).ToList();
    }

    public static void EnsureRemovable(string root,
        Dictionary<string, string> owned)
    {
        foreach (var (relative, hash) in owned)
        {
            var path = Resolve(root, relative);
            if (!File.Exists(path) || !Hash(path).Equals(hash,
                StringComparison.OrdinalIgnoreCase)) { continue; }
            using var handle = new FileStream(path, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
        }
    }

    public static Dictionary<string, string> ArchiveInventory(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) { continue; }
            size += entry.Length;
            if (size > 2L * 1024 * 1024 * 1024)
            {
                throw new InvalidDataException("Archive exceeds size limit");
            }
            using var stream = entry.Open();
            result.Add(entry.FullName,
                Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        return result;
    }

    private static IEnumerable<string> Walk(string root)
    {
        RejectLinks(root);
        if (!Directory.Exists(root)) { yield break; }
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            RejectLinks(path);
            if (!Directory.Exists(path)) { yield return path; continue; }
            foreach (var file in Walk(path)) { yield return file; }
        }
    }
}
