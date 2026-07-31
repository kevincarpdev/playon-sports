using SportsQa.Api.Data;

namespace SportsQa.Api.Semantics;

public sealed record CertifiedQuery(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    string Scope,
    /// <summary>
    /// The column a superlative ranks on, when there is one. Named explicitly so tie
    /// detection does not have to guess which column carries the answer.
    /// </summary>
    string? RankedValueColumn = null);

/// <summary>
/// Reviewed SQL owned by the semantic layer, one template per known intent.
///
/// The model's job is to classify intent and surface entities — not to author SQL. Where we
/// recognise the intent we run our own query, which is how the sharp edges in
/// SEMANTIC_MODEL.md get handled once, correctly, instead of hoping the model remembers:
/// season scoping is always applied, `td` is never called `touchdowns`, the stale rollup is
/// bypassed for the fact table, superlatives rank instead of `LIMIT 1`, and head-to-head
/// matches both home and away orientations.
///
/// Caller-supplied values arrive as parameters and are never interpolated. The only
/// interpolated fragments are `Metric.Expression` and fixed column names, both drawn from
/// closed sets declared in Slots.cs. That invariant lives in a comment rather than a type,
/// which FINDINGS.md §5 records as a known limitation.
/// </summary>
public sealed class CertifiedQueries(DatasetFacts facts)
{
    private const string StatJoins = """
        FROM player_game_stats s
        JOIN games   g ON g.game_id   = s.game_id
        JOIN players p ON p.player_id = s.player_id
        """;

    private const string PlayerName = "(p.first_name || ' ' || p.last_name)";

    public CertifiedQuery CountTeams() => new(
        "SELECT COUNT(*) AS team_count FROM teams",
        Empty,
        "All teams, counted one row per school per sport.");

    public CertifiedQuery SchoolsInBothSports()
    {
        var placeholders = string.Join(", ", facts.Sports.Select((_, index) => $"$sport{index}"));
        var parameters = facts.Sports
            .Select((sport, index) => KeyValuePair.Create($"$sport{index}", (object?)sport))
            .ToDictionary();
        parameters["$sportCount"] = facts.Sports.Count;

        return new CertifiedQuery(
            $"""
             SELECT school FROM teams
             WHERE sport IN ({placeholders})
             GROUP BY school
             HAVING COUNT(DISTINCT sport) = $sportCount
             ORDER BY school
             """,
            parameters,
            $"Schools fielding a team in all of: {string.Join(", ", facts.Sports)}.");
    }

    /// <summary>
    /// Ranked leaders, returning the whole tied set rather than an arbitrary row. Ordering
    /// follows the metric's declared direction, not an assumption that bigger wins — see
    /// PRODUCTION_NOTES.md §3.1 for why that assumption breaks on track and field times.
    /// </summary>
    public CertifiedQuery TopByMetric(string sport, Metric metric, int topN)
    {
        var season = facts.SeasonFor(sport);
        var order = metric.SqlDirection;

        return new CertifiedQuery(
            $"""
             SELECT player, {metric.Key} AS value, games_played FROM (
               SELECT {PlayerName} AS player,
                      {metric.Expression} AS {metric.Key},
                      COUNT(DISTINCT s.game_id) AS games_played,
                      DENSE_RANK() OVER (ORDER BY {metric.Expression} {order}) AS rnk,
                      s.player_id
               {StatJoins}
               WHERE g.sport = $sport AND g.season = $season
               GROUP BY s.player_id
             )
             WHERE rnk <= $topN
             ORDER BY value {order}, player_id
             """,
            new Dictionary<string, object?>
            {
                ["$sport"] = sport,
                ["$season"] = season,
                ["$topN"] = topN,
            },
            $"{metric.Label}, {sport} {season}. Schedules differ, so totals are not directly comparable.",
            RankedValueColumn: "value");
    }

    /// <summary>
    /// Single-game maximum, returning every row at the top rank. Ties are not an edge case
    /// here: 52 stat lines across 34 players share the rebound high, because the column is
    /// clipped at 12 (SEMANTIC_MODEL.md §6.8).
    /// </summary>
    public CertifiedQuery SingleGameMax(string sport, string column, string label)
    {
        var season = facts.SeasonFor(sport);

        return new CertifiedQuery(
            $"""
             SELECT player, value, game_date FROM (
               SELECT {PlayerName} AS player, s.{column} AS value, g.game_date,
                      DENSE_RANK() OVER (ORDER BY s.{column} DESC) AS rnk, s.player_id
               {StatJoins}
               WHERE g.sport = $sport AND g.season = $season AND s.{column} IS NOT NULL
             )
             WHERE rnk = 1
             ORDER BY player_id
             """,
            new Dictionary<string, object?> { ["$sport"] = sport, ["$season"] = season },
            $"Highest single-game {label}, {sport} {season}.",
            RankedValueColumn: "value");
    }

    public CertifiedQuery HighestScoringGame(string sport)
    {
        var season = facts.SeasonFor(sport);

        return new CertifiedQuery(
            """
            SELECT game_date, home, home_score, away, away_score, total_points FROM (
              SELECT g.game_date, h.school AS home, g.home_score,
                     a.school AS away, g.away_score,
                     g.home_score + g.away_score AS total_points,
                     DENSE_RANK() OVER (ORDER BY g.home_score + g.away_score DESC) AS rnk,
                     g.game_id
              FROM games g
              JOIN teams h ON h.team_id = g.home_team_id
              JOIN teams a ON a.team_id = g.away_team_id
              WHERE g.sport = $sport AND g.season = $season
            )
            WHERE rnk = 1
            ORDER BY game_id
            """,
            new Dictionary<string, object?> { ["$sport"] = sport, ["$season"] = season },
            $"Highest combined score, {sport} {season}.",
            RankedValueColumn: "total_points");
    }

    /// <summary>Player season metric computed from the fact table, never the stale rollup.</summary>
    public CertifiedQuery PlayerMetric(string playerName, string sport, Metric metric) => new(
        $"""
         SELECT {metric.Expression} AS {metric.Key},
                COUNT(DISTINCT s.game_id) AS games_played
         {StatJoins}
         WHERE {PlayerName} = $player AND g.sport = $sport AND g.season = $season
         """,
        new Dictionary<string, object?>
        {
            ["$player"] = playerName,
            ["$sport"] = sport,
            ["$season"] = facts.SeasonFor(sport),
        },
        $"{metric.Label} for {playerName}, computed from per-game stat lines ({sport}).");

    public CertifiedQuery RosterCount(string school, string sport) => new(
        """
        SELECT COUNT(*) AS player_count
        FROM players p
        JOIN teams t ON t.team_id = p.team_id
        WHERE t.school = $school AND t.sport = $sport
        """,
        new Dictionary<string, object?> { ["$school"] = school, ["$sport"] = sport },
        $"Roster size for {school} {sport}. Counts all rostered players, including those with no stat lines.");

    /// <summary>
    /// Wins by score comparison. Four basketball games in this dataset are drawn (FINDINGS.md
    /// §1.7), and strict `>` excludes them rather than miscounting them, so the count is right
    /// while the record is incomplete: draws are silently absent from any win/loss accounting.
    /// PRODUCTION_NOTES.md §2.3 explains why real data needs an explicit result column, since
    /// forfeits, vacated wins and legal ties all make "scored more" the wrong definition.
    /// </summary>
    public CertifiedQuery TeamWins(string school, string sport)
    {
        var season = facts.SeasonFor(sport);

        return new CertifiedQuery(
            """
            SELECT COUNT(*) AS wins
            FROM games g
            JOIN teams t ON t.team_id IN (g.home_team_id, g.away_team_id)
            WHERE t.school = $school AND t.sport = $sport
              AND g.sport = $sport AND g.season = $season
              AND (CASE WHEN g.home_team_id = t.team_id THEN g.home_score ELSE g.away_score END)
                > (CASE WHEN g.home_team_id = t.team_id THEN g.away_score ELSE g.home_score END)
            """,
            new Dictionary<string, object?>
            {
                ["$school"] = school,
                ["$sport"] = sport,
                ["$season"] = season,
            },
            $"Wins for {school} {sport} {season}.");
    }

    public CertifiedQuery TeamPointsFor(string school, string sport)
    {
        var season = facts.SeasonFor(sport);

        return new CertifiedQuery(
            """
            SELECT SUM(CASE WHEN g.home_team_id = t.team_id THEN g.home_score ELSE g.away_score END)
                     AS points_for,
                   COUNT(*) AS games_played
            FROM games g
            JOIN teams t ON t.team_id IN (g.home_team_id, g.away_team_id)
            WHERE t.school = $school AND t.sport = $sport
              AND g.sport = $sport AND g.season = $season
            """,
            new Dictionary<string, object?>
            {
                ["$school"] = school,
                ["$sport"] = sport,
                ["$season"] = season,
            },
            $"Points scored by {school} {sport} {season}.");
    }

    /// <summary>
    /// Head-to-head across both orientations — a one-sided join misses half the fixtures.
    ///
    /// `winner` is NULL on a draw rather than defaulting to the away side. Four basketball games
    /// in this dataset are tied, so a two-branch CASE would silently name a loser as the winner.
    /// </summary>
    public CertifiedQuery HeadToHead(string schoolA, string schoolB, string sport)
    {
        var season = facts.SeasonFor(sport);

        return new CertifiedQuery(
            """
            SELECT g.game_date, h.school AS home, g.home_score,
                   a.school AS away, g.away_score,
                   CASE
                     WHEN g.home_score > g.away_score THEN h.school
                     WHEN g.away_score > g.home_score THEN a.school
                     ELSE NULL
                   END AS winner
            FROM games g
            JOIN teams h ON h.team_id = g.home_team_id
            JOIN teams a ON a.team_id = g.away_team_id
            WHERE ((h.school = $a AND a.school = $b) OR (h.school = $b AND a.school = $a))
              AND g.sport = $sport AND g.season = $season
            ORDER BY g.game_date
            """,
            new Dictionary<string, object?>
            {
                ["$a"] = schoolA,
                ["$b"] = schoolB,
                ["$sport"] = sport,
                ["$season"] = season,
            },
            $"All {sport} {season} meetings between {schoolA} and {schoolB}, both home and away.");
    }

    /// <summary>
    /// Rollup rows the nightly job has left behind. Operational, not fan-facing.
    ///
    /// This is the seed of a real freshness SLO. MaxPreps statistics are coach-entered, arrive
    /// late and get corrected after publication, so freshness has to be monitored per sport,
    /// season and state rather than checked once. See PRODUCTION_NOTES.md §2.4.
    /// </summary>
    public CertifiedQuery StaleRollupRows() => new(
        """
        SELECT p.first_name || ' ' || p.last_name AS player, pst.sport, pst.season,
               pst.points AS rollup_points, pst.games_played AS rollup_games,
               pst.updated_at, lg.last_game
        FROM player_season_totals pst
        JOIN players p ON p.player_id = pst.player_id
        JOIN (SELECT sport, season, MAX(game_date) AS last_game FROM games GROUP BY sport, season) lg
          ON lg.sport = pst.sport AND lg.season = pst.season
        WHERE date(pst.updated_at) < date(lg.last_game)
        ORDER BY pst.updated_at
        """,
        Empty,
        "Rollup rows older than the last game of their season.");

    private static IReadOnlyDictionary<string, object?> Empty =>
        new Dictionary<string, object?>();
}
