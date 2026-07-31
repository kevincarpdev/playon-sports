using Microsoft.Data.Sqlite;
using SportsQa.Api.Configuration;

namespace SportsQa.Api.Data;

/// <summary>
/// Facts about coverage, read from the data at startup. Sports and seasons are never
/// hardcoded, so adding a season or a sport changes no code — and the clarification options
/// the caller sees stay in step with what actually exists.
/// </summary>
public sealed class DatasetFacts
{
    public required IReadOnlyList<string> Sports { get; init; }

    /// <summary>
    /// The season for each sport. Single-valued today; when a sport gains a second season
    /// this stops being a safe shortcut and season becomes a slot in its own right.
    /// </summary>
    public required IReadOnlyDictionary<string, string> SeasonBySport { get; init; }

    public bool HasSingleSeasonPerSport { get; private init; }

    public static DatasetFacts Load(SportsQaOptions options)
    {
        using var connection = SqliteConnections.OpenReadOnly(options.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sport, season, COUNT(*) FROM games GROUP BY sport, season ORDER BY sport";

        var seasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sport = reader.GetString(0);
            if (!seasons.TryGetValue(sport, out var list))
            {
                seasons[sport] = list = [];
            }

            list.Add(reader.GetString(1));
        }

        return new DatasetFacts
        {
            Sports = seasons.Keys.OrderBy(sport => sport).ToList(),
            SeasonBySport = seasons.ToDictionary(
                entry => entry.Key,
                entry => entry.Value[0],
                StringComparer.OrdinalIgnoreCase),
            HasSingleSeasonPerSport = seasons.Values.All(list => list.Count == 1),
        };
    }

    public string? SeasonFor(string sport) =>
        SeasonBySport.TryGetValue(sport, out var season) ? season : null;

    public string? MatchSport(string text)
    {
        var lowered = text.ToLowerInvariant();
        return Sports.FirstOrDefault(sport => lowered.Contains(sport.ToLowerInvariant()));
    }
}
