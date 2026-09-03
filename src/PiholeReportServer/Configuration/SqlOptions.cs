namespace PiholeReportServer.Configuration;

/// <summary>
/// How the application authenticates to SQL Server.
/// </summary>
public enum SqlAuthMode
{
    /// <summary>
    /// A dedicated read-only SQL login. Credentials come from user-secrets in
    /// development and from the environment / a key vault in production.
    /// Works against any SQL Server edition, including SQL Express.
    /// </summary>
    SqlLogin,

    /// <summary>
    /// Windows Integrated authentication as the app pool / service account.
    /// Requires the host to be domain-joined and the service account granted
    /// read access to the database.
    /// </summary>
    Integrated,

    /// <summary>
    /// Entra ID token for the *application's own* identity (managed identity or
    /// client credentials). Requires Azure SQL, SQL Managed Instance, or an
    /// Azure Arc-enabled SQL Server 2022+ with Entra authentication enabled.
    /// </summary>
    EntraApp,

    /// <summary>
    /// Entra ID token for the *signed-in user*, obtained on-behalf-of the web
    /// session, so SQL Server sees the real person and its own permissions and
    /// auditing apply per user. Same platform requirement as
    /// <see cref="EntraApp"/> — see docs/03-sql-server-setup.md.
    /// </summary>
    EntraOnBehalfOf,
}

public sealed class SqlOptions
{
    public const string SectionName = "Sql";

    /// <summary>Host name or host\instance of the SQL Server.</summary>
    public string Server { get; set; } = "";

    public int Port { get; set; } = 1433;

    public string Database { get; set; } = "pihole";

    public SqlAuthMode AuthMode { get; set; } = SqlAuthMode.SqlLogin;

    /// <summary>Only used when <see cref="AuthMode"/> is SqlLogin.</summary>
    public string? UserId { get; set; }

    /// <summary>Only used when <see cref="AuthMode"/> is SqlLogin.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Scope requested when acquiring a SQL access token. For Azure SQL this is
    /// https://database.windows.net//.default (note the double slash).
    /// </summary>
    public string TokenScope { get; set; } = "https://database.windows.net//.default";

    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// Set true only for a server presenting a self-signed certificate, which
    /// is common for an on-premises SQL Express instance.
    /// </summary>
    public bool TrustServerCertificate { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>Hard ceiling on how long any single report query may run.</summary>
    public int CommandTimeoutSeconds { get; set; } = 60;

    public string ApplicationName { get; set; } = "PiholeReportServer";
}
