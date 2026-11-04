// The unit of work handed out by IWriterConnectionFactory. Exposes the abstract
// System.Data.Common types so Common/Ports/ carries no database driver; the
// Infrastructure adapter backs them with Npgsql.
//
// No `using System.Data.Common;` here: copy-assets.ps1 puts it in the project's GlobalUsings.cs,
// and the build-enforced coding standard makes a duplicate a red build (IDE0005).

namespace RouteGo.Fleet.Api.Common.Ports;

/// <summary>
/// A connection with an open transaction that commits on <see cref="CommitAsync"/>
/// and rolls back on dispose if the caller never committed.
/// </summary>
public interface IWriterConnection : IAsyncDisposable
{
    /// <summary>The open connection; run Dapper calls against it.</summary>
    DbConnection Connection { get; }

    /// <summary>The live transaction; pass it to every command on this connection.</summary>
    DbTransaction Transaction { get; }

    /// <summary>Commits the transaction. Without this call, dispose rolls back.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-stamp app.action partway through the transaction. The audit trigger reads it
    /// at each row write, so rows written after this call carry the new action label
    /// while earlier rows keep the previous one. Use only when one transaction performs
    /// several distinct business actions; most writes set the action once via
    /// <see cref="IWriterConnectionFactory.OpenWriteTransactionAsync"/>.
    /// </summary>
    Task SetActionAsync(string action, CancellationToken cancellationToken = default);
}
