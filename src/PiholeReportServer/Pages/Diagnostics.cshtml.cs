using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Data;

namespace PiholeReportServer.Pages;

public sealed class DiagnosticsModel : PageModel
{
    private readonly ISqlConnectionFactory _factory;
    private readonly SqlOptions _sql;

    public DiagnosticsModel(ISqlConnectionFactory factory, IOptions<SqlOptions> sql)
    {
        _factory = factory;
        _sql = sql.Value;
    }

    public string Server => _sql.Server;
    public string Database => _sql.Database;
    public SqlAuthMode AuthMode => _sql.AuthMode;

    public string? SqlIdentity { get; private set; }
    public string? SqlError { get; private set; }

    /// <summary>Table name to row count, or null where the object is missing.</summary>
    public Dictionary<string, long?> Objects { get; } = new();

    public IEnumerable<Claim> InterestingClaims => User.Claims.Where(c =>
        c.Type is "name" or "preferred_username" or "oid" or "tid" or "roles"
            or ClaimTypes.Name or ClaimTypes.NameIdentifier or ClaimTypes.Role);

    private static readonly string[] Expected =
    [
        "dbo.PiholeQueries",
        "dbo.DimClient",
        "dbo.DimType",
        "dbo.DimStatus",
        "dbo.Adlists",
        "dbo.GravityDomains",
    ];

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            SqlIdentity = await _factory.DescribeIdentityAsync(ct);
        }
        catch (Exception ex)
        {
            SqlError = ex.Message;
            return;
        }

        try
        {
            await using var conn = await _factory.OpenAsync(ct);

            foreach (var name in Expected)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _sql.CommandTimeoutSeconds;

                // Row counts come from sys.dm_db_partition_stats rather than a
                // COUNT(*), so the page stays fast against a 23M-row table.
                cmd.CommandText = """
                    SELECT SUM(ps.row_count)
                    FROM sys.dm_db_partition_stats AS ps
                    WHERE ps.object_id = OBJECT_ID(@name)
                      AND ps.index_id IN (0, 1);
                    """;
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@name", name));

                var value = await cmd.ExecuteScalarAsync(ct);
                Objects[name] = value is null or DBNull ? null : Convert.ToInt64(value);
            }
        }
        catch (Exception ex)
        {
            SqlError = ex.Message;
        }
    }
}
