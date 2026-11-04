namespace RouteGo.Fleet.Api.Tests.Support;

/// <summary>
/// Operator profiles as the OperatorApproved worker writes them, for tests that need an operator
/// who passes the profile gate.
/// </summary>
public static class TestOperators
{
    private const string InsertProfileSql = """
        INSERT INTO operator_profiles.operator_profiles (operator_id, company_name, email, rating, approved_at)
        VALUES (@OperatorId, 'BusCo Travels', 'owner@busco.in', 4.0, '2026-10-09T09:00:00Z')
        """;

    /// <summary>Seeds a profile for a new operator and returns its identifier.</summary>
    public static async Task<Guid> SeedProfileAsync(DatabaseFixture database)
    {
        var operatorId = Guid.NewGuid();
        await database.SeedAsync(InsertProfileSql, new { OperatorId = operatorId.ToString() });
        return operatorId;
    }
}
