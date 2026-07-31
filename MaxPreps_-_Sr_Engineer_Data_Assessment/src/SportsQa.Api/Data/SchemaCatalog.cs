using Microsoft.Data.Sqlite;
using SportsQa.Api.Configuration;

namespace SportsQa.Api.Data;

public sealed record EntityMatch(
    string Kind, string Value, string Detail, IReadOnlyList<string> Sports)
{
    /// <summary>
    /// The sport, when this entity implies exactly one. A player plays a single sport, so
    /// asking a caller which sport they meant would be noise we can answer ourselves.
    /// </summary>
    public string? UnambiguousSport => Sports.Count == 1 ? Sports[0] : null;

    /// <summary>
    /// True when some longer entity name contains this one. The city "Jackson" is shadowed by
    /// both "Jackson Prep" and "Tony Jackson", so a bare "Jackson" in a question cannot be
    /// resolved to it — matching exactly is not the same as being the only candidate.
    /// </summary>
    public bool IsShadowed { get; init; }
}

/// <summary>
/// The live schema plus a lexicon of the entity names a question might mention. Both are
/// read from the database at startup rather than hardcoded, so a new sport or season needs
/// no code change.
///
/// At production scale the lexicon becomes a search index instead of an in-memory list;
/// the interface here is deliberately the same shape.
/// </summary>
public sealed class SchemaCatalog
{
    private readonly Dictionary<string, HashSet<string>> _columnsByTable;
    private readonly List<EntityMatch> _lexicon;

    public IReadOnlyCollection<string> Tables => _columnsByTable.Keys;

    private SchemaCatalog(Dictionary<string, HashSet<string>> columnsByTable, List<EntityMatch> lexicon)
    {
        _columnsByTable = columnsByTable;
        _lexicon = lexicon;
    }

    public static SchemaCatalog Load(SportsQaOptions options)
    {
        using var connection = SqliteConnections.OpenReadOnly(options.DatabasePath);
        return new SchemaCatalog(ReadSchema(connection), MarkShadowed(ReadLexicon(connection)));
    }

    /// <summary>
    /// Shadowing depends only on the lexicon, so it is computed once here rather than per
    /// question.
    /// </summary>
    private static List<EntityMatch> MarkShadowed(List<EntityMatch> lexicon)
    {
        var tokenSets = lexicon
            .Select(entity => Normalize(entity.Value).Split(' ').ToHashSet())
            .ToList();

        return lexicon
            .Select((entity, index) => entity with
            {
                IsShadowed = tokenSets.Where((_, other) => other != index)
                    .Any(other => other.IsProperSupersetOf(tokenSets[index])),
            })
            .ToList();
    }

    public bool HasTable(string table) => _columnsByTable.ContainsKey(table);

    public bool HasColumn(string column) =>
        _columnsByTable.Values.Any(columns => columns.Contains(column));

    /// <summary>
    /// Entities whose name appears in the question, with less specific matches removed.
    ///
    /// "Tony Jackson" and the city "Jackson" both match the same text, but the longer name
    /// subsumes the shorter one and is what the caller meant. Without this, every question
    /// about a player whose surname is also a place would be spuriously ambiguous — while a
    /// bare "Jackson" stays genuinely ambiguous, which is the case worth asking about.
    /// </summary>
    public IReadOnlyList<EntityMatch> FindMentioned(string question)
    {
        var haystack = $" {Normalize(question)} ";
        var matches = _lexicon
            .Where(entity => haystack.Contains($" {Normalize(entity.Value)} "))
            .ToList();

        return matches
            .Where(entity => !matches.Any(other => Subsumes(other, entity)))
            .ToList();
    }

    private static bool Subsumes(EntityMatch longer, EntityMatch shorter) =>
        longer.Value.Length > shorter.Value.Length
        && $" {Normalize(longer.Value)} ".Contains($" {Normalize(shorter.Value)} ");

    /// <summary>Substring matches, used to surface candidates behind a partial name.</summary>
    public IReadOnlyList<EntityMatch> FindPartial(string question)
    {
        var tokens = Normalize(question).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return _lexicon
            .Where(entity => Normalize(entity.Value)
                .Split(' ')
                .Any(part => part.Length > 2 && tokens.Contains(part)))
            .ToList();
    }

    private static string Normalize(string value)
    {
        var cleaned = new string(value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character == ' ' ? character : ' ')
            .ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Dictionary<string, HashSet<string>> ReadSchema(SqliteConnection connection)
    {
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in Query(connection,
                     "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
                     reader => reader.GetString(0)))
        {
            schema[table] = new HashSet<string>(
                Query(connection, $"SELECT name FROM pragma_table_info('{table}')",
                    reader => reader.GetString(0)),
                StringComparer.OrdinalIgnoreCase);
        }

        return schema;
    }

    private static List<EntityMatch> ReadLexicon(SqliteConnection connection)
    {
        var lexicon = new List<EntityMatch>();

        lexicon.AddRange(Query(connection,
            """
            SELECT p.first_name || ' ' || p.last_name, p.position, t.school, t.sport
            FROM players p JOIN teams t ON t.team_id = p.team_id
            """,
            reader => new EntityMatch("player", reader.GetString(0),
                $"{reader.GetString(1)}, {reader.GetString(2)} {reader.GetString(3)}",
                [reader.GetString(3)])));

        lexicon.AddRange(Query(connection,
            "SELECT school, GROUP_CONCAT(sport) FROM teams GROUP BY school",
            reader => new EntityMatch("school", reader.GetString(0), reader.GetString(1),
                reader.GetString(1).Split(','))));

        lexicon.AddRange(Query(connection,
            "SELECT city, state, GROUP_CONCAT(DISTINCT sport) FROM teams GROUP BY city",
            reader => new EntityMatch("city", reader.GetString(0), reader.GetString(1),
                reader.GetString(2).Split(','))));

        return lexicon;
    }

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        using var reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }

        return results;
    }
}

public static class SqliteConnections
{
    /// <summary>
    /// Read-only is enforced at the connection, not by convention. Even a validation bug
    /// cannot mutate the database through this path.
    /// </summary>
    public static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }
}
