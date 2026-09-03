namespace PiholeReportServer.Configuration;

/// <summary>
/// Named authorization policies. Every page in the app requires at least
/// <see cref="Viewer"/>; the raw-SQL console additionally requires
/// <see cref="SqlAuthor"/>.
/// </summary>
public static class AuthorizationPolicies
{
    public const string Viewer = "Viewer";
    public const string SqlAuthor = "SqlAuthor";
}

/// <summary>
/// App roles as declared in the Entra ID app registration manifest.
/// See docs/02-entra-id-setup.md.
/// </summary>
public static class AppRoles
{
    /// <summary>May sign in, browse and run pre-canned reports and the builder.</summary>
    public const string Viewer = "Report.Viewer";

    /// <summary>May additionally run free-text SQL in the query console.</summary>
    public const string SqlAuthor = "Report.SqlAuthor";
}
