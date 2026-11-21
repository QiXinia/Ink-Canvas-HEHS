using System.IO.Compression;
using System.Numerics;
using System.Text;
using InkCanvas.Core.Imaging;
using InkCanvas.Features.Board;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class MediaImportTests
{
    [Theory]
    [InlineData("width='2in' height='1in'", 192, 96)]
    [InlineData("width='72pt' height='36pt'", 96, 48)]
    [InlineData("viewBox='-10 20 400 200'", 400, 200)]
    [InlineData("width='100%' height='100%' viewBox='0 0 640 360'", 640, 360)]
    [InlineData("width='120' viewBox='0 0 4 2'", 120, 60)]
    [InlineData("height='60' viewBox='0 0 4 2'", 120, 60)]
    [InlineData("", 300, 150)]
    public void SvgDimensionsRespectUnitsAndViewbox(string attributes, float width, float height)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes($"<svg xmlns='http://www.w3.org/2000/svg' {attributes}><path d='M0 0L10 10'/></svg>"));
        var svg = SvgImage.Read(source);
        Assert.Equal(new Vector2(width, height), svg.Size);
        Assert.Contains("<path", svg.Xml);
    }

    [Fact]
    public void CompressedSvgRetainsVectorContentAndIgnoresExternalDoctypes()
    {
        const string xml = "<!DOCTYPE svg SYSTEM 'file:///not-used.dtd'><svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'><circle cx='50' cy='50' r='30'/></svg>";
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(xml));
        compressed.Position = 0;
        var image = SvgImage.Read(compressed, compressed: true);
        Assert.Equal(new Vector2(200, 100), image.Size);
        Assert.Contains("circle", image.Xml);
        Assert.DoesNotContain("DOCTYPE", image.Xml);
    }

    [Fact]
    public void NamespaceFreeSvgIsNormalizedForTheNativeRenderer()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<svg viewBox='0 0 200 100'><g><path d='M0 0L20 20'/></g></svg>"));
        var svg = SvgImage.Read(source);
        var document = System.Xml.Linq.XDocument.Parse(svg.Xml);
        Assert.All(document.Root.DescendantsAndSelf(), element => Assert.Equal("http://www.w3.org/2000/svg", element.Name.NamespaceName));
        Assert.Equal(new Vector2(200, 100), svg.Size);
    }

    [Fact]
    public void InvalidVectorFileFailsBeforeInsertion()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("<html><body /></html>"));
        Assert.Throws<InvalidDataException>(() => SvgImage.Read(source));
    }

    [Fact]
    public void VideoReadStreamsShareEmbeddedBytesAndHaveIndependentSeekPositions()
    {
        var bytes = new byte[16 * 1024 * 1024];
        bytes[0] = 7; bytes[bytes.Length - 1] = 99;
        var media = new BoardMediaAsset { Data = bytes, NaturalWidth = 1920, NaturalHeight = 1080, PosterData = new byte[] { 1 } };
        using var first = media.OpenRead();
        using var second = media.OpenRead();
        Assert.Same(bytes, first.GetBuffer()); Assert.Same(bytes, second.GetBuffer());
        Assert.Equal(7, first.ReadByte()); Assert.Equal(0, second.Position);
        first.Seek(-1, SeekOrigin.End); Assert.Equal(99, first.ReadByte());
        Assert.False(first.CanWrite);
        var clone = media.Copy(preserveId: false, copyData: false);
        Assert.Same(bytes, clone.Data); Assert.Same(media.PosterData, clone.PosterData);
        Assert.Equal(1920u, clone.NaturalWidth); Assert.Equal(1080u, clone.NaturalHeight);
    }
}
