using System.Text.RegularExpressions;
using SportsQa.Api.Data;
using SportsQa.Api.Routing;

namespace SportsQa.Api.Sql;

public sealed record GuardResult(bool IsAllowed, string? Code = null, string? Detail = null)
{
    public static GuardResult Allow() => new(true);
    public static GuardResult Deny(string code, string detail) => new(false, code, detail);
}

/// <summary>
/// Static validation of model-authored SQL. Runs before the database is touched, so
/// rejecting a bad query costs nothing — which matters because with an untrusted author,
/// rejection is a common path rather than an exceptional one.
///
/// This is a allow-list validator, not a blocklist sanitiser. Anything it does not
/// positively recognise is refused.
/// </summary>
public sealed partial class SqlGuard(SchemaCatalog catalog)
{
    private static readonly string[] ForbiddenKeywords =
    [
        "insert", "update", "delete", "drop", "alter", "create", "replace", "truncate",
        "attach", "detach", "pragma", "vacuum", "reindex", "grant", "revoke",
    ];

    public GuardResult Validate(string sql, RoutingDecision routing)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return GuardResult.Deny("empty_sql", "The model produced no query.");
        }

        var normalized = StripStringLiterals(sql);

        if (UnbalancedQuotes(sql))
        {
            return GuardResult.Deny("malformed_sql", "Unbalanced quotes in the generated SQL.");
        }

        if (HasMultipleStatements(normalized))
        {
            return GuardResult.Deny("multiple_statements", "Only a single statement is permitted.");
        }

        if (!normalized.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)
            && !normalized.TrimStart().StartsWith("with", StringComparison.OrdinalIgnoreCase))
        {
            return GuardResult.Deny("not_a_select", "Only SELECT queries are permitted.");
        }

        var lowered = normalized.ToLowerInvariant();
        var forbidden = ForbiddenKeywords.FirstOrDefault(
            keyword => Regex.IsMatch(lowered, $@"\b{keyword}\b"));
        if (forbidden is not null)
        {
            return GuardResult.Deny("forbidden_keyword", $"'{forbidden}' is not permitted.");
        }

        return ValidateIdentifiers(normalized, routing);
    }

    /// <summary>
    /// Resolves every table and column mentioned against the live schema, then against the
    /// caller's grant. A hallucinated table and an unauthorised one fail here identically —
    /// one code path, so neither can be forgotten on some branch.
    /// </summary>
    private GuardResult ValidateIdentifiers(string sql, RoutingDecision routing)
    {
        foreach (var table in TablePattern().Matches(sql)
                     .Select(match => match.Groups["table"].Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.HasTable(table))
            {
                return GuardResult.Deny("unknown_table",
                    $"Table '{table}' does not exist in this dataset.");
            }

            if (!routing.AllowedTables.Contains(table))
            {
                return GuardResult.Deny("table_not_permitted",
                    $"Your access level does not include '{table}'.");
            }
        }

        foreach (var column in QualifiedColumnPattern().Matches(sql)
                     .Select(match => match.Groups["column"].Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!catalog.HasColumn(column))
            {
                return GuardResult.Deny("unknown_column",
                    $"Column '{column}' does not exist in this dataset.");
            }
        }

        return GuardResult.Allow();
    }

    /// <summary>Appends a row ceiling when the query does not already constrain itself.</summary>
    public static string EnforceRowLimit(string sql, int maxRows)
    {
        var trimmed = sql.TrimEnd().TrimEnd(';');
        return LimitPattern().IsMatch(trimmed)
            ? trimmed
            : $"{trimmed} LIMIT {maxRows}";
    }

    private static bool UnbalancedQuotes(string sql) =>
        sql.Count(character => character == '\'') % 2 != 0;

    private static bool HasMultipleStatements(string sql) =>
        sql.TrimEnd().TrimEnd(';').Contains(';');

    /// <summary>
    /// Removes quoted literals so a school name like 'Oak Hill' is never mistaken for an
    /// identifier, and a literal containing a keyword cannot trip the blocklist.
    /// </summary>
    private static string StripStringLiterals(string sql) =>
        StringLiteralPattern().Replace(sql, " '' ");

    [GeneratedRegex(@"'[^']*'", RegexOptions.Compiled)]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"\b(?:from|join)\s+(?<table>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TablePattern();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\.(?<column>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex QualifiedColumnPattern();

    [GeneratedRegex(@"\blimit\s+\d+\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LimitPattern();
}
