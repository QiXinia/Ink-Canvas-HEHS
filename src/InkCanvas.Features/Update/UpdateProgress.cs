namespace InkCanvas.Features.Update;

public enum UpdateStage { Idle, Checking, Downloading, Verifying, Preparing, Waiting, Installing, Failed, Canceled }

public sealed record UpdateProgress(UpdateStage Stage, double? Fraction = null, long DownloadedBytes = 0,
    long TotalBytes = 0, double BytesPerSecond = 0, string Source = null);
