namespace RouteGo.Fleet.Api.Features.OperatorProfiles.CreateOperatorProfile;

/// <summary>One OperatorApproved event as the fleet queue delivered it.</summary>
public sealed record OperatorApprovedDelivery
{
    /// <summary>The event's identity; the same on every repeat delivery.</summary>
    public required string EventId { get; init; }

    /// <summary>When the admin approved the operator; kept as the profile's approval time.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The approval request's correlation ID, kept on every log line for this event.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The event data: operatorId, companyName and email.</summary>
    public required JsonElement Data { get; init; }

    /// <summary>True when the queue parks the message if this attempt fails too.</summary>
    public required bool IsLastAttempt { get; init; }
}

/// <summary>What became of one delivery; it decides whether the queue keeps the message.</summary>
public enum CreateOperatorProfileOutcome
{
    /// <summary>The profile was created.</summary>
    Created,

    /// <summary>The operator already had a profile, so nothing changed.</summary>
    AlreadyExists,

    /// <summary>The event lacks usable data; trying again cannot help.</summary>
    InvalidEventData,

    /// <summary>The profile could not be stored this time; the event must be delivered again.</summary>
    RetryLater,
}

/// <summary>
/// Creates the operator profile for an approved operator, from Identity's OperatorApproved event.
/// The operator identifier is the key: a repeat of the event, or a later approval event for the same
/// operator, changes nothing.
/// </summary>
public sealed class CreateOperatorProfileHandler
{
    /// <summary>The producer's event type that creates a profile.</summary>
    public const string TriggerEventType = "OperatorApproved";

    // The job: prefix keeps event-driven writes separable from operator actions in the audit log.
    private const string AuditAction = "job:CreateOperatorProfile";
    private const string InvalidEventDataReason = "invalidEventData";
    private const string RepeatedFailureReason = "repeatedFailure";

    // A profile, once created, is never changed by a later approval event for the same operator.
    private const string InsertProfileSql = """
        INSERT INTO operator_profiles.operator_profiles (operator_id, company_name, email, rating, approved_at)
        VALUES (@OperatorId, @CompanyName, @Email, @Rating, @ApprovedAt)
        ON CONFLICT (operator_id) DO NOTHING
        """;

    private readonly IWriterConnectionFactory _writerFactory;
    private readonly ILogger _logger;

    public CreateOperatorProfileHandler(IWriterConnectionFactory writerFactory, ILogger logger)
    {
        _writerFactory = writerFactory;
        _logger = logger;
    }

    /// <summary>Creates the profile the event describes, or says why it cannot be created now.</summary>
    public async Task<CreateOperatorProfileOutcome> HandleAsync(
        OperatorApprovedDelivery delivery, CancellationToken cancellationToken)
    {
        ApprovedOperator? approved = ReadApprovedOperator(delivery.Data);
        if (approved is null)
        {
            _logger.LogError(
                "{Code}: OperatorApproved event {EventId} (correlation {CorrelationId}) created no profile, reason {Reason}: "
                + "the operator identifier, company name or email is missing or unusable; it will not be retried",
                ProblemTypes.OperatorProfileNotCreated.Code, delivery.EventId, delivery.CorrelationId, InvalidEventDataReason);
            return CreateOperatorProfileOutcome.InvalidEventData;
        }

        try
        {
            return await CreateAsync(approved, delivery, cancellationToken);
        }
        // Only our own shutdown ends the attempt; any other database failure is temporary, and the
        // queue delivers the event again.
        catch (DbException exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportTemporaryFailure(delivery, exception);
            return CreateOperatorProfileOutcome.RetryLater;
        }
    }

    private async Task<CreateOperatorProfileOutcome> CreateAsync(
        ApprovedOperator approved, OperatorApprovedDelivery delivery, CancellationToken cancellationToken)
    {
        var parameters = new
        {
            approved.OperatorId,
            approved.CompanyName,
            approved.Email,
            Rating = OperatorProfileRules.DefaultRating,
            ApprovedAt = delivery.OccurredAt,
        };
        await using IWriterConnection writer = await _writerFactory.OpenWriteTransactionAsync(AuditAction, cancellationToken);
        int inserted = await writer.Connection.ExecuteAsync(new CommandDefinition(
            InsertProfileSql, parameters, writer.Transaction, cancellationToken: cancellationToken));
        await writer.CommitAsync(cancellationToken);

        if (inserted == 0)
        {
            _logger.LogInformation(
                "Operator {OperatorId} already has a profile; OperatorApproved event {EventId} (correlation {CorrelationId}) changed nothing",
                approved.OperatorId, delivery.EventId, delivery.CorrelationId);
            return CreateOperatorProfileOutcome.AlreadyExists;
        }

        _logger.LogInformation(
            "Operator profile created for operator {OperatorId} from OperatorApproved event {EventId} (correlation {CorrelationId})",
            approved.OperatorId, delivery.EventId, delivery.CorrelationId);
        return CreateOperatorProfileOutcome.Created;
    }

    // On the last attempt the alert is raised here; the message itself is left on the queue, which
    // then parks it for the platform engineer to replay after the fix.
    private void ReportTemporaryFailure(OperatorApprovedDelivery delivery, DbException exception)
    {
        if (!delivery.IsLastAttempt)
        {
            _logger.LogWarning(
                exception, "OperatorApproved event {EventId} (correlation {CorrelationId}) could not be stored yet; it will be delivered again",
                delivery.EventId, delivery.CorrelationId);
            return;
        }

        _logger.LogError(
            exception, "{Code}: OperatorApproved event {EventId} (correlation {CorrelationId}) created no profile, reason {Reason}: "
            + "every attempt failed and the event is parked for replay",
            ProblemTypes.OperatorProfileNotCreated.Code, delivery.EventId, delivery.CorrelationId, RepeatedFailureReason);
    }

    // The profile takes the company name and email exactly as given; only their presence and the
    // company name's length are checked, because the profile table refuses anything else.
    private static ApprovedOperator? ReadApprovedOperator(JsonElement data)
    {
        string? operatorId = ReadText(data, "operatorId");
        string? companyName = ReadText(data, "companyName");
        string? email = ReadText(data, "email");
        if (operatorId is null || companyName is null || email is null)
        {
            return null;
        }

        if (!OperatorProfileRules.IsAcceptableCompanyName(companyName))
        {
            return null;
        }

        return new ApprovedOperator(operatorId, companyName, email);
    }

    private static string? ReadText(JsonElement data, string propertyName)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!data.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text;
    }

    /// <summary>The approved operator as the event names it.</summary>
    /// <param name="OperatorId">Identity's operator account identifier, the token's operatorId claim.</param>
    /// <param name="CompanyName">The company name.</param>
    /// <param name="Email">The operator's sign-in email.</param>
    private sealed record ApprovedOperator(string OperatorId, string CompanyName, string Email);
}
