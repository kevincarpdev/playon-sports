namespace SportsQa.Api.Semantics;

/// <summary>
/// The facts an intent needs before it can be answered. Named constants rather than loose
/// strings so the catalog and the resolver cannot drift apart.
/// </summary>
public static class Slots
{
    public const string Sport = "sport";
    public const string Metric = "metric";
    public const string Entity = "entity";
    public const string SchoolA = "school_a";
    public const string SchoolB = "school_b";
}

/// <summary>
/// Per-game columns capped upstream (SEMANTIC_MODEL.md §6.8). Superlatives that tie on these
/// are not answerable as single winners — the ceiling is shared by design.
/// </summary>
public static class ClippedStats
{
    public static readonly HashSet<string> Columns = new(StringComparer.OrdinalIgnoreCase)
    {
        "rebounds",
        "assists",
    };

    public static bool Contains(string? column) =>
        column is not null && Columns.Contains(column);
}

/// <summary>
/// The kinds of thing the lexicon holds. An intent declares which kind its entity slot needs,
/// because "is this a name in the data" is a weaker check than "is this the kind of name this
/// query can match" — a player name satisfies the former and returns nothing from a
/// school-scoped template.
/// </summary>
public static class EntityKinds
{
    public const string School = "school";
    public const string Player = "player";
    public const string City = "city";
}

/// <summary>
/// Which end of a metric's range is the good end.
///
/// Every metric in this dataset is higher-is-better, so this looks redundant here. It is not:
/// the moment track and field arrives, every running event is a time, and ranking those
/// descending silently returns the *slowest* athlete as the leader. That bug looks entirely
/// plausible in a result set, which is exactly the kind we cannot ship.
/// </summary>
public enum MetricDirection
{
    HigherIsBetter,
    LowerIsBetter,
}

/// <summary>
/// A metric a caller can rank by. A closed set, each with an explicit SQL expression, the
/// sport it is meaningful for, and its direction — the three things a ranking needs and that a
/// model should never be trusted to infer.
///
/// Restricting by sport is what structurally prevents summing football and basketball points
/// together; see SEMANTIC_MODEL.md §6.1.
/// </summary>
public sealed record Metric(
    string Key,
    string Label,
    string Expression,
    string? OnlyForSport,
    MetricDirection Direction = MetricDirection.HigherIsBetter)
{
    public string SqlDirection => Direction == MetricDirection.HigherIsBetter ? "DESC" : "ASC";

    public static readonly IReadOnlyList<Metric> All =
    [
        new("points", "Most total points", "SUM(s.points)", null),
        new("points_per_game", "Best points per game",
            "SUM(s.points) * 1.0 / COUNT(DISTINCT s.game_id)", null),
        new("rebounds", "Most rebounds", "SUM(s.rebounds)", "Basketball"),
        new("assists", "Most assists", "SUM(s.assists)", "Basketball"),
        new("touchdowns", "Most touchdowns", "SUM(s.td)", "Football"),
        new("passing_yards", "Most passing yards", "SUM(s.pass_yds)", "Football"),
    ];

    public static Metric? Find(string key) =>
        All.FirstOrDefault(metric => metric.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
