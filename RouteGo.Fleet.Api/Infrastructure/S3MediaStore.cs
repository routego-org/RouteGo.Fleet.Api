using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace RouteGo.Fleet.Api.Infrastructure;

/// <summary>
/// The media store as an S3 bucket. Immutable and thread-safe, so one instance serves every request.
/// </summary>
public sealed class S3MediaStore : IMediaStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3MediaStore(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public Uri CreateUploadAddress(string fileLocation, string contentType, DateTimeOffset expiresAt)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = fileLocation,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
            Protocol = ProtocolOf(_s3.Config.ServiceURL),
        };
        return new Uri(_s3.GetPreSignedURL(request));
    }

    public async Task<StoredFile?> GetFileAsync(string fileLocation, int headerLength, CancellationToken cancellationToken)
    {
        long? sizeBytes = await GetSizeAsync(fileLocation, cancellationToken);
        if (sizeBytes is null)
        {
            return null;
        }

        // An empty file has no bytes to range over; S3 refuses the range rather than returning none.
        if (sizeBytes.Value == 0)
        {
            return new StoredFile(0, Array.Empty<byte>());
        }

        long lastByte = Math.Min(headerLength, sizeBytes.Value) - 1;
        var request = new GetObjectRequest { BucketName = _bucket, Key = fileLocation, ByteRange = new ByteRange(0, lastByte) };
        using GetObjectResponse response = await _s3.GetObjectAsync(request, cancellationToken);
        using var header = new MemoryStream();
        await response.ResponseStream.CopyToAsync(header, cancellationToken);
        return new StoredFile(sizeBytes.Value, header.ToArray());
    }

    public async Task DeleteFileAsync(string fileLocation, CancellationToken cancellationToken)
    {
        await _s3.DeleteObjectAsync(_bucket, fileLocation, cancellationToken);
    }

    // A missing file is an expected answer here — the browser may not have sent it yet.
    private async Task<long?> GetSizeAsync(string fileLocation, CancellationToken cancellationToken)
    {
        try
        {
            GetObjectMetadataResponse metadata = await _s3.GetObjectMetadataAsync(_bucket, fileLocation, cancellationToken);
            return metadata.ContentLength;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // A local endpoint is plain HTTP; S3 itself is always HTTPS.
    private static Protocol ProtocolOf(string? serviceUrl)
    {
        if (serviceUrl != null && serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return Protocol.HTTP;
        }

        return Protocol.HTTPS;
    }
}
