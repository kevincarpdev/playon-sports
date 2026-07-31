using SportsQa.Api.Configuration;

namespace SportsQa.Api.Security;

/// <summary>
/// Subscription tiers, ordered by reach. This is authorization over what the *model* may
/// touch, not user login — identity arrives already established.
/// </summary>
public enum Role
{
    Anonymous,
    Member,
    Subscriber,
    Analyst,
    Admin,
}

public sealed record Principal(Role Role)
{
    public bool IsInternal => Role is Role.Analyst or Role.Admin;
}

/// <summary>
/// What a role is allowed to read. Table-level is the right granularity here: the sensitive
/// distinction in this dataset is aggregate team facts versus per-player and per-game detail.
/// </summary>
public sealed record RoleGrant(IReadOnlySet<string> Tables, int MaxRows, bool AllowOpsIntents)
{
    public bool Allows(string table) => Tables.Contains(table);
}

public static class RoleGrants
{
    private static readonly string[] PublicTables = ["teams", "games"];
    private static readonly string[] PlayerTables = ["players", "player_season_totals"];
    private const string GameStats = "player_game_stats";

    /// <summary>
    /// Row ceilings scale with tier. Kept relative to the configured maximum so a single
    /// config change moves every tier together.
    /// </summary>
    public static RoleGrant For(Role role, ExecutionOptions execution)
    {
        var max = execution.MaxRows;

        return role switch
        {
            Role.Anonymous => Grant(PublicTables, max / 4, ops: false),
            Role.Member => Grant([.. PublicTables, .. PlayerTables], max / 2, ops: false),
            Role.Subscriber => Grant([.. PublicTables, .. PlayerTables, GameStats], max, ops: false),
            Role.Analyst => Grant([.. PublicTables, .. PlayerTables, GameStats], max, ops: false),
            Role.Admin => Grant([.. PublicTables, .. PlayerTables, GameStats], max, ops: true),
            _ => Grant([], 0, ops: false),
        };
    }

    private static RoleGrant Grant(string[] tables, int maxRows, bool ops) =>
        new(new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase), maxRows, ops);
}

/// <summary>
/// Resolves the caller to a principal. A header stands in for real identity so the graded
/// path needs no auth setup; swapping in OIDC replaces this class alone.
/// </summary>
public sealed class PrincipalResolver(SportsQaOptions options)
{
    public Principal Resolve(IHeaderDictionary headers)
    {
        var claimed = headers[options.Authorization.RoleHeader].ToString();
        var fallback = options.Authorization.DefaultRole;

        return new Principal(Parse(claimed) ?? Parse(fallback) ?? Role.Anonymous);
    }

    private static Role? Parse(string? value) =>
        Enum.TryParse<Role>(value, ignoreCase: true, out var role) ? role : null;
}
