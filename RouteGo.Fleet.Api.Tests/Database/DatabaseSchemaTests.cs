// The three layers every database gets for free (references/testing.md): Layer 0 proves the
// snake_case -> PascalCase mapping is on and actually maps; Layer 1 proves the whole migration
// history applied to a fresh database and left the audit partitions with a year of runway, a
// DEFAULT circuit breaker and UTC bounds; Layer 2 proves every business-schema table is audited,
// or declared excluded, and never half of each. Layer 3 (behaviour) and Layer 4 (data-model
// coverage) are per-table and live beside the slices.
//
// Ships from skills-db-postgres (assets/Tests/Database/). Never hand-edited: an app's own schema
// assertions go in a class of their own that joins DatabaseCollection.

using Dapper;
using Npgsql;

namespace RouteGo.Fleet.Api.Tests.Database;

/// <summary>Proves the migration history itself, on the shared migrated database.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class DatabaseSchemaTests
{
    // Layer 0: no table needed. Dapper maps the result set's column names, so two multi-word
    // aliases are the whole test — and they never depend on which tables this app has.
    private const string MultiWordColumnsSql = """
        SELECT 'mapping@example.test'::text AS created_by_email, true AS is_mapped;
        """;

    private const string FutureMonthlyPartitionCountSql = """
        SELECT count(*)
        FROM pg_inherits i
        JOIN pg_class c ON c.oid = i.inhrelid
        JOIN pg_class p ON p.oid = i.inhparent
        JOIN pg_namespace n ON n.oid = p.relnamespace
        WHERE n.nspname = 'audit' AND p.relname = 'change_log'
          AND c.relname ~ '^change_log_\d{4}_\d{2}$'
          AND to_date(substring(c.relname from '\d{4}_\d{2}$'), 'YYYY_MM') > date_trunc('month', now())::date;
        """;

    private const string DefaultPartitionExistsSql = """
        SELECT EXISTS (
            SELECT 1 FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'audit.change_log'::regclass
              AND c.relname = 'change_log_default'
              AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT');
        """;

    private const string PartitionsNotOnUtcMidnightSql = """
        SELECT c.relname
        FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
        WHERE i.inhparent = 'audit.change_log'::regclass
          AND c.relname <> 'change_log_default'
          AND pg_get_expr(c.relpartbound, c.oid)
              !~ 'FROM \(''\d{4}-\d{2}-01 00:00:00\+00''\) TO \(''\d{4}-\d{2}-01 00:00:00\+00''\)';
        """;

    // A business schema is any schema outside the exclude-list, so a new domain schema is covered
    // automatically (fail-closed). A declared exclusion is held to the INVERSE by the next query.
    private const string UnauditedBusinessTablesSql = """
        SELECT t.table_schema || '.' || t.table_name
        FROM information_schema.tables t
        WHERE t.table_type = 'BASE TABLE'
          AND t.table_schema NOT IN ('audit', 'ref', 'jobs', 'public', 'information_schema')
          AND t.table_schema NOT LIKE 'pg_%'
          AND NOT EXISTS (
            SELECT 1 FROM audit.exclusions e
            WHERE e.schema_name = t.table_schema AND e.table_name = t.table_name)
          AND (
            (SELECT count(*) FROM information_schema.columns c
             WHERE c.table_schema = t.table_schema AND c.table_name = t.table_name
               AND c.column_name IN ('created_by_id', 'created_by_email', 'created_by_name', 'created_at',
                                     'updated_by_id', 'updated_by_email', 'updated_by_name', 'updated_at')) < 8
            OR EXISTS (
              SELECT 1 FROM information_schema.columns c
              WHERE c.table_schema = t.table_schema AND c.table_name = t.table_name
                AND c.column_name IN ('created_by_id', 'updated_by_id')
                AND c.data_type <> 'text')
            OR (SELECT count(DISTINCT m.col)
                FROM pg_constraint con
                CROSS JOIN LATERAL (SELECT unnest(ARRAY['created_by_id', 'updated_by_id']) AS col) m
                WHERE con.conrelid = (quote_ident(t.table_schema) || '.' || quote_ident(t.table_name))::regclass
                  AND con.contype = 'c'
                  AND pg_get_constraintdef(con.oid) ~ ('\mlength\(\s*' || m.col || '\s*\)\s*<=\s*64\M')) < 2
            OR NOT EXISTS (
              SELECT 1 FROM pg_trigger g
              JOIN pg_class r ON r.oid = g.tgrelid
              JOIN pg_namespace n ON n.oid = r.relnamespace
              WHERE n.nspname = t.table_schema AND r.relname = t.table_name
                AND g.tgname = 'zz_audit' AND NOT g.tgisinternal));
        """;

    private const string HalfAuditedExclusionsSql = """
        SELECT e.schema_name || '.' || e.table_name || ': ' ||
               CASE WHEN to_regclass(quote_ident(e.schema_name) || '.' || quote_ident(e.table_name)) IS NULL
                    THEN 'stale row -- the table no longer exists'
                    ELSE 'half-audited -- audit columns or a zz_audit* trigger remain' END
        FROM audit.exclusions e
        WHERE to_regclass(quote_ident(e.schema_name) || '.' || quote_ident(e.table_name)) IS NULL
           OR EXISTS (
             SELECT 1 FROM information_schema.columns c
             WHERE c.table_schema = e.schema_name AND c.table_name = e.table_name
               AND c.column_name IN ('created_by_id', 'created_by_email', 'created_by_name', 'created_at',
                                     'updated_by_id', 'updated_by_email', 'updated_by_name', 'updated_at'))
           OR EXISTS (
             SELECT 1 FROM pg_trigger g
             JOIN pg_class r ON r.oid = g.tgrelid
             JOIN pg_namespace n ON n.oid = r.relnamespace
             WHERE n.nspname = e.schema_name AND r.relname = e.table_name
               AND g.tgname LIKE 'zz_audit%' AND NOT g.tgisinternal);
        """;

    private readonly DatabaseFixture _database;

    public DatabaseSchemaTests(DatabaseFixture database)
    {
        _database = database;
    }

    [Fact]
    public void Apply_FixtureInitialized_EnablesUnderscoreMatching()
    {
        // Arrange / Act: the fixture's InitializeAsync is the act. This catches deletion of the
        // Apply() call; the next test catches the silent failure mode when it is late.

        // Assert
        Assert.True(DefaultTypeMap.MatchNamesWithUnderscores);
    }

    [Fact]
    public async Task Query_MultiWordColumnsIntoInitProperties_PopulatesEveryProperty()
    {
        // Arrange
        await using var connection = new NpgsqlConnection(_database.ConnectionString);

        // Act
        MappingProbe row = await connection.QuerySingleAsync<MappingProbe>(MultiWordColumnsSql);

        // Assert: a default here means the mapping was off when this (query, type) pair first ran —
        // Dapper does not throw on a column it cannot match, it skips it.
        Assert.Equal("mapping@example.test", row.CreatedByEmail);
        Assert.True(row.IsMapped);
    }

    [Fact]
    public void ApplyAsync_FreshDatabase_AppliesEveryMigrationCleanly()
    {
        // Arrange / Act: InitializeAsync applied the whole history from migrations/, in order, and
        // throws on the first script that fails — so a fixture with a connection string is the proof.

        // Assert
        Assert.False(string.IsNullOrEmpty(_database.ConnectionString));
    }

    [Fact]
    public async Task AuditPartitions_FreshDatabase_HaveAtLeastTwelveMonthsOfRunway()
    {
        // Arrange / Act: non-negotiable #28. R__audit_partition_runway.sql ran after every V####;
        // fewer than twelve future months means it is missing, misnamed, or no longer calls
        // fn_ensure_partition_runway. A missing partition fails every write in the database.
        long months = await _database.QueryScalarAsync<long>(FutureMonthlyPartitionCountSql);

        // Assert
        Assert.True(months >= 12, $"Only {months} future monthly audit partitions exist.");
    }

    [Fact]
    public async Task AuditPartitions_FreshDatabase_HaveDefaultPartitionAndUtcMidnightBounds()
    {
        // Arrange
        await using var connection = new NpgsqlConnection(_database.ConnectionString);

        // Act
        bool hasDefault = await connection.ExecuteScalarAsync<bool>(DefaultPartitionExistsSql);
        IEnumerable<string> offBounds = await connection.QueryAsync<string>(PartitionsNotOnUtcMidnightSql);

        // Assert: the DEFAULT partition is the circuit breaker; UTC midnight bounds are what the
        // SET timezone pin in fn_ensure_partition guarantees.
        Assert.True(hasDefault);
        Assert.Empty(offBounds);
    }

    [Fact]
    public async Task BusinessTables_AfterAllMigrations_AreAuditedOrDeclaredExcluded()
    {
        // Arrange
        await using var connection = new NpgsqlConnection(_database.ConnectionString);

        // Act
        IEnumerable<string> offenders = await connection.QueryAsync<string>(UnauditedBusinessTablesSql);

        // Assert: every business table is wired with audit.fn_enable_audit(...) — eight columns,
        // text actor ids capped at 64, the zz_audit trigger — or declared with
        // audit.fn_exclude_from_audit(...) and a reason (non-negotiable #14).
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task AuditExclusions_AfterAllMigrations_AreDeclaredCleanly()
    {
        // Arrange
        await using var connection = new NpgsqlConnection(_database.ConnectionString);

        // Act
        IEnumerable<string> offenders = await connection.QueryAsync<string>(HalfAuditedExclusionsSql);

        // Assert: a declared exclusion names a live table and carries none of the audit wiring.
        Assert.Empty(offenders);
    }

    // Init properties on purpose: a constructor-bound type throws on an unmatched column (loud),
    // an init-property type keeps its defaults (silent) — and the silent shape is the one Layer 0
    // exists to catch.
    private sealed record MappingProbe
    {
        public string? CreatedByEmail { get; init; }

        public bool IsMapped { get; init; }
    }
}
