namespace RouteGo.Fleet.Api.Common.Ports;

// The media store bus photos live in. Files never pass through Fleet: the operator's browser sends
// each one straight to the store through an upload address Fleet issues, and customers read them
// through the CDN. The adapter is Infrastructure/S3MediaStore.
public interface IMediaStore
{
    /// <summary>
    /// An address that accepts exactly one HTTP PUT of a file to <paramref name="fileLocation"/>,
    /// declaring <paramref name="contentType"/>, until <paramref name="expiresAt"/>. Signing is
    /// local work: nothing is sent to the store.
    /// </summary>
    Uri CreateUploadAddress(string fileLocation, string contentType, DateTimeOffset expiresAt);

    /// <summary>
    /// The size and first <paramref name="headerLength"/> bytes of the file at
    /// <paramref name="fileLocation"/>, or null when no file has been stored there.
    /// </summary>
    Task<StoredFile?> GetFileAsync(string fileLocation, int headerLength, CancellationToken cancellationToken);

    /// <summary>Removes the file at <paramref name="fileLocation"/>; removing a file that is not there succeeds.</summary>
    Task DeleteFileAsync(string fileLocation, CancellationToken cancellationToken);
}

/// <summary>What Fleet reads of a stored file to check it: its size and its first bytes.</summary>
/// <param name="SizeBytes">The whole file's size.</param>
/// <param name="Header">The file's first bytes; fewer than asked for when the file is shorter.</param>
public sealed record StoredFile(long SizeBytes, byte[] Header);
