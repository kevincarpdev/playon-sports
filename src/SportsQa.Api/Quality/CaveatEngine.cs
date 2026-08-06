using SportsQa.Api.Contracts;
using SportsQa.Api.Semantics;
using SportsQa.Api.Sql;

namespace SportsQa.Api.Quality;

/// <summary>
/// Attaches the data-quality warnings a caller needs to read a number correctly. These are
/// our rules applied to our results — the model's own prose never reaches the caller.
///
/// Each caveat corresponds to a documented sharp edge in SEMANTIC_MODEL.md.
/// </summary>
/// <summary>Caveat codes, named so the pipeline can price them without matching strings.</summary>
public static class CaveatCodes
{
    public const string NoMatchingRows = "no_matching_rows";
    public const string StatNotApplicable = "stat_not_applicable";
    public const string TiedResult = "tied_result";
    public const string Truncated = "truncated";
    public const string UnevenSchedules = "uneven_schedules";
    public const string SportScoped = "sport_scoped";
}

public sealed class CaveatEngine
{
    private static readonly string[] SportScopedIntents =
        ["top_scorer_overall", "best_player", "entity_points", "top_scorer_basketball"];

    public IReadOnlyList<Caveat> Evaluate(
        IntentPlan plan,
        IReadOnlyDictionary<string, string> slots,
        ResultSet result,
        string? rankedColumn)
    {
        var caveats = new List<Caveat>();

        if (result.IsEmpty || HasNullValue(result))
        {
            caveats.Add(NoDataCaveat(result));
        }

        if (IsTiedAtTop(result, rankedColumn))
        {
            caveats.Add(new Caveat(CaveatCodes.TiedResult,
                $"{result.Rows.Count} rows share the top value. All are returned; there is no " +
                "single answer to this question."));
        }

        if (result.Truncated)
        {
            caveats.Add(new Caveat(CaveatCodes.Truncated,
                "Results were truncated at the configured row ceiling."));
        }

        if (ComparesAcrossTeams(result))
        {
            caveats.Add(new Caveat(CaveatCodes.UnevenSchedules,
                "Teams played between 8 and 14 games depending on sport and school, so season " +
                "totals are not directly comparable across teams."));
        }

        if (SportScopedIntents.Contains(plan.Intent) && slots.TryGetValue(Slots.Sport, out var sport))
        {
            caveats.Add(new Caveat(CaveatCodes.SportScoped,
                $"Scoped to {sport} only. Points are not comparable across sports, so a " +
                "combined figure would be meaningless."));
        }

        return caveats;
    }

    /// <summary>
    /// A bare aggregate with no GROUP BY always returns exactly one row, so an empty match
    /// surfaces as NULL rather than as zero rows — SEMANTIC_MODEL.md hard rule 11. Testing
    /// <see cref="ResultSet.IsEmpty"/> alone therefore never fires on a scalar template, which is
    /// how a NULL reached a caller reported as an answer.
    /// </summary>
    private static bool HasNullValue(ResultSet result) =>
        result.Rows.Count == 1 && result.Rows[0].Any(cell => cell is null);

    /// <summary>
    /// The two different kinds of nothing (SEMANTIC_MODEL.md §6.5.1). A games-played count above
    /// zero proves the subject is tracked, so the NULL means this statistic does not apply to
    /// their position rather than that no data exists for them at all.
    /// </summary>
    private static Caveat NoDataCaveat(ResultSet result)
    {
        var index = IndexOf(result, "games_played");
        var tracked = index >= 0
                      && result.Rows.Count == 1
                      && result.Rows[0][index] is long played
                      && played > 0;

        return tracked
            ? new Caveat(CaveatCodes.StatNotApplicable,
                "There is no value for this statistic even though the player has game records, "
                + "so the statistic does not apply to their position. Football stat lines carry "
                + "passing yards for quarterbacks, rushing for running backs and receiving for "
                + "receivers only.")
            : new Caveat(CaveatCodes.NoMatchingRows,
                "No rows matched, so this is not a zero. In this dataset an absent stat line "
                + "means the subject is not tracked — offensive line, tight end and all "
                + "defensive positions have no stat rows at all.");
    }

    /// <summary>
    /// Certified rankings return their whole tied set, so several rows sharing the ranked
    /// value is the tie itself rather than a hint of one.
    /// </summary>
    private static bool IsTiedAtTop(ResultSet result, string? rankedColumn)
    {
        if (rankedColumn is null || result.Rows.Count < 2)
        {
            return false;
        }

        var index = IndexOf(result, rankedColumn);
        if (index < 0)
        {
            return false;
        }

        var top = result.Rows[0][index]?.ToString();
        return result.Rows.All(row => row[index]?.ToString() == top);
    }

    /// <summary>
    /// A multi-row result carrying games_played is a cross-team comparison, which is where
    /// uneven schedules distort a total.
    /// </summary>
    private static bool ComparesAcrossTeams(ResultSet result) =>
        result.Rows.Count > 1 && IndexOf(result, "games_played") >= 0;

    private static int IndexOf(ResultSet result, string column)
    {
        for (var index = 0; index < result.Columns.Count; index++)
        {
            if (result.Columns[index].Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
