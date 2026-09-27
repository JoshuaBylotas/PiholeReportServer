namespace PiholeReportServer.Configuration;

/// <summary>
/// Named authorization policies. Every page in the app requires at least
/// <see cref="Viewer"/>; the raw-SQL console additionally requires
/// <see cref="SqlAuthor"/>, and the device editor <see cref="DeviceEditor"/>.
/// </summary>
public static class AuthorizationPolicies
{
    public const string Viewer = "Viewer";
    public const string SqlAuthor = "SqlAuthor";
    public const string DeviceEditor = "DeviceEditor";
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

    /// <summary>
    /// May state device names in the editor and push them to DNS.
    /// <para>
    /// Separate from <see cref="SqlAuthor"/> because it is a different kind of
    /// power. SqlAuthor reads anything and changes nothing; this role changes
    /// what the network's name resolution says, which is felt by every device
    /// on it. Read access should not imply it.
    /// </para>
    /// </summary>
    public const string DeviceEditor = "Report.DeviceEditor";
}
