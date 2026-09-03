using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using PiholeReportServer.Configuration;

namespace PiholeReportServer.Data;

/// <summary>
/// Builds report-database connections. The connection string never contains a
/// credential in the Entra modes: instead an access token is attached to the
/// connection, so SQL Server authenticates the Entra principal directly.
/// </summary>
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly SqlOptions _opt;
    private readonly ITokenAcquisition _tokens;
    private readonly ILogger<SqlConnectionFactory> _log;

    public SqlConnectionFactory(
        IOptions<SqlOptions> opt,
        ITokenAcquisition tokens,
        ILogger<SqlConnectionFactory> log)
    {
        _opt = opt.Value;
        _tokens = tokens;
        _log = log;
    }

    private string BuildConnectionString()
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = _opt.Port == 1433 ? _opt.Server : $"{_opt.Server},{_opt.Port}",
            InitialCatalog = _opt.Database,
            Encrypt = _opt.Encrypt,
            TrustServerCertificate = _opt.TrustServerCertificate,
            ConnectTimeout = _opt.ConnectTimeoutSeconds,
            ApplicationName = _opt.ApplicationName,
            // Reporting is strictly read-only; advertise that so a future
            // Always On secondary can serve the load.
            ApplicationIntent = ApplicationIntent.ReadOnly,
            MultipleActiveResultSets = false,
        };

        switch (_opt.AuthMode)
        {
            case SqlAuthMode.SqlLogin:
                if (string.IsNullOrWhiteSpace(_opt.UserId) || string.IsNullOrWhiteSpace(_opt.Password))
                {
                    throw new InvalidOperationException(
                        "Sql:AuthMode is SqlLogin but Sql:UserId / Sql:Password are not configured. " +
                        "Set them with 'dotnet user-secrets' in development, or as environment " +
                        "variables Sql__UserId / Sql__Password in production. " +
                        "See docs/03-sql-server-setup.md.");
                }
                b.UserID = _opt.UserId;
                b.Password = _opt.Password;
                break;

            case SqlAuthMode.Integrated:
                b.IntegratedSecurity = true;
                break;

            case SqlAuthMode.EntraApp:
            case SqlAuthMode.EntraOnBehalfOf:
                // No credential in the string — a token is attached in OpenAsync.
                break;

            default:
                throw new InvalidOperationException($"Unsupported Sql:AuthMode '{_opt.AuthMode}'.");
        }

        return b.ConnectionString;
    }

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqlConnection(BuildConnectionString());

        if (_opt.AuthMode is SqlAuthMode.EntraApp or SqlAuthMode.EntraOnBehalfOf)
        {
            conn.AccessToken = await AcquireSqlTokenAsync();
        }

        try
        {
            await conn.OpenAsync(ct);
        }
        catch (SqlException ex) when (
            _opt.AuthMode is SqlAuthMode.EntraApp or SqlAuthMode.EntraOnBehalfOf
            && (ex.Number is 18456 or 18452 or 0))
        {
            await conn.DisposeAsync();
            throw new InvalidOperationException(
                $"SQL Server rejected the Entra ID token (error {ex.Number}). Entra ID authentication " +
                "requires Azure SQL Database, SQL Managed Instance, or an Azure Arc-enabled " +
                "SQL Server 2022+ with Entra authentication enabled. A stand-alone or SQL Express " +
                "instance cannot accept Entra tokens — use Sql:AuthMode=SqlLogin or Integrated " +
                "instead. See docs/03-sql-server-setup.md.", ex);
        }

        return conn;
    }

    private async Task<string> AcquireSqlTokenAsync()
    {
        var scopes = new[] { _opt.TokenScope };

        if (_opt.AuthMode == SqlAuthMode.EntraOnBehalfOf)
        {
            // Exchanges the signed-in user's session for a SQL-scoped token, so
            // the database sees the actual person behind the request.
            return await _tokens.GetAccessTokenForUserAsync(scopes);
        }

        return await _tokens.GetAccessTokenForAppAsync(_opt.TokenScope);
    }

    public async Task<string> DescribeIdentityAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SUSER_SNAME(), USER_NAME(), @@VERSION";
        cmd.CommandTimeout = _opt.CommandTimeoutSeconds;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
        {
            return "unknown";
        }

        var version = r.GetString(2).Split('\n')[0].Trim();
        return $"login={r.GetString(0)}; user={r.GetString(1)}; mode={_opt.AuthMode}; server={version}";
    }
}
