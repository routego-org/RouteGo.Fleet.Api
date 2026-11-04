namespace RouteGo.Fleet.Api.Domain.Buses;

/// <summary>Why a stored photo file cannot be used.</summary>
public enum PhotoFileFault
{
    /// <summary>Larger than the photo maximum size.</summary>
    TooLarge,

    /// <summary>A JPEG or PNG, but not the format the operator declared.</summary>
    FormatMismatch,

    /// <summary>Neither a JPEG nor a PNG.</summary>
    UnsupportedFormat,
}
