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

/// <summary>A lexicon hit with its fuzzy score against the question text.</summary>
public sealed record ScoredEntity(EntityMatch Match, double Score);

/// <summary>
/// The live schema plus a lexicon of the entity names a question might mention. Both are
/// read from the database at startup rather than hardcoded, so a new sport or season needs
/// no code change.
///
/// At production scale the lexicon becomes a search index instead of an in-memory list; the
/// interface here is deliberately the same shape. Concretely that means Postgres pg_trgm over
/// a materialized view, plus a curated alias table for the nicknames fans actually type
/// ("Bosco", "SJB"). See PRODUCTION_NOTES.md §4.
///
/// Worth noting what this component really is: entity linking. The same index should back site
/// search autocomplete and the chatbot's slot filling — one artifact, two consumers. The
/// subsumption and shadowing rules below are exactly the disambiguation a search box needs.
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

    /// <summary>
    /// Whether a name matches an entity in the data, optionally of a required kind. Used to
    /// validate caller-supplied slot values, so a clarification answer cannot introduce a name
    /// the dataset has never seen — or a real name of the wrong kind, which matches no rows and
    /// reads as a real answer of nothing.
    /// </summary>
    public bool IsKnownEntity(string value, string? kind = null) =>
        _lexicon.Any(entity =>
            entity.Value.Equals(value, StringComparison.OrdinalIgnoreCase)
            && (kind is null || entity.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>
    /// Ranked lexicon hits by fuzzy score. Exact phrase matches keep subsumption (longer name
    /// wins); non-exact typos still score. Optional kind filter narrows to what an intent can use.
    /// </summary>
    public IReadOnlyList<ScoredEntity> FindFuzzy(string question, string? kind = null)
    {
        var candidates = kind is null
            ? _lexicon
            : _lexicon.Where(entity =>
                entity.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));

        var scored = candidates
            .Select(entity => new ScoredEntity(entity, ScoreMention(question, entity.Value)))
            .Where(hit => hit.Score > 0)
            .OrderByDescending(hit => hit.Score)
            .ThenByDescending(hit => hit.Match.Value.Length)
            .ToList();

        // When an exact longer name is present, drop shorter exact hits the same way
        // FindMentioned does — fuzzy must not reintroduce subsumed "Jackson" next to "Tony Jackson".
        var exact = scored.Where(hit => hit.Score >= 1.0).Select(hit => hit.Match).ToList();
        return scored
            .Where(hit => hit.Score < 1.0
                          || !exact.Any(other => Subsumes(other, hit.Match)))
            .ToList();
    }

    /// <summary>
    /// How well <paramref name="entityName"/> appears in <paramref name="question"/>.
    /// Exact phrase = 1.0; all tokens present = 0.95; space-insensitive exact = 0.93;
    /// otherwise max(token overlap, edit similarity), always below 1.0.
    /// </summary>
    public static double ScoreMention(string question, string entityName)
    {
        var haystack = $" {Normalize(question)} ";
        var entity = Normalize(entityName);
        if (entity.Length == 0)
        {
            return 0;
        }

        if (haystack.Contains($" {entity} "))
        {
            return 1.0;
        }

        var questionTokens = Normalize(question).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entityTokens = entity.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (entityTokens.Length == 0 || questionTokens.Length == 0)
        {
            return 0;
        }

        if (entityTokens.All(token => questionTokens.Contains(token)))
        {
            return 0.95;
        }

        var significant = entityTokens.Where(token => token.Length > 2).ToArray();
        var overlapDenom = significant.Length > 0 ? significant : entityTokens;
        var tokenOverlap = overlapDenom.Count(token => questionTokens.Contains(token))
                           / (double)overlapDenom.Length;

        var questionCompact = string.Concat(questionTokens);
        var entityCompact = string.Concat(entityTokens);
        if (questionCompact.Contains(entityCompact, StringComparison.Ordinal))
        {
            return 0.93;
        }

        var editSimilarity = BestEditSimilarity(questionTokens, entityTokens, entityCompact);
        var score = Math.Max(tokenOverlap, editSimilarity);
        return Math.Min(score, 0.99);
    }

    private static double BestEditSimilarity(
        string[] questionTokens, string[] entityTokens, string entityCompact)
    {
        var entityJoined = string.Join(' ', entityTokens);
        var best = EditSimilarity(string.Concat(questionTokens), entityCompact);
        best = Math.Max(best, EditSimilarity(string.Join(' ', questionTokens), entityJoined));

        var window = Math.Max(1, entityTokens.Length);
        for (var start = 0; start <= questionTokens.Length - window; start++)
        {
            var slice = questionTokens.AsSpan(start, window);
            var joined = string.Join(' ', slice.ToArray());
            var compact = string.Concat(slice.ToArray());
            best = Math.Max(best, EditSimilarity(joined, entityJoined));
            best = Math.Max(best, EditSimilarity(compact, entityCompact));
        }

        // Also try windows one token wider/narrower for glued vs spaced names.
        foreach (var size in new[] { window - 1, window + 1 })
        {
            if (size < 1 || size > questionTokens.Length)
            {
                continue;
            }

            for (var start = 0; start <= questionTokens.Length - size; start++)
            {
                var slice = questionTokens.AsSpan(start, size);
                var joined = string.Join(' ', slice.ToArray());
                var compact = string.Concat(slice.ToArray());
                best = Math.Max(best, EditSimilarity(joined, entityJoined));
                best = Math.Max(best, EditSimilarity(compact, entityCompact));
            }
        }

        return best;
    }

    private static double EditSimilarity(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 1.0;
        }

        var maxLen = Math.Max(left.Length, right.Length);
        if (maxLen == 0)
        {
            return 0;
        }

        return 1.0 - Levenshtein(left, right) / (double)maxLen;
    }

    private static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
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
