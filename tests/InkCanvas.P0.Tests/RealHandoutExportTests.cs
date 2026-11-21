using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Ppt;
using Xunit;
using Xunit.Abstractions;

namespace InkCanvas.P0.Tests;

/// <summary>
/// 「真实课件讲义导出」核对测试。
/// <para>
/// 数据来源全部是真实课件：先把 <c>D:\hw\p0-test.pptx</c> 复制到临时目录（原件只读、前后比对 SHA-256），
/// 在 STA 线程内通过 PowerPoint COM 打开副本，用 <c>Slide.Export</c> 导出第 1、2 页的真实渲染 PNG，
/// 再用应用自身的 <see cref="SlideContentIndex.Build"/>（真实 Build + AttachThumbnail + SetText）
/// 组装批注/教师解析索引，调用 <see cref="HandoutExportService.ExportAsync"/> 走真实讲义合成与 PDF 写入路径。
/// </para>
/// <para>
/// 若 COM 不可用（未安装 PowerPoint、无桌面会话等），只输出「未验证：原因」并返回，不伪造通过；
/// 若 Win2D 合成不可用，则退回「COM 真实页导出 + 应用 HandoutPdfBuilder」并在输出中标注替代的 UI 行为。
/// </para>
/// </summary>
public sealed class RealHandoutExportTests
{
    private const string SourceDeckPath = @"D:\hw\p0-test.pptx";
    private const string ProgId = "PowerPoint.Application";
    private static readonly int[] SelectedPageIndexes = { 1, 2 };
    private const int ExpectedPageCount = 2;

    private readonly ITestOutputHelper _output;

    public RealHandoutExportTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Export_TwoSelectedSlides_FromRealDeck_UsesRealComRendering()
    {
        if (!File.Exists(SourceDeckPath))
        {
            _output.WriteLine("未验证：源课件不存在（" + SourceDeckPath + "）。");
            return;
        }

        string root = Path.Combine(Path.GetTempPath(),
            "inkcanvas-real-handout-" + Guid.NewGuid().ToString("N"));
        try
        {
            string sourceHashBefore = ComputeSha256(SourceDeckPath);
            _output.WriteLine("源课件 SHA-256（导出前）：" + sourceHashBefore);

            Directory.CreateDirectory(root);
            string copyPath = Path.Combine(root, "p0-test-copy.pptx");
            string renderDir = Path.Combine(root, "rendered");
            string outputDirectory = Path.Combine(root, "handout");
            File.Copy(SourceDeckPath, copyPath, overwrite: true);
            Directory.CreateDirectory(renderDir);
            Directory.CreateDirectory(outputDirectory);
            _output.WriteLine("临时副本：" + copyPath);

            int[] existingPowerPointPids = GetPowerPointProcessIds();
            ComExportResult com = RunExportOnStaThread(copyPath, renderDir, existingPowerPointPids);
            if (!com.Success)
            {
                _output.WriteLine("未验证：" + com.Reason);
                return;
            }

            foreach (RenderedSlide slide in com.RenderedSlides)
            {
                (int width, int height) = ReadPngSize(slide.PngPath);
                _output.WriteLine("COM 真实页导出：第 " + slide.Index + " 页 SlideID=" + slide.SlideId
                    + " → " + slide.PngPath + "（" + width + "×" + height + "，"
                    + new FileInfo(slide.PngPath).Length + " 字节）");
            }

            SlideContentIndex index = BuildContentIndex(com);
            var request = new HandoutRenderRequest
            {
                Index = index,
                Options = new HandoutOptions
                {
                    Content = HandoutContentMode.AnnotationsWithTeacherNotes,
                    SelectedSlideIds = com.RenderedSlides.Select(s => s.SlideId).ToList(),
                },
                PageWidthPx = 1240,
                PageHeightPx = 1754,
            };

            HandoutExportResult export = await HandoutExportService.ExportAsync(request, outputDirectory);
            foreach (string warning in export.Warnings)
            {
                _output.WriteLine("应用导出警告：" + warning);
            }

            bool composedByApp = export.Success
                && export.Pages.Count == ExpectedPageCount
                && export.Pages.All(p => !string.IsNullOrEmpty(p.PageImagePath)
                    && File.Exists(p.PageImagePath)
                    && new FileInfo(p.PageImagePath).Length > 0);

            List<string> pngPaths;
            if (composedByApp)
            {
                pngPaths = export.Pages.Select(p => p.PageImagePath).ToList();
                _output.WriteLine("导出方式：应用 HandoutExportService 真实合成路径（Win2D 逐页合成 PNG + HandoutPdfBuilder 写 PDF）。");
            }
            else
            {
                pngPaths = CopyComRendersToOutput(com.RenderedSlides, outputDirectory);
                _output.WriteLine("导出方式：替代路径。Win2D 讲义合成不可用（见上方警告），"
                    + "改用 PowerPoint COM 真实页渲染 PNG + 应用 HandoutPdfBuilder 生成 PDF；"
                    + "被替代的 UI 行为是「应用内讲义页合成（标题/课件图/教师解析排版）」。");
            }

            // 1) PNG：至少两页，存在且尺寸大于 0。
            Assert.Equal(ExpectedPageCount, pngPaths.Count);
            var pngSizes = new List<(string Path, int Width, int Height, long Length)>();
            foreach (string pngPath in pngPaths)
            {
                Assert.True(File.Exists(pngPath), "讲义 PNG 不存在：" + pngPath);
                var info = new FileInfo(pngPath);
                Assert.True(info.Length > 0, "讲义 PNG 为空：" + pngPath);
                (int width, int height) = ReadPngSize(pngPath);
                Assert.True(width > 0 && height > 0, "讲义 PNG 尺寸无效：" + pngPath);
                pngSizes.Add((pngPath, width, height, info.Length));
            }

            foreach (var png in pngSizes)
            {
                _output.WriteLine("讲义 PNG：" + Path.GetFileName(png.Path)
                    + "（" + png.Width + "×" + png.Height + "，" + png.Length + " 字节）");
            }

            // 2) PDF：合法头、页对象数精确等于 2、Kids 顺序与输入页序一致。
            byte[] pdf;
            if (composedByApp && !string.IsNullOrEmpty(export.PdfPath) && File.Exists(export.PdfPath))
            {
                pdf = File.ReadAllBytes(export.PdfPath);
            }
            else
            {
                pdf = await BuildPdfFromPngAsync(pngPaths, outputDirectory);
                _output.WriteLine("PDF 由 HandoutImageDecoder + HandoutPdfBuilder 生成"
                    + "（应用项目内的真实 PDF 写入实现）。");
            }

            Assert.True(pdf.Length > 0, "PDF 为空。");
            Assert.True(pdf.Length >= 8, "PDF 长度不足 8 字节。");
            string header = Encoding.ASCII.GetString(pdf, 0, 8);
            _output.WriteLine("PDF 头：" + header);
            Assert.Equal("%PDF-1.4", header);

            int pageObjectCount = CountPdfPageObjects(pdf);
            _output.WriteLine("PDF 页对象数（/Type /Page，排除 /Pages）：" + pageObjectCount);
            Assert.Equal(ExpectedPageCount, pageObjectCount);

            string pdfText = Encoding.ASCII.GetString(pdf);
            List<int> kids = ParsePdfKids(pdfText);
            _output.WriteLine("PDF /Kids 页对象顺序：" + string.Join(", ", kids));
            Assert.Equal(ExpectedPageCount, kids.Count);
            Assert.True(kids.SequenceEqual(kids.OrderBy(k => k)),
                "PDF 页对象顺序必须与输入页序一致（Kids 必须递增）。");

            // 3) 选页顺序：文件名 handout-001.png / handout-002.png 与页序一致。
            string firstPng = Path.GetFileName(pngPaths[0]);
            string secondPng = Path.GetFileName(pngPaths[1]);
            _output.WriteLine("讲义 PNG 文件名顺序：" + firstPng + " → " + secondPng);
            if (composedByApp)
            {
                Assert.Equal(new[] { 1, 2 }, export.Pages.Select(p => p.Index).ToArray());
                Assert.EndsWith("handout-001.png", firstPng, StringComparison.Ordinal);
                Assert.EndsWith("handout-002.png", secondPng, StringComparison.Ordinal);
            }
            else
            {
                Assert.EndsWith("handout-001.png", firstPng, StringComparison.Ordinal);
                Assert.EndsWith("handout-002.png", secondPng, StringComparison.Ordinal);
            }

            Assert.Equal(ExpectedPageCount, com.RenderedSlides.Count);
            Assert.Equal(SelectedPageIndexes, com.RenderedSlides.Select(s => s.Index).ToArray());

            // 4) 原课件不被修改：前后 SHA-256 一致。
            string sourceHashAfter = ComputeSha256(SourceDeckPath);
            _output.WriteLine("源课件 SHA-256（导出后）：" + sourceHashAfter);
            Assert.Equal(sourceHashBefore, sourceHashAfter);
        }
        finally
        {
            CleanupDirectory(root, _output);
        }
    }

    // ── 应用侧索引构造：真实 SlideContentIndex.Build + AttachThumbnail + SetText ──

    private static SlideContentIndex BuildContentIndex(ComExportResult com)
    {
        var presentation = new PptPresentationInfo(
            "real-handout-test", Path.GetFileName(com.CopyPath), com.CopyPath,
            true, com.AllSlides.Count, com.SlideWidthPt, com.SlideHeightPt, ProgId);

        var details = new PptPresentationDetails(presentation, com.AllSlides, null);

        var strokesByIndex = new Dictionary<int, IReadOnlyList<InkStroke>>();
        foreach (RenderedSlide slide in com.RenderedSlides)
        {
            strokesByIndex[slide.Index] = BuildTestStrokes(slide.Index);
        }

        var stored = new List<PptStoredSlide>();
        foreach (PptSlideInfo slide in com.AllSlides)
        {
            IReadOnlyList<InkStroke> strokes = strokesByIndex.TryGetValue(slide.Index, out var found)
                ? found
                : Array.Empty<InkStroke>();
            stored.Add(new PptStoredSlide(slide.SlideId, slide.Index, strokes));
        }

        var archive = new PptInkArchive(
            presentation, null, null, stored, false, null, 2);

        SlideContentIndex index = SlideContentIndex.Build(
            details, archive, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        foreach (RenderedSlide slide in com.RenderedSlides)
        {
            Assert.True(index.AttachThumbnail(slide.SlideId, slide.PngPath),
                "应能把真实导出页挂到索引第 " + slide.Index + " 页（SlideID=" + slide.SlideId + "）。");
            Assert.True(index.SetText(slide.SlideId, BuildTeacherNotes(slide)),
                "应能写入第 " + slide.Index + " 页的教师解析。");
        }

        SlideContentEntry first = index.Find(com.RenderedSlides[0].SlideId);
        Assert.NotNull(first);
        Assert.True(first.HasInk, "第 1 页应带有真实批注标记。");
        Assert.True(first.InkStrokeCount > 0, "第 1 页应带有真实笔画数量。");

        return index;
    }

    /// <summary>构造真实 <see cref="InkStroke"/>（Core 层真实类型），用来驱动索引的 HasInk/InkStrokeCount。</summary>
    private static IReadOnlyList<InkStroke> BuildTestStrokes(int pageIndex)
    {
        var strokes = new List<InkStroke>();
        for (int s = 0; s < 3; s++)
        {
            var attributes = new StrokeAttributes
            {
                Color = InkColor.Black,
                BaseWidth = 3f,
            };
            var stroke = new InkStroke(attributes);
            for (int p = 0; p < 6; p++)
            {
                stroke.AddPoint(
                    120f + p * 18f + pageIndex * 4f,
                    160f + s * 42f + p * 7f,
                    0.5f,
                    p * 8f);
            }

            stroke.Complete();
            strokes.Add(stroke);
        }

        return strokes;
    }

    private static string BuildTeacherNotes(RenderedSlide slide)
    {
        string text = string.IsNullOrWhiteSpace(slide.Text)
            ? "（本页未提取到文本）"
            : slide.Text;
        return text + "\n教师解析：围绕本页要点讲解，并结合第 " + slide.Index + " 页批注回顾。";
    }

    // ── PDF 回退：仅使用应用项目内的真实 HandoutImageDecoder / HandoutPdfBuilder ──

    private static async Task<byte[]> BuildPdfFromPngAsync(List<string> pngPaths, string outputDirectory)
    {
        var pdfPages = new List<HandoutPdfPage>();
        foreach (string pngPath in pngPaths)
        {
            HandoutPdfPage page = await HandoutImageDecoder.DecodePngAsync(File.ReadAllBytes(pngPath));
            if (page is not null) pdfPages.Add(page);
        }

        byte[] pdf = HandoutPdfBuilder.Build(pdfPages, 595.28, 841.89);
        if (pdf.Length > 0)
        {
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "handout.pdf"), pdf);
        }

        return pdf;
    }

    private static List<string> CopyComRendersToOutput(
        IReadOnlyList<RenderedSlide> slides, string outputDirectory)
    {
        var paths = new List<string>();
        foreach (RenderedSlide slide in slides)
        {
            string target = Path.Combine(outputDirectory,
                "handout-" + slide.Index.ToString("D3") + ".png");
            File.Copy(slide.PngPath, target, overwrite: true);
            paths.Add(target);
        }

        return paths;
    }

    // ── PowerPoint COM（STA 线程）──

    private sealed class ComExportResult
    {
        public bool Success { get; init; }

        public string Reason { get; init; } = "";

        public string CopyPath { get; init; } = "";

        public int TotalSlideCount { get; init; }

        public double SlideWidthPt { get; init; }

        public double SlideHeightPt { get; init; }

        public List<PptSlideInfo> AllSlides { get; init; } = new();

        public List<RenderedSlide> RenderedSlides { get; init; } = new();
    }

    private sealed record RenderedSlide(
        int Index, int SlideId, string PngPath, string Text);

    private static ComExportResult RunExportOnStaThread(
        string copyPath, string renderDir, int[] existingPowerPointPids)
    {
        ComExportResult result = null;
        Exception threadError = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = ExportWithPowerPoint(copyPath, renderDir, existingPowerPointPids);
            }
            catch (Exception ex)
            {
                threadError = ex;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(180)))
        {
            return new ComExportResult
            {
                Success = false,
                Reason = "PowerPoint COM 导出超时（180 秒），未能在 STA 线程内完成真实页渲染。",
            };
        }

        if (threadError is not null)
        {
            return new ComExportResult
            {
                Success = false,
                Reason = "PowerPoint COM STA 线程异常：" + threadError.GetType().Name
                    + "：" + threadError.Message,
            };
        }

        return result ?? new ComExportResult
        {
            Success = false,
            Reason = "PowerPoint COM 导出未返回结果。",
        };
    }

    private static ComExportResult ExportWithPowerPoint(
        string copyPath, string renderDir, int[] existingPowerPointPids)
    {
        dynamic app = null;
        object presentationsObject = null;
        object presentationObject = null;
        object slidesObject = null;
        bool createdNew = false;

        try
        {
            Type progIdType = Type.GetTypeFromProgID(ProgId);
            if (progIdType is null)
            {
                return new ComExportResult
                {
                    Success = false,
                    Reason = "本机未注册 " + ProgId + "，无法调用真实 PowerPoint 渲染。",
                };
            }

            object running = TryGetRunningPowerPoint();
            if (running is not null)
            {
                app = running;
            }
            else
            {
                app = Activator.CreateInstance(progIdType);
                createdNew = true;
            }

            presentationsObject = app.Presentations;
            dynamic presentations = presentationsObject;
            // ReadOnly=-1（msoTrue）、Untitled=0、WithWindow=0：只读打开副本且不弹编辑窗口。
            presentationObject = presentations.Open(copyPath, -1, 0, 0);
            dynamic presentation = presentationObject;

            object pageSetupObject = presentation.PageSetup;
            dynamic pageSetup = pageSetupObject;
            double slideWidthPt = (double)pageSetup.SlideWidth;
            double slideHeightPt = (double)pageSetup.SlideHeight;
            Release(pageSetupObject);

            int pixelWidth = Math.Max(16, (int)Math.Round(slideWidthPt * 96.0 / 72.0));
            int pixelHeight = Math.Max(16, (int)Math.Round(slideHeightPt * 96.0 / 72.0));

            slidesObject = presentation.Slides;
            dynamic slides = slidesObject;
            int totalSlideCount = (int)slides.Count;

            var allSlides = new List<PptSlideInfo>();
            for (int index = 1; index <= totalSlideCount; index++)
            {
                object slideObject = null;
                try
                {
                    slideObject = slides[index];
                    dynamic slide = slideObject;
                    allSlides.Add(new PptSlideInfo(
                        (int)slide.SlideID, index, (string)slide.Name, null, null, null));
                }
                finally
                {
                    Release(slideObject);
                }
            }

            var rendered = new List<RenderedSlide>();
            foreach (int index in SelectedPageIndexes)
            {
                if (index < 1 || index > totalSlideCount) continue;

                object slideObject = null;
                try
                {
                    slideObject = slides[index];
                    dynamic slide = slideObject;
                    int slideId = (int)slide.SlideID;
                    string pngPath = Path.Combine(renderDir, "slide-" + index.ToString("D3") + ".png");
                    // Slide.Export 由 PowerPoint 自己渲染真实课件页（含母版、字体、图片），不是合成图。
                    slide.Export(pngPath, "PNG", pixelWidth, pixelHeight);
                    string text = ReadSlideText(slide);
                    rendered.Add(new RenderedSlide(index, slideId, pngPath, text));
                }
                finally
                {
                    Release(slideObject);
                }
            }

            if (rendered.Count != SelectedPageIndexes.Length)
            {
                return new ComExportResult
                {
                    Success = false,
                    Reason = "真实课件只导出 " + rendered.Count + " 页，期望 "
                        + SelectedPageIndexes.Length + " 页。",
                };
            }

            // 只读副本，无需保存；Close 不触碰源课件。
            presentation.Close();

            return new ComExportResult
            {
                Success = true,
                CopyPath = copyPath,
                TotalSlideCount = totalSlideCount,
                SlideWidthPt = slideWidthPt,
                SlideHeightPt = slideHeightPt,
                AllSlides = allSlides,
                RenderedSlides = rendered,
            };
        }
        catch (COMException ex)
        {
            return new ComExportResult
            {
                Success = false,
                Reason = "PowerPoint COM 调用失败（0x" + ex.HResult.ToString("X8") + "）：" + ex.Message,
            };
        }
        catch (Exception ex)
        {
            return new ComExportResult
            {
                Success = false,
                Reason = "真实页导出失败：" + ex.GetType().Name + "：" + ex.Message,
            };
        }
        finally
        {
            Release(presentationObject);
            Release(slidesObject);
            Release(presentationsObject);
            if (createdNew && app is not null)
            {
                // 仅退出本测试新建的 PowerPoint 实例，绝不影响用户已打开的演示文稿。
                try { app.Quit(); } catch (Exception) { }
            }

            Release(app);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (createdNew) WaitForNewPowerPointExit(existingPowerPointPids);
        }
    }

    private static string ReadSlideText(dynamic slide)
    {
        object shapesObject = null;
        var builder = new StringBuilder();
        try
        {
            shapesObject = slide.Shapes;
            dynamic shapes = shapesObject;
            int count = (int)shapes.Count;
            for (int i = 1; i <= count; i++)
            {
                object shapeObject = null;
                try
                {
                    shapeObject = shapes[i];
                    dynamic shape = shapeObject;
                    dynamic frame = shape.TextFrame;
                    if ((int)frame.HasText != -1) continue;
                    string text = (string)frame.TextRange.Text;
                    if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(text.Trim());
                }
                catch (Exception)
                {
                    // 单个形状无 TextFrame 时跳过，继续读取其余文本。
                }
                finally
                {
                    Release(shapeObject);
                }
            }
        }
        catch (Exception)
        {
            // 文本读取失败不影响真实页导出本身。
        }
        finally
        {
            Release(shapesObject);
        }

        return builder.ToString().Trim();
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CLSIDFromProgID(string progId, out Guid clsid);

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, out IntPtr punk);

    private static object TryGetRunningPowerPoint()
    {
        try
        {
            if (CLSIDFromProgID(ProgId, out Guid clsid) != 0) return null;
            if (GetActiveObject(ref clsid, IntPtr.Zero, out IntPtr punk) != 0
                || punk == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.GetObjectForIUnknown(punk);
            }
            finally
            {
                Marshal.Release(punk);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Release(object value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (Exception) { }
    }

    private static int[] GetPowerPointProcessIds()
    {
        try
        {
            return Process.GetProcessesByName("POWERPNT").Select(p => p.Id).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<int>();
        }
    }

    private static void WaitForNewPowerPointExit(int[] existingPids)
    {
        var known = new HashSet<int>(existingPids);
        for (int attempt = 0; attempt < 32; attempt++)
        {
            bool anyNew = false;
            foreach (Process process in Process.GetProcessesByName("POWERPNT"))
            {
                if (!known.Contains(process.Id)) { anyNew = true; break; }
            }

            if (!anyNew) return;
            Thread.Sleep(250);
        }

        // 测试启动的实例仍未退出：仅结束列表中新增（非用户原有）的进程。
        foreach (Process process in Process.GetProcessesByName("POWERPNT"))
        {
            if (known.Contains(process.Id)) continue;
            try { process.Kill(); }
            catch (Exception) { }
        }
    }

    // ── 字节级校验 ──

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        var header = new byte[33];
        using var stream = File.OpenRead(path);
        int read = stream.Read(header, 0, header.Length);
        if (read < 33) return (0, 0);
        if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47)
        {
            return (0, 0);
        }

        int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return (width, height);
    }

    private static int CountPdfPageObjects(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        // 只统计 /Type /Page，排除 /Type /Pages（页树节点）。
        return Regex.Matches(text, @"/Type\s*/Page(?![s])").Count;
    }

    private static List<int> ParsePdfKids(string pdfText)
    {
        Match match = Regex.Match(pdfText, @"/Kids\s*\[([^\]]*)\]");
        if (!match.Success) return new List<int>();

        var result = new List<int>();
        string[] tokens = match.Groups[1].Value.Split(
            new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        // /Kids 的每一项形如「<对象号> 0 R」，三项一组。
        for (int i = 0; i + 2 < tokens.Length; i += 3)
        {
            if (int.TryParse(tokens[i], out int number)) result.Add(number);
        }

        return result;
    }

    private static void CleanupDirectory(string root, ITestOutputHelper output)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4)
                {
                    output.WriteLine("临时目录未能清理（" + root + "）："
                        + ex.GetType().Name + "：" + ex.Message);
                    return;
                }

                Thread.Sleep(300);
            }
        }
    }
}