namespace RouteGo.Fleet.Api.Features.Buses.ListBuses;

public sealed class ListBusesValidator : AbstractValidator<ListBusesRequest>
{
    public ListBusesValidator()
    {
        RuleFor(x => x.Status).Must(IsBusStatus).When(x => x.Status != null)
            .WithMessage("The status must be active or inactive.");
        RuleFor(x => x.Page).Must(Paging.IsAcceptablePage).WithMessage(Paging.PageMessage);
        RuleFor(x => x.PageSize).Must(Paging.IsAcceptablePageSize).WithMessage(Paging.PageSizeMessage);
    }

    private static bool IsBusStatus(string? status)
    {
        return WireJson.TryReadEnum<BusStatus>(status, out _);
    }
}
