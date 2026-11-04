namespace RouteGo.Fleet.Api.Features.Routes.CreateRoute;

public sealed class CreateRouteValidator : AbstractValidator<CreateRouteRequest>
{
    public CreateRouteValidator()
    {
        RuleFor(x => x.Stops).NotNull().WithMessage("The stops are required.");
        RuleFor(x => x.Stops!).Must(stops => RouteRules.IsAcceptableStopCount(stops.Count)).When(x => x.Stops != null)
            .WithMessage("A route has 2 to 12 stops.");
        RuleForEach(x => x.Stops).SetValidator((request, _) => new RouteStopValidator(request.Stops!));
        RuleFor(x => x.DistanceKm).Must(distance => distance != null && RouteRules.IsAcceptableDistance(distance.Value))
            .WithMessage("The distance must be a whole number of kilometres from 1 to 3,000.");
    }
}

public sealed class RouteStopValidator : AbstractValidator<RouteStopInput>
{
    private readonly IReadOnlyList<RouteStopInput> _stops;

    public RouteStopValidator(IReadOnlyList<RouteStopInput> stops)
    {
        _stops = stops;

        RuleFor(x => x.CityId).NotNull().WithMessage("The stop's city is required.");
        RuleFor(x => x.CityId).Must(BeVisitedOnce).When(x => x.CityId != null)
            .WithMessage("No city may appear twice on a route.");
        RuleFor(x => x.OffsetMinutes).NotNull().WithMessage("The stop's offset is required.");
        RuleFor(x => x.OffsetMinutes).Must(FollowThePreviousStop).When(x => x.OffsetMinutes != null)
            .WithMessage("The first stop's offset must be 0; each later stop's must be greater than the one before and at most 2,880 minutes.");
    }

    private bool BeVisitedOnce(RouteStopInput stop, Guid? cityId)
    {
        int visits = _stops.Count(other => other.CityId == cityId);
        return visits == 1;
    }

    private bool FollowThePreviousStop(RouteStopInput stop, int? offsetMinutes)
    {
        int position = IndexOf(stop);
        int? previousOffsetMinutes = null;
        if (position > 0)
        {
            previousOffsetMinutes = _stops[position - 1].OffsetMinutes;
        }

        return RouteRules.IsAcceptableOffset(position, offsetMinutes!.Value, previousOffsetMinutes);
    }

    // Stops are compared by reference: two stops may carry identical values and still be different places in the list.
    private int IndexOf(RouteStopInput stop)
    {
        for (int position = 0; position < _stops.Count; position++)
        {
            if (ReferenceEquals(_stops[position], stop))
            {
                return position;
            }
        }

        throw new InvalidOperationException("The stop being validated is not in the route's stops.");
    }
}
