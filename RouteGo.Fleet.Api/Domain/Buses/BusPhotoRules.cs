namespace RouteGo.Fleet.Api.Domain.Buses;

/// <summary>
/// The rules every bus photo obeys: its formats, size limit, the number a bus may have, how long an
/// upload slot lasts, and where its file is kept in the media store.
/// </summary>
public static class BusPhotoRules
{
    /// <summary>The largest photo file accepted: 3 MB.</summary>
    public const long MaxSizeBytes = 3_145_728;

    /// <summary>How many photos a bus may have, counting uploaded photos and unexpired upload slots.</summary>
    public const int MaxPhotosPerBus = 5;

    /// <summary>How many leading bytes identify a file's format: the length of the PNG signature, the longer of the two.</summary>
    public const int SignatureLength = 8;

    private const string JpegContentType = "image/jpeg";
    private const string PngContentType = "image/png";

    /// <summary>How long an upload slot accepts its file.</summary>
    public static readonly TimeSpan UploadSlotLifetime = TimeSpan.FromMinutes(5);

    // Every JPEG starts with the start-of-image marker followed by another marker; every PNG with
    // its fixed eight-byte signature.
    private static readonly byte[] _jpegSignature = new byte[] { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] _pngSignature = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// What is wrong with a stored file, or null when it may become the photo: it must be no larger
    /// than the maximum and, judged by its first bytes, be a file of the declared format.
    /// </summary>
    public static PhotoFileFault? FindFault(PhotoFormat declared, long sizeBytes, byte[] header)
    {
        if (sizeBytes > MaxSizeBytes)
        {
            return PhotoFileFault.TooLarge;
        }

        PhotoFormat? actual = DetectFormat(header);
        if (actual is null)
        {
            return PhotoFileFault.UnsupportedFormat;
        }

        if (actual.Value != declared)
        {
            return PhotoFileFault.FormatMismatch;
        }

        return null;
    }

    private static PhotoFormat? DetectFormat(byte[] header)
    {
        if (header.AsSpan().StartsWith(_jpegSignature))
        {
            return PhotoFormat.Jpeg;
        }

        if (header.AsSpan().StartsWith(_pngSignature))
        {
            return PhotoFormat.Png;
        }

        return null;
    }

    /// <summary>Whether a declared or stored file size is within the limit: at least 1 byte, at most 3 MB.</summary>
    public static bool IsAcceptableSize(long? sizeBytes)
    {
        if (sizeBytes is null)
        {
            return false;
        }

        return sizeBytes.Value >= 1 && sizeBytes.Value <= MaxSizeBytes;
    }

    /// <summary>The content type an upload of this format must declare.</summary>
    public static string GetContentType(PhotoFormat format)
    {
        switch (format)
        {
            case PhotoFormat.Jpeg:
                return JpegContentType;
            case PhotoFormat.Png:
                return PngContentType;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown photo format.");
        }
    }

    /// <summary>
    /// Where the photo's file is kept: under the bus's photo area, named after the photo, so a slot
    /// can only ever write that one place.
    /// </summary>
    public static string GetFileLocation(Guid busId, Guid photoId)
    {
        return $"buses/{busId}/photos/{photoId}";
    }
}
