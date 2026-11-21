using InkCanvas.Core.Settings;
using InkCanvas.Features.Classroom;
using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace InkCanvas.Classroom.Tests;

/// <summary>
/// 课堂资源包回归：打包 / 解包往返、manifest 一致、原文件哈希不变、
/// 重复解包追加后缀不覆盖、功能开关关闭时拒绝打包、缺少 bundle.json 与打包失败清理。
/// 全部使用临时目录，测试结束递归清理。
/// </summary>
public sealed class ClassroomResourceBundleTests : IDisposable
{
    private const string ManifestJson = """
        {
          "name": "第 3 单元 一次函数",
          "files": ["lesson.txt", "notes.txt"]
        }
        """;

    private readonly string _root;

    public ClassroomResourceBundleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "inkcanvas-bundle-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 临时目录清理失败不影响测试结论；系统会在后续清理 %TEMP%。
        }
    }

    [Fact]
    public void ArchiveAndRestore_RoundTrip_PreservesContentAndSourceFiles()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string lesson = Path.Combine(sourceDirectory, "lesson.txt");
        string notes = Path.Combine(sourceDirectory, "notes.txt");
        File.WriteAllText(lesson, "一次函数：y = kx + b");
        File.WriteAllText(notes, "课堂板书：斜率与截距");

        string lessonHash = Sha256Of(lesson);
        string notesHash = Sha256Of(notes);

        string zipPath = Path.Combine(_root, "out", "lesson-bundle.zip");
        Assert.False(Directory.Exists(Path.GetDirectoryName(zipPath)), "打包前输出目录不应存在。");

        ClassroomResourceBundle.Archive(new[] { lesson, notes }, ManifestJson, zipPath);

        Assert.True(File.Exists(zipPath), "打包后应生成资源包。");
        Assert.True(Directory.Exists(Path.GetDirectoryName(zipPath)), "打包应自动创建输出目录。");

        string restoreDirectory = Path.Combine(_root, "restored");
        ClassroomBundleRestoreResult result = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal(ManifestJson, result.ManifestJson);

        string restoredLesson = Path.Combine(restoreDirectory, "lesson.txt");
        string restoredNotes = Path.Combine(restoreDirectory, "notes.txt");
        Assert.Contains(restoredLesson, result.Files);
        Assert.Contains(restoredNotes, result.Files);
        Assert.Equal(File.ReadAllText(lesson), File.ReadAllText(restoredLesson));
        Assert.Equal(File.ReadAllText(notes), File.ReadAllText(restoredNotes));

        Assert.Equal(lessonHash, Sha256Of(lesson));
        Assert.Equal(notesHash, Sha256Of(notes));
        Assert.Equal(2, Directory.GetFiles(sourceDirectory).Length);
    }

    [Fact]
    public void Restore_Twice_AppendsSuffixAndKeepsExistingFiles()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string lesson = Path.Combine(sourceDirectory, "lesson.txt");
        string notes = Path.Combine(sourceDirectory, "notes.txt");
        File.WriteAllText(lesson, "原始课件内容");
        File.WriteAllText(notes, "原始批注内容");

        string zipPath = Path.Combine(_root, "bundle.zip");
        ClassroomResourceBundle.Archive(new[] { lesson, notes }, ManifestJson, zipPath);

        string restoreDirectory = Path.Combine(_root, "restored");
        ClassroomBundleRestoreResult first = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);
        ClassroomBundleRestoreResult second = ClassroomResourceBundle.Restore(zipPath, restoreDirectory);

        Assert.Equal(2, first.Files.Count);
        Assert.Equal(2, second.Files.Count);
        Assert.All(second.Files, path => Assert.Contains("-1", Path.GetFileName(path)));

        // 第二次解包不覆盖第一次的文件，内容保持一致。
        Assert.Equal(File.ReadAllText(lesson), File.ReadAllText(Path.Combine(restoreDirectory, "lesson.txt")));
        Assert.Equal(File.ReadAllText(lesson), File.ReadAllText(Path.Combine(restoreDirectory, "lesson-1.txt")));
        Assert.Equal(File.ReadAllText(notes), File.ReadAllText(Path.Combine(restoreDirectory, "notes-1.txt")));
        Assert.Equal(4, Directory.GetFiles(restoreDirectory).Length);
    }

    [Fact]
    public void Archive_WhenFeatureDisabled_Throws()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string lesson = Path.Combine(sourceDirectory, "lesson.txt");
        File.WriteAllText(lesson, "关闭开关时的内容");
        string zipPath = Path.Combine(_root, "disabled.zip");
        var settings = new FeatureSettings { EnableResourceBundle = false };

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ClassroomResourceBundle.Archive(new[] { lesson }, ManifestJson, zipPath, settings));

        Assert.Equal("课堂资源包已关闭", error.Message);
        Assert.False(File.Exists(zipPath), "开关关闭时不应生成任何资源包。");
    }

    [Fact]
    public void Archive_WhenSourceMissing_CleansUpHalfProduct()
    {
        string sourceDirectory = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceDirectory);
        string missing = Path.Combine(sourceDirectory, "missing.txt");
        string zipPath = Path.Combine(_root, "broken", "partial.zip");

        Assert.Throws<FileNotFoundException>(
            () => ClassroomResourceBundle.Archive(new[] { missing }, ManifestJson, zipPath));

        Assert.False(File.Exists(zipPath), "打包失败不应留下正式资源包。");
        Assert.False(File.Exists(zipPath + ".tmp"), "打包失败应清理 .tmp 半成品。");
        Assert.True(File.Exists(missing) == false, "不存在的源文件不应被创建。");
    }

    [Fact]
    public void Restore_WithoutBundleManifest_ThrowsInvalidDataException()
    {
        string zipPath = Path.Combine(_root, "no-manifest.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("没有 bundle.json 的旧包");
        }

        Assert.Throws<InvalidDataException>(
            () => ClassroomResourceBundle.Restore(zipPath, Path.Combine(_root, "restore-missing")));
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}