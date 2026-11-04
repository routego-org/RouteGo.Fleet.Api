namespace RouteGo.Fleet.Api.Common;

/// <summary>
/// Turns a bus photo's place in the media store into the CDN address customers load it from. The
/// media store itself is private, so the CDN address is the only one ever returned.
/// </summary>
public sealed class PhotoAddresses
{
    private readonly Uri _cdnBaseAddress;

    /// <param name="cdnBaseAddress">The CDN base address, ending in '/' so a file location is appended to it, not swapped for its last segment.</param>
    public PhotoAddresses(Uri cdnBaseAddress)
    {
        _cdnBaseAddress = cdnBaseAddress;
    }

    /// <summary>The CDN address of the file kept at <paramref name="fileLocation"/> in the media store.</summary>
    public Uri GetAddress(string fileLocation)
    {
        return new Uri(_cdnBaseAddress, fileLocation);
    }
}
