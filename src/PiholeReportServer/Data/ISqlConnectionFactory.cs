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

    /// <summary>Describes the effective identity, for the diagnostics page.</summary>
    Task<string> DescribeIdentityAsync(CancellationToken ct = default);
}
