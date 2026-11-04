namespace RouteGo.Fleet.Api.Common;

/// <summary>One page of a list, as the caller asked for it after the defaults are applied.</summary>
/// <param name="Page">The 1-based page number.</param>
/// <param name="PageSize">How many items the page holds at most.</param>
public sealed record PageRequest(int Page, int PageSize)
{
    /// <summary>How many items come before this page.</summary>
    public int Offset
    {
        get
        {
            return (Page - 1) * PageSize;
        }
    }
}
