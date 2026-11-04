namespace RouteGo.Fleet.Api.Features.Trips.ReplaceTripFares;

public sealed class ReplaceTripFaresValidator : AbstractValidator<ReplaceTripFaresRequest>
{
    public ReplaceTripFaresValidator()
    {
        RuleFor(x => x.ClassFares).NotNull().WithMessage("The class fares are required.");
        RuleFor(x => x.ClassFares!).Must(HaveAcceptableFareCount).When(x => x.ClassFares != null)
            .WithMessage("A trip gives 1 to 5 class fares, one for each of the bus's seat classes.");
        RuleForEach(x => x.ClassFares).NotNull().WithMessage("Each class fare must be given.");
        RuleForEach(x => x.ClassFares).SetValidator(request => new ClassFareInputValidator(request.ClassFares!));
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
