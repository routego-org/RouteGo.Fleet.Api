namespace RouteGo.Fleet.Api.Features.Trips.CreateSchedule;

public sealed class CreateScheduleValidator : AbstractValidator<CreateScheduleRequest>
{
    public CreateScheduleValidator()
    {
        RuleFor(x => x.RouteId).NotNull().WithMessage("The route is required.");
        RuleFor(x => x.BusId).NotNull().WithMessage("The bus is required.");
        RuleFor(x => x.DepartureTime).Must(TripRules.IsAcceptableDepartureTime)
            .WithMessage("The departure time must be HH:mm on the 24-hour clock, from 00:00 to 23:59.");
        RuleFor(x => x.DaysOfWeek).NotNull().WithMessage("The days of service are required.");
        RuleFor(x => x.DaysOfWeek!).Must(HaveAcceptableDayCount).When(x => x.DaysOfWeek != null)
            .WithMessage("A schedule runs on 1 to 7 days of the week.");
        RuleFor(x => x.DaysOfWeek!).Must(HaveNoRepeatedDay).When(x => x.DaysOfWeek != null)
            .WithMessage("Each day of the week may be listed at most once.");
        RuleFor(x => x.ClassFares).NotNull().WithMessage("The class fares are required.");
        RuleFor(x => x.ClassFares!).Must(HaveAcceptableFareCount).When(x => x.ClassFares != null)
            .WithMessage("A schedule gives 1 to 5 class fares, one for each of the bus's seat classes.");
        RuleForEach(x => x.ClassFares).NotNull().WithMessage("Each class fare must be given.");
        RuleForEach(x => x.ClassFares).SetValidator(request => new ClassFareInputValidator(request.ClassFares!));
    }

    private static bool HaveAcceptableDayCount(IReadOnlyList<DayOfWeek> days)
    {
        return days.Count >= 1 && days.Count <= TripRules.MaxDaysOfWeek;
    }

    private static bool HaveNoRepeatedDay(IReadOnlyList<DayOfWeek> days)
    {
        return days.Distinct().Count() == days.Count;
    }

    private static bool HaveAcceptableFareCount(IReadOnlyList<ClassFareInput> classFares)
    {
        return classFares.Count >= 1 && classFares.Count <= TripRules.MaxClassFares;
    }
}

public sealed class ClassFareInputValidator : AbstractValidator<ClassFareInput>
{
    private readonly IReadOnlyList<ClassFareInput> _classFares;

    public ClassFareInputValidator(IReadOnlyList<ClassFareInput> classFares)
    {
        _classFares = classFares;

        RuleFor(x => x.SeatClass).Must(BusRules.IsAcceptableSeatClassName)
            .WithMessage("Each seat class name must be 2 to 30 characters.");
        RuleFor(x => x.SeatClass).Must(BePricedOnce).When(x => x.SeatClass != null)
            .WithMessage("Each seat class may have only one fare, ignoring case.");
        RuleFor(x => x.Fare).Must(TripRules.IsAcceptableFare)
            .WithMessage("Each fare must be whole rupees from 50 to 10,000.");
    }

    private bool BePricedOnce(string? seatClass)
    {
        int pricings = _classFares.Count(other => other?.SeatClass != null && BusRules.IsSameSeatClass(other.SeatClass, seatClass!));
        return pricings == 1;
    }
}
