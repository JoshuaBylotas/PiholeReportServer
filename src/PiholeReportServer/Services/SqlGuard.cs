using System.Text;
using System.Text.RegularExpressions;

namespace PiholeReportServer.Services;

public sealed record SqlGuardResult(bool Allowed, string? Reason)
{
    public static readonly SqlGuardResult Ok = new(true, null);
    public static SqlGuardResult Deny(string reason) => new(false, reason);
}

/// <summary>
/// Static validation for user-supplied SQL in the query console.
/// <para>
/// This is defence in depth, not the primary control. The primary control is
/// that the application connects with a login holding nothing but
/// <c>db_datareader</c> on the report database, so a statement that slipped
/// past this check would still be refused by SQL Server. This class exists to
/// fail fast with a clear message and to keep obvious foot-guns out.
/// </para>
/// </summary>
public static partial class SqlGuard
{
    /// <summary>
    /// Constructs that are rejected outright. Matched as whole words against
    /// SQL that has had comments and string literals removed, so a domain name
    /// or a value containing one of these words does not trip the check.
    /// </summary>
    private static readonly string[] ForbiddenKeywords =
    [
        // mutation
        "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE", "BULK",
        // schema
        "CREATE", "ALTER", "DROP", "RENAME",
        // SELECT ... INTO materialises a table
        "INTO",
        // execution
        "EXEC", "EXECUTE", "RECONFIGURE", "SHUTDOWN", "KILL",
        // permissions
        "GRANT", "REVOKE", "DENY",
        // backup / restore
        "BACKUP", "RESTORE",
        // external data access
        "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "OPENXML",
        // transaction and session control
        "COMMIT", "ROLLBACK", "SAVE", "BEGIN", "SET", "USE",
        // denial of service
        "WAITFOR", "DELAY",
    ];

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"--[^\r\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"'(?:[^']|'')*'")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketIdentifier();

    /// <summary>
    /// Removes comments, string literals and bracketed identifiers, replacing
    /// each with a space so token boundaries survive. What remains is SQL
    /// structure only — safe to keyword-match against.
    /// </summary>
    internal static string Scrub(string sql)
    {
        var s = BlockComment().Replace(sql, " ");
        s = LineComment().Replace(s, " ");
        s = StringLiteral().Replace(s, " '' ");
        s = BracketIdentifier().Replace(s, " x ");
        return s;
    }

    public static SqlGuardResult Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlGuardResult.Deny("The query is empty.");
        }

        var scrubbed = Scrub(sql).Trim();

        if (scrubbed.Length == 0)
        {
            return SqlGuardResult.Deny("The query contains no executable SQL.");
        }

        // A single statement only. A trailing semicolon is tolerated.
        var withoutTrailing = scrubbed.TrimEnd().TrimEnd(';').TrimEnd();
        if (withoutTrailing.Contains(';', StringComparison.Ordinal))
        {
            return SqlGuardResult.Deny(
                "Only one statement may be run at a time. Remove the extra ';'.");
        }

        // Must be a read. WITH covers common table expressions.
        var firstToken = FirstWord(withoutTrailing);
        if (!firstToken.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
            && !firstToken.Equals("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return SqlGuardResult.Deny(
                $"Queries must begin with SELECT or WITH; this one begins with '{firstToken}'.");
        }

        foreach (var kw in ForbiddenKeywords)
        {
            if (ContainsWord(withoutTrailing, kw))
            {
                return SqlGuardResult.Deny(
                    $"'{kw}' is not permitted. The query console is read-only — " +
                    "use SELECT against the report views and tables.");
            }
        }

        // Extended and system stored procedures.
        if (Regex.IsMatch(withoutTrailing, @"\b(?:xp_|sp_|fn_trace)", RegexOptions.IgnoreCase))
        {
            return SqlGuardResult.Deny("System and extended stored procedures are not permitted.");
        }

        // Cross-database and linked-server references (4-part or 3-part names).
        if (Regex.IsMatch(withoutTrailing, @"\b\w+\s*\.\s*\w+\s*\.\s*\w+\s*\.\s*\w+\b"))
        {
            return SqlGuardResult.Deny(
                "Four-part (linked server) names are not permitted. Query the report database only.");
        }

        return SqlGuardResult.Ok;
    }

    private static string FirstWord(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.TrimStart('(', ' ', '\t', '\r', '\n'))
        {
            if (!char.IsLetter(c))
            {
                break;
            }
            sb.Append(c);
        }
        return sb.Length == 0 ? "(nothing)" : sb.ToString();
    }

    private static bool ContainsWord(string haystack, string word) =>
        Regex.IsMatch(haystack, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
}
