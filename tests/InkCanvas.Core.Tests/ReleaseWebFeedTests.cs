using InkCanvas.Core.Update;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class ReleaseWebFeedTests
{
    private const string Repository = "QiXinia/Ink-Canvas-HEHS";

    [Fact]
    public void ReleasePageExcludesPreviewsUnrelatedRepositoriesAndMalformedTags()
    {
        const string html = """
            <section><a href="/QiXinia/Ink-Canvas-HEHS/releases/tag/v3.7.1">Ink &amp; Canvas</a>
            <div class="markdown-body">修复 <strong>更新</strong></div></section>
            <section><a href="/QiXinia/Ink-Canvas-HEHS/releases/tag/v9.0.0">Preview</a><span>Pre-release</span></section>
            <section><a href="/other/repository/releases/tag/v8.0.0">Other</a></section>
            <section><a href="https://github.com.evil.example/QiXinia/Ink-Canvas-HEHS/releases/tag/v8.0.0">Invalid</a></section>
            <section><a href="/QiXinia/Ink-Canvas-HEHS/releases/tag/latest">Malformed</a></section>
            """;
        var release = Assert.Single(ReleaseWebFeed.ReadEntries(html, Repository));
        Assert.Equal("v3.7.1", release.Tag); Assert.Equal("Ink & Canvas", release.Name);
        Assert.Contains("更新", release.Notes);
        Assert.Empty(ReleaseWebFeed.ReadEntries("<html>Access denied</html>", Repository));
    }

    [Fact]
    public void AssetsKeepTheMatchingPackageDigestAndChecksumLink()
    {
        const string html = """
            <ul>
            <li><a href="/QiXinia/Ink-Canvas-HEHS/releases/download/v3.7.1/inkcanvas.pdf-1.zip">PDF module</a><span>sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb</span></li>
            <li><a href="/QiXinia/Ink-Canvas-HEHS/releases/download/v3.7.1/InkCanvas-WinUI3-3.7.1-win-x64.zip">Application</a><span>sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</span></li>
            <li><a href="/QiXinia/Ink-Canvas-HEHS/releases/download/v3.7.1/SHA256SUMS.txt">Checksums</a></li>
            <li><a href="https://github.com.evil.example/QiXinia/Ink-Canvas-HEHS/releases/download/v3.7.1/InkCanvas-WinUI3-3.7.1-win-x64.zip">Invalid</a></li>
            </ul>
            """;
        var entry = new ReleaseWebFeed.Entry("v3.7.1", "Release", "Notes");
        var release = ReleaseWebFeed.ReadAssets(html, Repository, entry);
        Assert.True(release.IsPortable); Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Equal("https://github.com/" + Repository + "/releases/download/v3.7.1/SHA256SUMS.txt", release.ChecksumsUrl);
        Assert.True(ReleaseWebFeed.IsAssetsPage(html, Repository, entry));
        Assert.False(ReleaseWebFeed.IsAssetsPage(html, Repository, entry with { Tag = "v3.7.2" }));
    }

    [Fact]
    public void LatestChecksumFallbackOnlyAcceptsStablePortableApplicationNames()
    {
        string sums = new string('a', 64) + "  InkCanvas-WinUI3-3.7.1-win-x64.zip\n"
            + new string('b', 64) + "  inkcanvas.pdf-9.0.0.zip\n"
            + new string('c', 64) + "  InkCanvas-WinUI3-99.0.0-beta-win-x64.zip\n"
            + new string('d', 64) + "  ../InkCanvas-WinUI3-99.0.0-win-x64.zip\n";
        var release = ReleaseFeed.ParseLatestChecksums(sums, Repository);
        Assert.Equal("v3.7.1", release.Tag); Assert.Equal(new string('a', 64), release.Sha256);
        Assert.Equal("https://github.com/" + Repository + "/releases/latest/download/InkCanvas-WinUI3-3.7.1-win-x64.zip", release.InstallerUrl);
        Assert.Null(ReleaseFeed.ParseLatestChecksums("<html>Unavailable</html>", Repository));
    }
}
