using System.Net.Http.Headers;

namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// The operator's browser, as far as the media store sees it: it sends a file to an upload address
/// with a plain HTTP PUT, carrying no RouteGo credentials.
/// </summary>
public sealed class MediaStoreBrowser
{
    private readonly HttpClient _http;

    public MediaStoreBrowser(HttpClient http)
    {
        _http = http;
    }

    /// <summary>PUTs <paramref name="content"/> to the upload address, declaring <paramref name="contentType"/>.</summary>
    public async Task<HttpStatusCode> UploadAsync(string uploadUrl, string contentType, byte[] content)
    {
        using var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using HttpResponseMessage upload = await _http.PutAsync(uploadUrl, file);
        return upload.StatusCode;
    }
}
