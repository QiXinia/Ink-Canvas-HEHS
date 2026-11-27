using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using Clipper2Lib;
using InkCanvas.Core.Ink;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;

namespace InkCanvas.Pdf;

public sealed class PdfPasswordRequiredException(string message, Exception inner) : IOException(message, inner);

/// <summary>Owns an immutable source snapshot. Saves never append duplicate InkCanvas appearances.</summary>
public sealed class PdfAnnotationFile : IDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, IReadOnlyList<InkStroke>> _stored = new();
    private readonly Dictionary<int, PdfPageSpacing> _spacing = new();
    private readonly Dictionary<int, int> _rotations = new();
    public string SourcePath { get; }
    public string RenderPath { get; private set; }
    public string Password { get; }
    public IReadOnlyList<PdfPageGeometry> Pages { get; private set; }
    public IEnumerable<int> InkPages => _stored.Keys;
    public IReadOnlyList<PdfBookmark> Bookmarks { get; }
    public bool ReadOnly { get; }
    public bool HasSignatures { get; }
    public int UnrecognizedInkPages { get; private set; }
    private bool _disposed;
    private readonly Dictionary<string, (long Length, DateTime Modified)> _savedFiles = new(StringComparer.OrdinalIgnoreCase);

    private PdfAnnotationFile(string path, string password, string directory, PdfDocument document, bool readOnly, CancellationToken token)
    {
        SourcePath = Path.GetFullPath(path); Password = password; _directory = directory; ReadOnly = readOnly;
        RenderPath = Path.Combine(directory, "source.pdf");
        RememberFile(SourcePath);
        var pages = new PdfPageGeometry[document.PageCount];
        for (int index = 0; index < pages.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = document.Pages[index];
            var box = page.CropBox; if (box.Width <= 0 || box.Height <= 0) box = page.MediaBox;
            double unit = page.Elements.GetReal("/UserUnit"); if (unit <= 0) unit = 1;
            var geometry = new PdfPageGeometry(box.X1, box.Y1, box.X2, box.Y2, page.Rotate, unit);
            if (!float.IsFinite(geometry.DipWidth) || !float.IsFinite(geometry.DipHeight) || geometry.DipWidth <= 0 || geometry.DipHeight <= 0)
                throw new InvalidDataException("PDF 页面尺寸无效。");
            pages[index] = geometry;
        }
        Pages = pages;
        HasSignatures = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetInteger("/SigFlags") > 0;
        var bookmarks = new List<PdfBookmark>();
        if (document.Outlines.Count > 0)
        {
            var pageIndices = new Dictionary<PdfPage, int>(pages.Length);
            for (int index = 0; index < pages.Length; index++) pageIndices.Add(document.Pages[index], index);
            ReadBookmarks(document.Outlines, pageIndices, 0);
        }
        Bookmarks = bookmarks;
        void ReadBookmarks(PdfOutlineCollection outlines, Dictionary<PdfPage, int> pageIndices, int depth)
        {
            if (depth > 24 || bookmarks.Count >= 5000) return;
            foreach (PdfOutline outline in outlines)
            {
                token.ThrowIfCancellationRequested();
                if (outline.DestinationPage is { } page && pageIndices.TryGetValue(page, out int index)) bookmarks.Add(new PdfBookmark(outline.Title, index, depth));
                if (outline.HasChildren) ReadBookmarks(outline.Outlines, pageIndices, depth + 1);
                if (bookmarks.Count >= 5000) return;
            }
        }
    }
    public static async Task<PdfAnnotationFile> OpenAsync(string path, string password = null, CancellationToken token = default)
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-Pdf", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string copy = Path.Combine(directory, "source.pdf");
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
            await using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await input.CopyToAsync(output, token);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                PdfDocument document; bool readOnly = false;
                try { document = PdfReader.Open(copy, password, PdfDocumentOpenMode.Modify); }
                catch (PdfReaderException ex) when (ex.Message.Contains("owner", StringComparison.OrdinalIgnoreCase))
                { document = PdfReader.Open(copy, password, PdfDocumentOpenMode.Import); readOnly = true; }
                catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                { throw new PdfPasswordRequiredException("此 PDF 需要正确的打开密码。", ex); }
                try
                {
                    var session = new PdfAnnotationFile(path, password, directory, document, readOnly, token);
                    if (session.Pages.Count == 0) throw new InvalidDataException("PDF 没有可显示的页面。");
                    if (!readOnly && session.ExtractEditableContent(document, token))
                    {
                        token.ThrowIfCancellationRequested();
                        session.RenderPath = Path.Combine(directory, "background.pdf");
                        document.Options.CompressContentStreams = false;
                        document.Save(session.RenderPath);
                    }
                    token.ThrowIfCancellationRequested();
                    document.Dispose();
                    return session;
                }
                catch { document.Dispose(); throw; }
            }, token);
        }
        catch { try { Directory.Delete(directory, true); } catch (IOException) { } throw; }
    }
    public IReadOnlyList<InkStroke> GetInk(int pageIndex) => _stored.TryGetValue(pageIndex, out var ink) ? ink : Array.Empty<InkStroke>();
    public PdfPageSpacing GetSpacing(int pageIndex) => _spacing.GetValueOrDefault(pageIndex) ?? PdfPageSpacing.Empty;
    public int GetDisplayRotation(int pageIndex) => _rotations.GetValueOrDefault(pageIndex);

    private bool ExtractEditableContent(PdfDocument document, CancellationToken token)
    {
        bool changed = ExtractInk(document, token);
        var originalPages = (PdfPageGeometry[])Pages;
        for (int index = 0; index < originalPages.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var displayed = originalPages[index];
            if (!PdfSpacingContent.TryRestore(document.Pages[index], displayed, out var original, out var spacing)) continue;
            changed = true;
            originalPages[index] = original;
            _spacing[index] = spacing;
            _rotations[index] = (displayed.NormalizedRotation - original.NormalizedRotation + 360) % 360;
            var transform = displayed.TransformTo(spacing.Expand(original));
            if (transform != Matrix3x2.Identity && _stored.TryGetValue(index, out var strokes))
                foreach (var stroke in strokes) stroke.Transform(transform, scaleWidth: false);
        }
        Pages = originalPages;
        if (changed && _stored.Count > 0)
        {
            var display = Pages.Select((page, index) => GetSpacing(index).Expand(page)).ToArray();
            var distributed = PdfInkSplitter.Distribute(_stored, new PdfContinuousLayout(display), token);
            _stored.Clear();
            foreach (var pair in distributed) if (pair.Value.Count > 0) _stored[pair.Key] = pair.Value;
        }
        return changed;
    }

    private bool ExtractInk(PdfDocument document, CancellationToken token)
    {
        bool any = false;
        for (int index = 0; index < document.PageCount; index++)
        {
            token.ThrowIfCancellationRequested();
            var page = document.Pages[index];
            if (!page.Elements.ContainsKey("/Annots")) continue;
            var strokes = new List<InkStroke>();
            for (int a = page.Annotations.Count - 1; a >= 0; a--)
            {
                token.ThrowIfCancellationRequested();
                var annotation = page.Annotations[a];
                if (annotation.Elements.GetInteger("/InkCanvasVersion") != 1) continue;
                var data = annotation.Elements.GetDictionary("/InkCanvasData");
                try
                {
                    if (data?.Stream is null || data.Elements.GetName("/Filter") != "/FlateDecode") throw new InvalidDataException("Unknown ink metadata encoding.");
                    var decoded = PdfInkCodec.Decode(data.Stream.Value, token);
                    if (annotation.Elements.GetDictionary("/InkCanvasLayers")?.Stream is { } layers)
                    {
                        var indices = layers.Value;
                        if (indices.Length == decoded.Count)
                            for (int i = 0; i < decoded.Count; i++) decoded[i].InkLayerIndex = indices[i];
                    }
                    strokes.AddRange(decoded);
                    page.Annotations.Remove(annotation); any = true;
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or OverflowException) { UnrecognizedInkPages++; }
            }
            if (strokes.Count > 0) _stored[index] = InkStroke.InDrawingOrder(strokes.GroupBy(stroke => stroke.Id).Select(group => group.Last())).ToArray();
        }
        return any;
    }

    public Task SaveAsync(string target, IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> pages, CancellationToken token = default)
        => SaveAsync(target, pages, null, token);

    public async Task SaveAsync(string target, IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> pages,
        IReadOnlyDictionary<int, int> rotations, CancellationToken token = default)
        => await SaveAsync(target, pages, rotations, _spacing, token);

    public async Task SaveAsync(string target, IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> pages,
        IReadOnlyDictionary<int, int> rotations, IReadOnlyDictionary<int, PdfPageSpacing> spacing, CancellationToken token = default,
        IReadOnlyList<int> pageOrder = null)
    {
        if (ReadOnly) throw new UnauthorizedAccessException("此 PDF 的权限只允许查看，请使用有编辑权限的文件。 ");
        target = Path.GetFullPath(target);
        if (HasSignatures && target.Equals(SourcePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("此 PDF 包含数字签名，请另存为批注副本。");
        var order = pageOrder?.ToArray();
        if (order is not null && (order.Length == 0 || order.Distinct().Count() != order.Length || order.Any(page => (uint)page >= (uint)Pages.Count)))
            throw new ArgumentException("Invalid PDF page order.", nameof(pageOrder));
        if (order is not null && order.SequenceEqual(Enumerable.Range(0, Pages.Count))) order = null;
        if (order is not null)
        {
            var retained = order.ToHashSet();
            pages = pages.Where(pair => retained.Contains(pair.Key)).ToDictionary();
            spacing = spacing.Where(pair => retained.Contains(pair.Key)).ToDictionary();
        }
        foreach (var page in pages)
        {
            if (page.Key < 0 || page.Key >= Pages.Count) throw new ArgumentOutOfRangeException(nameof(pages));
            if (page.Value.Sum(stroke => (long)stroke.Count) > PdfInkCodec.MaximumPoints) throw new InvalidDataException("单页墨迹点数过多，请拆分页保存。");
        }
        var spacingSnapshot = spacing.Where(pair => !pair.Value.IsEmpty).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (spacingSnapshot.Any(pair => pair.Key < 0 || pair.Key >= Pages.Count || !pair.Value.IsValid(Pages[pair.Key])))
            throw new ArgumentException("Invalid PDF page spacing.", nameof(spacing));
        var dimensions = Pages.Select((page, index) => spacingSnapshot.TryGetValue(index, out var layout) ? layout.Expand(page) : page).ToArray();
        // Structural edits keep page-local ink attached to the source page, including its margins.
        if (order is null) pages = PdfInkSplitter.Distribute(pages, new PdfContinuousLayout(dimensions));
        var rotationSnapshot = (rotations ?? _rotations).ToDictionary(pair => pair.Key, pair => pair.Value);
        if (rotationSnapshot is not null && rotationSnapshot.Any(pair => pair.Key < 0 || pair.Key >= Pages.Count || pair.Value % 90 != 0))
            throw new ArgumentException("Invalid page rotation.", nameof(rotations));
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() => SaveCore(target, pages, rotationSnapshot, spacingSnapshot, dimensions, order, token), token);
        }
        finally { _gate.Release(); }
    }

    private void SaveCore(string target, IReadOnlyDictionary<int, IReadOnlyList<InkStroke>> pages, IReadOnlyDictionary<int, int> rotations,
        IReadOnlyDictionary<int, PdfPageSpacing> spacing, IReadOnlyList<PdfPageGeometry> dimensions, int[] pageOrder, CancellationToken token)
    {
        if (_savedFiles.TryGetValue(target, out var expected))
        {
            var current = new FileInfo(target);
            if (!current.Exists || current.Length != expected.Length || current.LastWriteTimeUtc != expected.Modified)
                throw new IOException("文件已被其他程序更改，请另存为以保留双方的修改。");
        }
        using var document = PdfReader.Open(RenderPath, Password, PdfDocumentOpenMode.Modify);
        string temporary = Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            foreach (var pair in spacing)
            {
                token.ThrowIfCancellationRequested();
                PdfSpacingContent.Apply(document, document.Pages[pair.Key], Pages[pair.Key], pair.Value);
            }
            if (rotations is not null)
                foreach (var rotation in rotations) document.Pages[rotation.Key].Rotate = (Pages[rotation.Key].Rotation + rotation.Value + 360) % 360;
            foreach (var pair in pages.OrderBy(pair => pair.Key))
            {
                token.ThrowIfCancellationRequested(); if (pair.Value.Count == 0) continue;
                var page = document.Pages[pair.Key]; var geometry = dimensions[pair.Key];
                IReadOnlyList<InkStroke> ink = pair.Value;
                if (rotations?.TryGetValue(pair.Key, out int turn) == true && turn % 360 != 0)
                {
                    var rotated = geometry with { Rotation = geometry.Rotation + turn };
                    var transform = geometry.TransformTo(rotated);
                    ink = ink.Select(stroke => { var copy = stroke.Clone(); copy.Transform(transform, scaleWidth: false); return copy; }).ToArray();
                    geometry = rotated;
                }
                var expanded = PdfPageBleed.IncludeInk(geometry, ink);
                if (expanded != geometry)
                {
                    // Enlarging CropBox must not reveal content hidden by the original crop.
                    var clip = Encoding.ASCII.GetBytes($"q\n{Number(geometry.Left)} {Number(geometry.Bottom)} {Number(geometry.Width)} {Number(geometry.Height)} re W n\n");
                    page.Contents.PrependContent().CreateStream(clip);
                    // Cover the new margin after restoring the original graphics state. Some
                    // renderers expand a rectangular clip to whole pixels at the crop edge.
                    var margin = $"\nQ\nq 1 1 1 rg {Number(expanded.Left)} {Number(expanded.Bottom)} {Number(expanded.Width)} {Number(expanded.Height)} re "
                        + $"{Number(geometry.Left)} {Number(geometry.Bottom)} {Number(geometry.Width)} {Number(geometry.Height)} re f* Q\n";
                    page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(margin));
                    if (!page.Elements.ContainsKey("/TrimBox")) page.TrimBox = Rectangle(geometry);
                    var media = page.MediaBox;
                    var bleed = page.Elements.ContainsKey("/BleedBox") ? page.BleedBox : Rectangle(geometry);
                    page.CropBox = Rectangle(expanded);
                    page.BleedBox = Box(Math.Min(expanded.Left, bleed.X1), Math.Min(expanded.Bottom, bleed.Y1),
                        Math.Max(expanded.Right, bleed.X2), Math.Max(expanded.Top, bleed.Y2));
                    page.MediaBox = Box(Math.Min(media.X1, page.BleedBox.X1), Math.Min(media.Y1, page.BleedBox.Y1),
                        Math.Max(media.X2, page.BleedBox.X2), Math.Max(media.Y2, page.BleedBox.Y2));
                }
                var annotation = new InkAnnotation(document) { Rectangle = Rectangle(expanded),
                    Contents = "Ink Canvas 可编辑墨迹", Flags = PdfAnnotationFlags.Print };
                annotation.Elements.SetInteger("/InkCanvasVersion", 1);
                var offset = PdfPageBleed.InkOffset(geometry, expanded);
                var savedInk = offset == Vector2.Zero ? ink : ink.Select(stroke =>
                {
                    var copy = stroke.Clone(); copy.Transform(Matrix3x2.CreateTranslation(offset), scaleWidth: false); return copy;
                }).ToArray();
                if (savedInk.Any(stroke => stroke.InkLayerIndex != 0))
                {
                    savedInk = InkStroke.InDrawingOrder(savedInk).Select((stroke, index) =>
                    {
                        var copy = stroke.Clone();
                        copy.LayerOrder = index;
                        return copy;
                    }).ToArray();
                    var layers = new PdfDictionary(document);
                    layers.CreateStream(savedInk.Select(stroke => (byte)stroke.InkLayerIndex).ToArray());
                    document.Internals.AddObject(layers);
                    annotation.Elements["/InkCanvasLayers"] = layers.Reference;
                }
                var state = CompressedStream(PdfInkCodec.Encode(savedInk), alreadyCompressed: true);
                annotation.Elements["/InkCanvasData"] = state.Reference;
                var appearance = Appearance(ink, geometry, expanded);
                var ap = new PdfDictionary(document); ap.Elements["/N"] = appearance.Reference; annotation.Elements["/AP"] = ap;
                page.Annotations.Add(annotation);
            }
            if (pageOrder is not null) ApplyPageOrder(document, pageOrder, token);
            document.Options.CompressContentStreams = true; document.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
            document.Save(temporary);
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(temporary, target, true);
            RememberFile(target);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        PdfDictionary CompressedStream(byte[] data, bool alreadyCompressed = false)
        {
            var dictionary = new PdfDictionary(document);
            dictionary.CreateStream(alreadyCompressed ? data : Compress(data)); dictionary.Elements.SetName("/Filter", "/FlateDecode");
            document.Internals.AddObject(dictionary); return dictionary;
        }
        PdfDictionary Appearance(IReadOnlyList<InkStroke> strokes, PdfPageGeometry geometry, PdfPageGeometry extent)
        {
            var content = new StringBuilder("q\n"); var states = new PdfDictionary(document);
            var alphas = new HashSet<byte>();
            foreach (var stroke in strokes)
            foreach (var layer in InkBrush.Layers(stroke))
                alphas.Add(layer.Color(stroke.Attributes.EffectiveColor).A);
            foreach (var alpha in alphas)
            {
                var gs = new PdfDictionary(document); gs.Elements.SetName("/Type", "/ExtGState");
                gs.Elements.SetReal("/ca", alpha / 255d); gs.Elements.SetReal("/CA", alpha / 255d);
                states.Elements["/GS" + alpha] = gs;
            }
            Span<Vector2> contour = stackalloc Vector2[26];
            foreach (var stroke in InkStroke.InDrawingOrder(strokes).SelectMany(InkCanvas.Core.Shapes.ShapeDefinition.RenderContours))
            {
                var waterInk = InkBrush.UsesInkWash(stroke) ? WaterInkPaths(stroke, token) : null;
                var layers = InkBrush.Layers(stroke);
                for (int layerIndex = 0; layerIndex < layers.Length; layerIndex++)
                {
                    var layer = layers[layerIndex];
                    token.ThrowIfCancellationRequested();
                    var color = layer.Color(stroke.Attributes.EffectiveColor);
                    content.Append('/').Append("GS").Append(color.A).Append(" gs\n").Append(Number(color.R / 255d)).Append(' ').Append(Number(color.G / 255d)).Append(' ').Append(Number(color.B / 255d)).Append(" rg\n");
                    var points = stroke.Points;
                    if (points.Count == 0) continue;
                    if (stroke.ShapeSource?.Geometry?.IsFill == true)
                    {
                        AppendPaths(FilledInkGeometry.ToPaths(stroke, 10000d)); content.Append("f\n"); continue;
                    }
                    if (waterInk is not null)
                    {
                        AppendPaths(Clipper.Union(waterInk[layerIndex], FillRule.NonZero));
                        content.Append("f\n");
                        continue;
                    }
                    // Union bounded groups of the renderer's exact polygon capsules. This removes
                    // covered cap edges from the PDF without fitting a different curve or rasterizing.
                    for (int batch = 0; batch < Math.Max(1, points.Count - 1); batch += 256)
                    {
                        token.ThrowIfCancellationRequested();
                        var paths = new Paths64();
                        int end = Math.Min(batch + 256, Math.Max(1, points.Count - 1));
                        for (int segment = batch; segment < end; segment++)
                        {
                            int length = StrokeGeometry.WriteInkSegment(stroke, segment, layer.WidthScale, contour);
                            var path = new Path64(length);
                            for (int p = 0; p < length; p++) path.Add(new Point64(Math.Round(contour[p].X * 10000d), Math.Round(contour[p].Y * 10000d)));
                            paths.Add(path);
                        }
                        AppendPaths(Clipper.Union(paths, FillRule.NonZero));
                    }
                    content.Append("f\n");
                }
            }

            void AppendPaths(Paths64 paths)
            {
                foreach (var outline in paths)
                {
                    for (int p = 0; p < outline.Count; p++)
                    {
                        var point = geometry.ToPdf(new Vector2(outline[p].X / 10000f, outline[p].Y / 10000f));
                        content.Append(Number(point.X - extent.Left)).Append(' ').Append(Number(point.Y - extent.Bottom)).Append(p == 0 ? " m\n" : " l\n");
                    }
                    content.Append("h\n");
                }
            }
            content.Append("Q\n");
            var form = CompressedStream(Encoding.ASCII.GetBytes(content.ToString()));
            form.Elements.SetName("/Type", "/XObject"); form.Elements.SetName("/Subtype", "/Form"); form.Elements.SetInteger("/FormType", 1);
            form.Elements["/BBox"] = new PdfRectangle(new PdfSharp.Drawing.XRect(0, 0, extent.Width, extent.Height));
            var resources = new PdfDictionary(document); resources.Elements["/ExtGState"] = states; form.Elements["/Resources"] = resources;
            return form;
        }

        static PdfRectangle Box(double left, double bottom, double right, double top)
            => new(new PdfSharp.Drawing.XRect(left, bottom, right - left, top - bottom));
        static PdfRectangle Rectangle(PdfPageGeometry bounds) => Box(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top);
    }
    private void RememberFile(string path)
    {
        var file = new FileInfo(path);
        _savedFiles[path] = (file.Length, file.LastWriteTimeUtc);
    }
    private static void ApplyPageOrder(PdfDocument document, int[] order, CancellationToken token)
    {
        var retained = order.ToHashSet();
        var removed = new HashSet<PdfPage>();
        for (int page = document.PageCount - 1; page >= 0; page--)
        {
            token.ThrowIfCancellationRequested();
            if (retained.Contains(page)) continue;
            removed.Add(document.Pages[page]);
            document.Pages.RemoveAt(page);
        }
        if (removed.Count > 0) RemoveDeletedBookmarks(document.Outlines);
        var current = order.Order().ToList();
        for (int position = 0; position < order.Length; position++)
        {
            token.ThrowIfCancellationRequested();
            if (current[position] == order[position]) continue;
            int previous = current.IndexOf(order[position], position + 1);
            document.Pages.MovePage(previous, position);
            current.RemoveAt(previous);
            current.Insert(position, order[position]);
        }

        PdfPage RemoveDeletedBookmarks(PdfOutlineCollection bookmarks)
        {
            PdfPage first = null;
            for (int i = bookmarks.Count - 1; i >= 0; i--)
            {
                var outline = bookmarks[i];
                var childPage = outline.HasChildren ? RemoveDeletedBookmarks(outline.Outlines) : null;
                var destination = outline.DestinationPage;
                if (destination is not null && removed.Contains(destination))
                {
                    if (childPage is null) { bookmarks.RemoveAt(i); continue; }
                    outline.DestinationPage = destination = childPage;
                }
                first = destination ?? childPage ?? first;
            }
            return first;
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream(); using (var stream = new ZLibStream(output, CompressionLevel.SmallestSize, true)) stream.Write(data);
        return output.ToArray();
    }
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static Paths64[] WaterInkPaths(InkStroke stroke, CancellationToken token)
    {
        var result = Enumerable.Range(0, 4).Select(_ => new Paths64()).ToArray();
        for (int start = 0; start < stroke.Count - 1; start += CalligraphyBrushGeometry.ChunkPointCount)
        {
            token.ThrowIfCancellationRequested();
            int end = Math.Min(start + CalligraphyBrushGeometry.ChunkPointCount, stroke.Count - 1);
            var chunk = CalligraphyBrushGeometry.BuildChunk(stroke, start, end, start == 0, end == stroke.Count - 1, true);
            if (chunk is null) continue;
            var holes = Paths(chunk.FeibaiCutouts);
            var layers = new[] { chunk.WetBody, chunk.GreyDryDrag, chunk.OuterClusters, chunk.CoreClusters };
            for (int layer = 0; layer < layers.Length; layer++)
            {
                var paths = Paths(layers[layer]);
                result[layer].AddRange(layer == 1 || holes.Count == 0 ? paths : Clipper.Difference(paths, holes, FillRule.NonZero));
            }
        }
        return result;

        static Paths64 Paths(Vector2[][] contours)
        {
            var paths = new Paths64(contours.Length);
            foreach (var contour in contours)
            {
                var path = new Path64(contour.Length);
                foreach (var point in contour) path.Add(new Point64(Math.Round(point.X * 10000d), Math.Round(point.Y * 10000d)));
                paths.Add(path);
            }
            return paths;
        }
    }

    public async Task<string> ReadPageTextAsync(int index, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index < 0 || index >= Pages.Count) throw new ArgumentOutOfRangeException(nameof(index), "PDF 页码不存在。");
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var document = UglyToad.PdfPig.PdfDocument.Open(RenderPath, new UglyToad.PdfPig.ParsingOptions { Password = Password });
                return document.GetPage(index + 1).Text;
            }, token);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<PdfSearchHit>> SearchAsync(string query, CancellationToken token = default,
        IReadOnlyList<int> pageOrder = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run<IReadOnlyList<PdfSearchHit>>(() =>
            {
                query = query?.Trim();
                if (string.IsNullOrEmpty(query)) return Array.Empty<PdfSearchHit>();
                using var document = UglyToad.PdfPig.PdfDocument.Open(RenderPath, new UglyToad.PdfPig.ParsingOptions { Password = Password });
                var results = new List<PdfSearchHit>();
                foreach (int index in pageOrder ?? Enumerable.Range(0, document.NumberOfPages))
                {
                    token.ThrowIfCancellationRequested();
                    var content = document.GetPage(index + 1);
                    var letters = content.Letters;
                    string text = string.Concat(letters.Select(letter => letter.Value));
                    var geometry = Pages[index];
                    float scaleX = geometry.DipWidth / (float)content.Width;
                    float scaleY = geometry.DipHeight / (float)content.Height;
                    for (int start = 0; start <= text.Length - query.Length && results.Count < 500;)
                    {
                        token.ThrowIfCancellationRequested();
                        int found = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                        if (found < 0) break;
                        int position = 0;
                        var bounds = new List<Rect>();
                        foreach (var letter in letters)
                        {
                            int end = position + letter.Value.Length;
                            if (end > found && position < found + query.Length && !string.IsNullOrWhiteSpace(letter.Value))
                            {
                                // PdfPig normalizes CropBox and rotation to a bottom-left visible page.
                                // Its coordinates omit UserUnit, accounted for by these page-size ratios.
                                var box = letter.BoundingBox;
                                double minX = Math.Min(Math.Min(box.BottomLeft.X, box.BottomRight.X), Math.Min(box.TopLeft.X, box.TopRight.X));
                                double maxX = Math.Max(Math.Max(box.BottomLeft.X, box.BottomRight.X), Math.Max(box.TopLeft.X, box.TopRight.X));
                                double minY = Math.Min(Math.Min(box.BottomLeft.Y, box.BottomRight.Y), Math.Min(box.TopLeft.Y, box.TopRight.Y));
                                double maxY = Math.Max(Math.Max(box.BottomLeft.Y, box.BottomRight.Y), Math.Max(box.TopLeft.Y, box.TopRight.Y));
                                float left = Math.Clamp((float)minX * scaleX, 0, geometry.DipWidth);
                                float top = Math.Clamp(geometry.DipHeight - (float)maxY * scaleY, 0, geometry.DipHeight);
                                float right = Math.Clamp((float)maxX * scaleX, left, geometry.DipWidth);
                                float bottom = Math.Clamp(geometry.DipHeight - (float)minY * scaleY, top, geometry.DipHeight);
                                if (right > left && bottom > top) bounds.Add(new(left, top, right, bottom));
                            }
                            position = end;
                            if (position >= found + query.Length) break;
                        }
                        int previewStart = Math.Max(0, found - 24);
                        results.Add(new PdfSearchHit(index, text.Substring(previewStart, Math.Min(100, text.Length - previewStart)), bounds));
                        start = found + query.Length;
                    }
                    if (results.Count >= 500) break;
                }
                return results;
            }, token);
        }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; try { Directory.Delete(_directory, true); } catch (IOException) { } }
        finally { _gate.Release(); }
    }
    private sealed class InkAnnotation : PdfAnnotation
    {
        public InkAnnotation(PdfDocument document) : base(document) { Elements.SetName("/Subtype", "/Stamp"); Elements.SetString("/NM", "InkCanvas-" + Guid.NewGuid().ToString("N")); }
    }
}
