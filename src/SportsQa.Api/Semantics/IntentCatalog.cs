using SportsQa.Api.Contracts;
using SportsQa.Api.Routing;

namespace SportsQa.Api.Semantics;

/// <summary>
/// How we handle each intent the model can report. This is the policy table: what the intent
/// needs before it can run, what capability it consumes, and whether it is answerable at all.
///
/// Refusals live here rather than being inferred from a failed query, because the reason
/// matters. A nonexistent table is not a bug to retry — it is a permanent answer.
/// </summary>
public sealed record IntentPlan(
    string Intent,
    Capability RequiredCapability,
    IReadOnlyList<string> RequiredSlots,
    RefusalReason? Refusal = null,
    Metric? FixedMetric = null,
    string? FixedSport = null,
    /// <summary>
    /// The kind of entity this intent's certified template can actually match. A team template
    /// filters on <c>teams.school</c>, so a player name fills the slot and matches nothing.
    /// Declared here so the resolver rejects the wrong kind instead of the query returning NULL.
    /// </summary>
    string? EntityKind = null,
    /// <summary>
    /// Whether the model authors the SQL for this intent instead of <see cref="CertifiedQueries"/>.
    /// Set only where the recorded interpretation was executed and found correct, so trust is
    /// calibrated per route by evidence rather than applied uniformly. This governs *who writes
    /// the query*, not whether the question is answerable — that stays with <see cref="Refusal"/>.
    /// </summary>
    bool PreferModelSql = false)
{
    public bool IsRefused => Refusal is not null;
}

public static class IntentCatalog
{
    private const string Basketball = "Basketball";
    private const string Football = "Football";

    // Everything else keeps its certified template, because forensics showed the model's SQL
    // reads the stale rollup, cuts ties with LIMIT 1, invents a `touchdowns` column, or
    // silently guesses an entity.
    private static readonly Dictionary<string, IntentPlan> Plans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["count_teams"] = new("count_teams",
            Capability.ScalarQuery, [], PreferModelSql: true),

        ["schools_both_sports"] = new("schools_both_sports",
            Capability.AggregateQuery, []),

        ["top_scorer_basketball"] = new("top_scorer_basketball",
            Capability.RankedQuery, [], FixedMetric: Metric.Find("points"), FixedSport: Basketball,
            PreferModelSql: true),

        ["top5_scorers_basketball"] = new("top5_scorers_basketball",
            Capability.RankedQuery, [], FixedMetric: Metric.Find("points"), FixedSport: Basketball),

        ["max_rebounds_single_game"] = new("max_rebounds_single_game",
            Capability.RankedQuery, [], FixedSport: Basketball),

        ["highest_scoring_game"] = new("highest_scoring_game",
            Capability.RankedQuery, [], FixedSport: Football),

        ["roster_count"] = new("roster_count",
            Capability.ScalarQuery, [Slots.Entity, Slots.Sport],
            EntityKind: EntityKinds.School, PreferModelSql: true),

        ["team_wins"] = new("team_wins",
            Capability.AggregateQuery, [Slots.Entity, Slots.Sport],
            EntityKind: EntityKinds.School, PreferModelSql: true),

        ["player_passing_yards"] = new("player_passing_yards",
            Capability.AggregateQuery, [Slots.Entity],
            FixedMetric: Metric.Find("passing_yards"), FixedSport: Football,
            EntityKind: EntityKinds.Player),

        ["player_touchdowns"] = new("player_touchdowns",
            Capability.AggregateQuery, [Slots.Entity],
            FixedMetric: Metric.Find("touchdowns"), FixedSport: Football,
            EntityKind: EntityKinds.Player),

        ["player_total_points"] = new("player_total_points",
            Capability.AggregateQuery, [Slots.Entity, Slots.Sport],
            FixedMetric: Metric.Find("points"), EntityKind: EntityKinds.Player),

        ["player_avg_ppg"] = new("player_avg_ppg",
            Capability.AggregateQuery, [Slots.Entity, Slots.Sport],
            FixedMetric: Metric.Find("points_per_game"), EntityKind: EntityKinds.Player),

        // Sport is required: Riverside lost to Oak Hill in football and won twice in
        // basketball, so a single yes/no is wrong half the time.
        ["head_to_head"] = new("head_to_head",
            Capability.JoinAcrossGames, [Slots.SchoolA, Slots.SchoolB, Slots.Sport]),

        // "Jackson" is a school, a city and a player surname in this dataset. The template is
        // team-scoped, so only the school reading can be answered — the other two must clarify.
        ["entity_points"] = new("entity_points",
            Capability.AggregateQuery, [Slots.Entity, Slots.Sport],
            EntityKind: EntityKinds.School),

        // "Most points" is a choice, not a definition, and it mixes sports.
        ["best_player"] = new("best_player",
            Capability.RankedQuery, [Slots.Metric, Slots.Sport]),

        ["top_scorer_overall"] = new("top_scorer_overall",
            Capability.RankedQuery, [Slots.Sport], FixedMetric: Metric.Find("points")),

        ["team_injuries"] = new("team_injuries",
            Capability.AggregateQuery, [],
            new RefusalReason(
                "not_in_dataset",
                "This dataset has no injury data. There is no injuries table, and injury " +
                "status is not recorded anywhere else in the schema.",
                "An injuries table linking players or teams to reported injuries and dates.")),

        ["ops:rollup_freshness"] = new("ops:rollup_freshness",
            Capability.OpsIntents, []),
    };

    public static IntentPlan For(string intent) =>
        Plans.TryGetValue(intent, out var plan)
            ? plan
            : new IntentPlan(intent, Capability.ScalarQuery, [],
                new RefusalReason(
                    "unsupported_question",
                    "I can't answer that from this dataset with confidence.",
                    "This dataset covers Football 2025 and Basketball 2025-26 only: teams, " +
                    "rosters, game results and per-game player stat lines."));

    public static bool IsKnown(string intent) => Plans.ContainsKey(intent);

    /// <summary>Ops intents are namespaced so unprivileged callers cannot enumerate them.</summary>
    public static bool IsOpsIntent(string intent) =>
        intent.StartsWith("ops:", StringComparison.OrdinalIgnoreCase);
}
