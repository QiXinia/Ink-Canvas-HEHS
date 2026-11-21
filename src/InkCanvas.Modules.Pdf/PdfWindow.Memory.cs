namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    internal bool ReleaseInactiveResources() => _idleResources.ReleaseNow();

    private void ReleaseInactiveRendering()
    {
        StopScrollMotion();
        _renderCancellation?.Cancel();
        _detailCancellation?.Cancel();
        _detailTimer.Stop();
        _generation++;
        _requestedPages = Array.Empty<int>();
        ReleasePagePreviews();
        _detail?.Dispose();
        _detail = null;
        SuspendThumbnails();
        _ink.SuspendRendering();
    }

    private void RestoreRendering()
    {
        if (_closed) return;
        _ink.ResumeRendering();
        _paper.Invalidate();
        QueuePageRendering();
        RefreshThumbnails();
    }
}
