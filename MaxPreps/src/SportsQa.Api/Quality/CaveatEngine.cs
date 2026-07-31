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

        if (result.IsEmpty)
        {
            caveats.Add(new Caveat("no_matching_rows",
                "No rows matched. In this dataset an absent stat line means the player is not " +
                "tracked rather than that the value is zero — offensive line, tight end and all " +
                "defensive positions have no stat rows at all."));
        }

        if (IsTiedAtTop(result, rankedColumn))
        {
            caveats.Add(new Caveat("tied_result",
                $"{result.Rows.Count} rows share the top value. All are returned; there is no " +
                "single answer to this question."));
        }

        if (result.Truncated)
        {
            caveats.Add(new Caveat("truncated",
                "Results were truncated at the configured row ceiling."));
        }

        if (ComparesAcrossTeams(result))
        {
            caveats.Add(new Caveat("uneven_schedules",
                "Teams played between 8 and 14 games depending on sport and school, so season " +
                "totals are not directly comparable across teams."));
        }

        if (SportScopedIntents.Contains(plan.Intent) && slots.TryGetValue(Slots.Sport, out var sport))
        {
            caveats.Add(new Caveat("sport_scoped",
                $"Scoped to {sport} only. Points are not comparable across sports, so a " +
                "combined figure would be meaningless."));
        }

        return caveats;
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
