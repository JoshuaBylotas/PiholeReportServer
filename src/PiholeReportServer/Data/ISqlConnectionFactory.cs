using Microsoft.Data.SqlClient;

namespace PiholeReportServer.Data;

public interface ISqlConnectionFactory
{
    /// <summary>
    /// Opens a connection to the report database using the configured
    /// authentication mode. For the Entra on-behalf-of mode the connection
    /// carries the signed-in user's own token.
    /// </summary>
    Task<SqlConnection> OpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens a connection intended for writing. The only writable object is
    /// dbo.SavedReports; the warehouse itself is read-only to this login.
    /// <para>
    /// Separate from <see cref="OpenAsync"/> because the read path advertises
    /// ApplicationIntent=ReadOnly, which an availability-group listener uses to route
    /// to a readable secondary — where a write would fail.
    /// </para>
    /// </summary>
    Task<SqlConnection> OpenWriteAsync(CancellationToken ct = default);

    /// <summary>Describes the effective identity, for the diagnostics page.</summary>
    Task<string> DescribeIdentityAsync(CancellationToken ct = default);
}
