namespace RouteGo.Fleet.Api.Features.Routes.ListRoutes;

public sealed class ListRoutesValidator : AbstractValidator<ListRoutesRequest>
{
    public ListRoutesValidator()
    {
        RuleFor(x => x.Status).Must(IsRouteStatus).When(x => x.Status != null)
            .WithMessage("The status must be active or inactive.");
        RuleFor(x => x.Page).Must(Paging.IsAcceptablePage).WithMessage(Paging.PageMessage);
        RuleFor(x => x.PageSize).Must(Paging.IsAcceptablePageSize).WithMessage(Paging.PageSizeMessage);
    }

    private static bool IsRouteStatus(string? status)
    {
        return WireJson.TryReadEnum<RouteStatus>(status, out _);
    }
}
