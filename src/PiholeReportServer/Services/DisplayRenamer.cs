using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Applies the owner's display substitutions to a result.
/// <para>
/// Reverse DNS produces several spellings of one machine — <c>ALIEN01</c>,
/// <c>ALIEN01.bylotas.net</c>, <c>alien01.BYLOTAS.NET</c> — and a report that lists
/// all three reads as three devices. A rename collapses them to one label.
/// </para>
/// <para>
/// <b>Display only.</b> The substitution happens after the query has run, so the
/// numbers are exactly what the database returned and nothing is aggregated or
/// merged. Two rows that now show the same label stay two rows; renaming is not a
/// GROUP BY, and pretending otherwise would silently change totals.
/// </para>
/// </summary>
public static class DisplayRenamer
{
    /// <summary>
    /// A rename rule: every cell matching <paramref name="Match"/> is shown as
    /// <paramref name="Display"/>.
    /// </summary>
    public sealed record Rule(string Match, string Display);

    /// <summary>
    /// Builds the rules from the owner's memory. Longest match first, so a specific
    /// FQDN rule wins over a broad short-name one covering the same host.
    /// </summary>
    public static IReadOnlyList<Rule> RulesFrom(IReadOnlyList<AnalystFact> facts) =>
        facts
            .Where(f => f.Kind == "rename"
                        && !string.IsNullOrWhiteSpace(f.Target)
                        && !string.IsNullOrWhiteSpace(f.Subject))
            .Select(f => new Rule(f.Target!.Trim(), f.Subject.Trim()))
            .OrderByDescending(r => r.Match.Length)
            .ToList();

    /// <summary>
    /// Whether a cell value matches a rule.
    /// <para>
    /// Two deliberate behaviours, so the effect is predictable rather than magical:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// A pattern <b>containing a dot</b> matches the whole value only. Use the full
    /// name — <c>alien01.bylotas.net</c> — when precision matters.
    /// </item>
    /// <item>
    /// A pattern with <b>no dot</b> also matches any value whose first label equals
    /// it, so <c>ALIEN01</c> catches <c>ALIEN01.bylotas.net</c>. That is what "all
    /// variations of the hostname" means in practice. It will also catch
    /// <c>alien01.example.com</c>, which is the cost of the convenience — use the
    /// dotted form to avoid it.
    /// </item>
    /// </list>
    /// Always case-insensitive: reverse DNS is inconsistent about case, which is half
    /// the reason the variations exist at all.
    /// </summary>
    internal static bool Matches(string value, string pattern)
    {
        if (string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A dotted pattern is taken as exact, and has already been tested above.
        if (pattern.Contains('.'))
        {
            return false;
        }

        // Short name: match the first label of a dotted value.
        var dot = value.IndexOf('.');
        return dot > 0
               && string.Compare(value, 0, pattern, 0, dot, StringComparison.OrdinalIgnoreCase) == 0
               && dot == pattern.Length;
    }

    /// <summary>
    /// Returns the value to display for a cell, or the original when no rule applies.
    /// </summary>
    internal static string Apply(string value, IReadOnlyList<Rule> rules)
    {
        foreach (var rule in rules)
        {
            if (Matches(value, rule.Match))
            {
                return rule.Display;
            }
        }
        return value;
    }

    /// <summary>
    /// Rewrites the string cells of a result.
    /// <para>
    /// Applied once, before the result is cached, so searching, paging and CSV export
    /// all see the same labels. Doing it in the browser instead would mean a search
    /// for the friendly name matched nothing, because the server still held the raw
    /// value.
    /// </para>
    /// <para>
    /// Returns the same instance when nothing changed, so the common case of no rules
    /// costs one comparison rather than a full copy of a 100,000-row result.
    /// </para>
    /// </summary>
    public static QueryResult Rename(QueryResult result, IReadOnlyList<Rule> rules)
    {
        if (rules.Count == 0 || result.Rows.Count == 0)
        {
            return result;
        }

        var changed = false;
        var rows = new List<object?[]>(result.Rows.Count);

        foreach (var row in result.Rows)
        {
            object?[]? copy = null;
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is not string s)
                {
                    continue;
                }
                var replaced = Apply(s, rules);
                if (ReferenceEquals(replaced, s))
                {
                    continue;
                }
                // Copy lazily: most rows in most results match nothing.
                copy ??= (object?[])row.Clone();
                copy[i] = replaced;
                changed = true;
            }
            rows.Add(copy ?? row);
        }

        if (!changed)
        {
            return result;
        }

        return new QueryResult
        {
            Columns = result.Columns,
            Rows = rows,
            Truncated = result.Truncated,
            RowCap = result.RowCap,
            Elapsed = result.Elapsed,
        };
    }
}
