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
/// Static validation of SQL before the database is touched, so rejecting a bad query costs
/// nothing — which matters when the author is untrusted and rejection is a common path.
///
/// This is a **conservative prefilter, not a SQL parser.** Regex cannot reason about quoting,
/// comments-as-whitespace, CTE scope or compound selects, so this does not pretend to. It
/// fails closed instead: anything whose shape it cannot fully account for is denied, and the
/// syntax that makes bypasses possible is refused outright rather than analysed.
///
/// An adversarial review found six bypasses in an earlier version that analysed raw text and
/// matched only bare identifiers: quoted table names, comment-separated tokens, a UNION leg, a
/// CTE shadowing a permitted table, pragma_table_info for schema recon, and a recursive CTE
/// that ran 31s against a 5s configured timeout. The three structural rules here — strip
/// comments first, refuse quoted identifiers, refuse CTEs — close all six.
/// </summary>
public sealed partial class SqlGuard(SchemaCatalog catalog)
{
    private static readonly string[] ForbiddenKeywords =
    [
        "insert", "update", "delete", "drop", "alter", "create", "replace", "truncate",
        "attach", "detach", "pragma", "vacuum", "reindex", "grant", "revoke", "recursive",
    ];

    public GuardResult Validate(string sql, RoutingDecision routing)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return GuardResult.Deny("empty_sql", "The model produced no query.");
        }

        if (UnbalancedQuotes(sql))
        {
            return GuardResult.Deny("malformed_sql", "Unbalanced quotes in the generated SQL.");
        }

        // Comments are whitespace to SQLite but not to a regex, so they must go before any
        // structural analysis — otherwise FROM/**/players hides a table reference entirely.
        var normalized = StripStringLiterals(StripComments(sql));

        if (HasMultipleStatements(normalized))
        {
            return GuardResult.Deny("multiple_statements", "Only a single statement is permitted.");
        }

        if (!normalized.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
        {
            // Refusing WITH costs nothing — no certified template uses a CTE — and removes CTE
            // shadowing and recursive-CTE resource exhaustion as entire categories.
            return GuardResult.Deny("not_a_select",
                "Only a single SELECT is permitted. Common table expressions are not accepted; "
                + "use a subquery instead.");
        }

        // Quoted identifiers exist here only to smuggle names past identifier analysis. Nothing
        // we generate needs them, so they are refused rather than parsed.
        if (QuotedIdentifierPattern().IsMatch(normalized))
        {
            return GuardResult.Deny("quoted_identifier",
                "Quoted or bracketed identifiers are not permitted.");
        }

        var lowered = normalized.ToLowerInvariant();

        // Prefix match, not whole word: \bpragma\b misses pragma_table_info, a table-valued
        // function that reads the schema of tables the caller may not query.
        var forbidden = ForbiddenKeywords.FirstOrDefault(
            keyword => Regex.IsMatch(lowered, $@"\b{keyword}"));
        if (forbidden is not null)
        {
            return GuardResult.Deny("forbidden_keyword", $"'{forbidden}' is not permitted.");
        }

        return ValidateIdentifiers(normalized, routing);
    }

    /// <summary>
    /// Every FROM/JOIN target must resolve to a table the caller may read, or be the opening
    /// parenthesis of a subquery whose own targets this same pass checks. A target that cannot
    /// be classified is denied — the pass never shrugs and validates only what it recognised.
    /// </summary>
    private GuardResult ValidateIdentifiers(string sql, RoutingDecision routing)
    {
        var sources = SourcePattern().Matches(sql);

        // A FROM/JOIN the source pattern could not account for means the query has a shape we do
        // not model. Fail closed rather than approving the parts that happened to parse.
        if (sources.Count != SourceKeywordPattern().Matches(sql).Count)
        {
            return GuardResult.Deny("unparsable_source",
                "A FROM or JOIN target could not be identified.");
        }

        foreach (Match match in sources)
        {
            if (match.Groups["sub"].Success)
            {
                continue;
            }

            var table = match.Groups["table"].Value;

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
            // Known limitation: this proves the column exists somewhere in the schema, not that
            // it belongs to the table its alias refers to. Resolving aliases needs a parser.
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

    private static string StripComments(string sql) =>
        BlockCommentPattern().Replace(LineCommentPattern().Replace(sql, " "), " ");

    /// <summary>
    /// Removes quoted literals so a school name like 'Oak Hill' is never read as an identifier,
    /// and a literal containing a keyword cannot trip the blocklist.
    /// </summary>
    private static string StripStringLiterals(string sql) =>
        StringLiteralPattern().Replace(sql, " '' ");

    [GeneratedRegex(@"'[^']*'", RegexOptions.Compiled)]
    private static partial Regex StringLiteralPattern();

    [GeneratedRegex(@"--[^\n\r]*", RegexOptions.Compiled)]
    private static partial Regex LineCommentPattern();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex BlockCommentPattern();

    [GeneratedRegex(@"[""`\[\]]", RegexOptions.Compiled)]
    private static partial Regex QuotedIdentifierPattern();

    /// <summary>Every FROM/JOIN, used to confirm the source pattern accounted for all of them.</summary>
    [GeneratedRegex(@"\b(?:from|join)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SourceKeywordPattern();

    /// <summary>A FROM/JOIN target: a subquery's opening paren, or a bare identifier.</summary>
    [GeneratedRegex(@"\b(?:from|join)\s*(?:(?<sub>\()|(?<table>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex SourcePattern();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\.(?<column>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex QualifiedColumnPattern();

    /// <summary>A trailing LIMIT, with or without a following OFFSET.</summary>
    [GeneratedRegex(@"\blimit\s+\d+\s*(?:offset\s+\d+\s*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex LimitPattern();
}
