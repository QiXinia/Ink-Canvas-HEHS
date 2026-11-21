using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InkCanvas.Features.Ppt;

/// <summary>
/// Produces a stable, file-content-aware key for a presentation. COM identity is
/// connection-local and cannot be persisted; this key is what archives are scoped by.
/// </summary>
public static class PresentationIdentity
{
    private const string UnknownKey = "unknown";

    public static string BuildKey(PptPresentationInfo presentation)
    {
        if (presentation is null) return UnknownKey;

        if (string.IsNullOrWhiteSpace(presentation.FullName))
        {
            if (string.IsNullOrWhiteSpace(presentation.Name)) return UnknownKey;
            return Hash(string.Join('|',
                presentation.Name,
                presentation.SlideCount.ToString(CultureInfo.InvariantCulture),
                Format(presentation.SlideWidth),
                Format(presentation.SlideHeight)));
        }

        string fullPath = NormalizePath(presentation.FullName);
        if (string.IsNullOrEmpty(fullPath)) return UnknownKey;

        try
        {
            var file = new FileInfo(fullPath);
            if (file.Exists)
            {
                return Hash(string.Join('|',
                    fullPath,
                    file.Length.ToString(CultureInfo.InvariantCulture),
                    file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
        }

        return Hash(fullPath);
    }

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path.Trim()).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            return path.Trim().ToLowerInvariant();
        }
    }

    /// <summary>First 16 bytes of SHA-256 as 32 lowercase hex characters.</summary>
    public static string Hash(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}