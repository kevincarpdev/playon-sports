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
/// A metric a caller can rank by. Kept as a closed set with an explicit SQL expression and
/// the sport it is meaningful for — this is what stops "points" being summed across sports.
/// </summary>
public sealed record Metric(string Key, string Label, string Expression, string? OnlyForSport)
{
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
