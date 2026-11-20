namespace InkCanvas.Features.Persistence;

internal static class StorageFiles
{
    public static bool IsRegular(string path)
    {
        for (var item = new FileInfo(Path.GetFullPath(path)) as FileSystemInfo; item is not null; item = Directory.GetParent(item.FullName))
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    public static IEnumerable<FileInfo> Enumerate(string root)
    {
        if (!Directory.Exists(root) || !IsRegular(root)) yield break;
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(pending.Pop()).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else if (entry is FileInfo file) yield return file;
            }
        }
    }

    public static long Size(string root) => Enumerate(root).Sum(file => file.Length);

    public static bool DeleteDirectory(string path)
    {
        if (!Directory.Exists(path) || !IsRegular(path)) return false;
        // Validate the whole tree before removal so a junction can never lead outside it.
        var directories = new Stack<string>();
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(path);
        while (pending.Count > 0)
        {
            string current = pending.Pop(); directories.Push(current);
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else files.Add(entry.FullName);
            }
        }
        foreach (string file in files) File.Delete(file);
        while (directories.TryPop(out string directory)) Directory.Delete(directory);
        return true;
    }
}
