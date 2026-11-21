using InkCanvas.Core.Settings;
using System.IO.Compression;
using System.Text;

namespace InkCanvas.Features.Classroom;

/// <summary>资源包解包结果：解包后实际落盘的文件绝对路径与 <c>bundle.json</c> 原文。</summary>
public sealed record ClassroomBundleRestoreResult(IReadOnlyList<string> Files, string ManifestJson);

/// <summary>
/// 课堂资源包：把课件、板书等文件连同 <c>bundle.json</c> 描述打包成 zip，并可解包到指定目录。
/// 解包遇到同名文件时追加 <c>-1</c> / <c>-2</c> 后缀，绝不覆盖既有文件；源文件与源目录只读。
/// 打包受 <see cref="FeatureSettings.EnableResourceBundle"/> 控制，解包不受开关影响。
/// </summary>
public static class ClassroomResourceBundle
{
    /// <summary>资源包内的描述文件名称。</summary>
    public const string ManifestEntryName = "bundle.json";

    /// <summary>
    /// 打包资源包。包内先写入 <paramref name="manifestJson"/> 作为 <c>bundle.json</c>，
    /// 再按顺序加入给定文件（保留文件名，重名时追加 <c>-1</c> 后缀）。
    /// 输出目录不存在会自动创建；任何一步失败都会清理本次的半成品临时文件。
    /// </summary>
    public static void Archive(
        IReadOnlyList<string> files,
        string manifestJson,
        string outputZip,
        FeatureSettings settings = null)
    {
        if (IsDisabled(settings))
        {
            throw new InvalidOperationException("课堂资源包已关闭");
        }

        if (files is null)
        {
            throw new ArgumentNullException(nameof(files));
        }

        if (string.IsNullOrWhiteSpace(outputZip))
        {
            throw new ArgumentException("输出资源包路径不能为空。", nameof(outputZip));
        }

        string fullOutput = Path.GetFullPath(outputZip);
        string outputDirectory = Path.GetDirectoryName(fullOutput);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // 先写同目录 .tmp 再整体替换：失败时正式路径要么是旧文件，要么不存在，不会出现半个 zip。
        string tmp = fullOutput + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WriteTextEntry(zip, ManifestEntryName, manifestJson ?? "");

                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestEntryName };
                foreach (string file in files)
                {
                    if (string.IsNullOrWhiteSpace(file))
                    {
                        continue;
                    }

                    string name = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    // 文件不存在时 CreateEntryFromFile 会抛 FileNotFoundException，交由调用方感知。
                    zip.CreateEntryFromFile(file, MakeUniqueEntryName(name, used), CompressionLevel.Optimal);
                }
            }

            File.Move(tmp, fullOutput, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// 解包资源包到 <paramref name="targetDir"/>。目标目录不存在会自动创建；
    /// 同名文件按 <c>-1</c> / <c>-2</c> 递增后缀改名，既有文件与源资源包均不会被改写。
    /// 资源包缺少 <c>bundle.json</c> 时抛出 <see cref="InvalidDataException"/>。
    /// </summary>
    public static ClassroomBundleRestoreResult Restore(string zipPath, string targetDir)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
        {
            throw new ArgumentException("资源包路径不能为空。", nameof(zipPath));
        }

        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new ArgumentException("目标目录不能为空。", nameof(targetDir));
        }

        string fullZip = Path.GetFullPath(zipPath);
        if (!File.Exists(fullZip))
        {
            throw new FileNotFoundException("资源包不存在。", fullZip);
        }

        string root = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(root);

        using var stream = new FileStream(fullZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        ZipArchiveEntry manifestEntry = zip.Entries.FirstOrDefault(entry =>
            string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase));
        if (manifestEntry is null)
        {
            throw new InvalidDataException("课堂资源包缺少 bundle.json。");
        }

        string manifestJson = ReadEntryText(manifestEntry);

        var written = new List<string>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (string.Equals(entry.FullName, ManifestEntryName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 只取文件名，丢弃包内目录段：既保持打包时的扁平结构，也避免 zip 路径穿越。
            string fileName = entry.Name;
            if (string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            written.Add(ExtractUnique(entry, root, fileName));
        }

        return new ClassroomBundleRestoreResult(written, manifestJson);
    }

    private static bool IsDisabled(FeatureSettings settings)
        => settings is not null && !settings.EnableResourceBundle;

    private static void WriteTextEntry(ZipArchive zip, string entryName, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string MakeUniqueEntryName(string fileName, HashSet<string> used)
    {
        if (used.Add(fileName))
        {
            return fileName;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 1; index < 1000; index++)
        {
            string candidate = $"{stem}-{index}{extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("资源包内同名文件过多：" + fileName);
    }

    /// <summary>
    /// 以 <see cref="FileMode.CreateNew"/> 写入，确保不覆盖任何既有文件；命中同名时尝试下一个后缀。
    /// </summary>
    private static string ExtractUnique(ZipArchiveEntry entry, string root, string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 0; index < 1000; index++)
        {
            string candidate = index == 0 ? fileName : $"{stem}-{index}{extension}";
            string path = Path.Combine(root, candidate);
            try
            {
                using Stream input = entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // 目标已存在：换下一个后缀继续尝试，绝不覆盖。
            }
        }

        throw new IOException("无法为解包文件分配唯一名称：" + fileName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响异常传播；残留的 .tmp 不会被当作正式资源包。
        }
    }
}