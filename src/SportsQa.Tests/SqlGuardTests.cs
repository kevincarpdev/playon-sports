using SportsQa.Api.Configuration;
using SportsQa.Api.Data;
using SportsQa.Api.Routing;
using SportsQa.Api.Security;
using SportsQa.Api.Sql;
using Xunit;

namespace SportsQa.Tests;

/// <summary>
/// Direct tests on the validation boundary. Every case in <see cref="KnownBypasses"/> was a
/// working exploit found by an adversarial review — each one read a table the caller was not
/// permitted, or ran unbounded work, while <c>Validate</c> returned allowed.
///
/// They are here so the bypass class stays closed. The guard is a conservative prefilter, not
/// a parser, so the property under test is "fails closed", not "understands SQL".
/// </summary>
public sealed class SqlGuardTests
{
    private static readonly SportsQaOptions Options = new()
    {
        DatabasePath = TestPaths.Database,
    };

    private static readonly SchemaCatalog Catalog = SchemaCatalog.Load(Options);
    private static readonly SqlGuard Guard = new(Catalog);

    /// <summary>Anonymous may read teams and games, never per-player or per-game stat lines.</summary>
    private static RoutingDecision Anonymous() =>
        new CapabilityRouter(Options).Route("how many teams", new Principal(Role.Anonymous));

    private static RoutingDecision Subscriber() =>
        new CapabilityRouter(Options).Route("how many teams", new Principal(Role.Subscriber));

    public static TheoryData<string, string> KnownBypasses() => new()
    {
        { "quoted identifier hides the table",
          "SELECT first_name FROM \"players\" LIMIT 3" },

        { "block comment stands in for whitespace",
          "SELECT first_name FROM/**/players LIMIT 3" },

        { "line comment before the table",
          "SELECT first_name FROM --x\n players LIMIT 3" },

        { "union leg reaches an unpermitted table",
          "SELECT school AS value FROM teams UNION ALL SELECT first_name FROM \"players\" LIMIT 8" },

        { "cte shadows a permitted table name",
          "WITH teams AS (SELECT first_name AS school FROM \"players\") SELECT school FROM teams LIMIT 5" },

        { "table-valued function reads restricted schema",
          "SELECT name FROM/**/pragma_table_info('players') LIMIT 6" },

        { "recursive cte burns cpu regardless of row cap",
          "WITH RECURSIVE t(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM t WHERE x<200000000) "
          + "SELECT SUM(x) FROM t" },

        { "bracketed identifier",
          "SELECT first_name FROM [players] LIMIT 3" },
    };

    [Theory]
    [MemberData(nameof(KnownBypasses))]
    public void Rejects_known_bypasses(string because, string sql)
    {
        var result = Guard.Validate(sql, Anonymous());

        Assert.False(result.IsAllowed, $"Bypass reopened — {because}: {sql}");
        Assert.NotNull(result.Code);
    }

    [Theory]
    [InlineData("SELECT * FROM player_game_stats LIMIT 1")]
    [InlineData("SELECT s.points FROM player_game_stats s LIMIT 1")]
    public void Denies_unpermitted_tables_for_anonymous(string sql) =>
        Assert.Equal("table_not_permitted", Guard.Validate(sql, Anonymous()).Code);

    [Fact]
    public void Allows_the_same_table_for_a_subscriber() =>
        Assert.True(Guard.Validate("SELECT s.points FROM player_game_stats s LIMIT 1",
            Subscriber()).IsAllowed);

    [Fact]
    public void Rejects_tables_absent_from_the_catalog() =>
        Assert.Equal("unknown_table",
            Guard.Validate("SELECT * FROM nonexistent_table", Subscriber()).Code);

    [Fact]
    public void Allows_a_legitimate_query_over_a_permitted_table() =>
        Assert.True(Guard.Validate("SELECT COUNT(*) FROM teams", Subscriber()).IsAllowed);

    [Fact]
    public void Rejects_hallucinated_columns() =>
        Assert.Equal("unknown_column",
            Guard.Validate("SELECT s.touchdowns FROM player_game_stats s", Subscriber()).Code);

    /// <summary>
    /// Writes are refused at the first gate because they do not begin with SELECT, which is why
    /// the code is not_a_select rather than forbidden_keyword. Denial is the contract; the
    /// specific gate that catches it is not.
    /// </summary>
    [Theory]
    [InlineData("DELETE FROM teams")]
    [InlineData("ATTACH DATABASE 'x' AS y")]
    [InlineData("DROP TABLE teams")]
    [InlineData("UPDATE teams SET school = 'x'")]
    public void Rejects_writes(string sql) =>
        Assert.False(Guard.Validate(sql, Subscriber()).IsAllowed);

    [Theory]
    [InlineData("SELECT 1; DROP TABLE teams")]
    [InlineData("SELECT * FROM teams; SELECT * FROM games")]
    public void Rejects_stacked_statements(string sql) =>
        Assert.Equal("multiple_statements", Guard.Validate(sql, Subscriber()).Code);

    /// <summary>A write hidden mid-statement must still be caught by the keyword pass.</summary>
    [Fact]
    public void Rejects_a_write_keyword_inside_a_select() =>
        Assert.Equal("forbidden_keyword",
            Guard.Validate("SELECT * FROM teams WHERE 1 IN (SELECT 1) AND delete", Subscriber()).Code);

    [Fact]
    public void Rejects_unbalanced_quotes() =>
        Assert.Equal("malformed_sql",
            Guard.Validate("SELECT * FROM teams WHERE school = 'Riverside", Subscriber()).Code);

    [Fact]
    public void Accepts_subqueries_because_certified_templates_use_them()
    {
        const string sql = """
            SELECT player FROM (
              SELECT p.first_name AS player, s.rebounds
              FROM player_game_stats s
              JOIN games g ON g.game_id = s.game_id
              JOIN players p ON p.player_id = s.player_id
            ) LIMIT 5
            """;

        Assert.True(Guard.Validate(sql, Subscriber()).IsAllowed);
    }

    [Theory]
    [InlineData("SELECT * FROM teams", 10, "LIMIT 10")]
    [InlineData("SELECT * FROM teams LIMIT 5", 10, "LIMIT 5")]
    [InlineData("SELECT * FROM teams LIMIT 5 OFFSET 2", 10, "LIMIT 5 OFFSET 2")]
    public void Enforces_a_row_limit_without_duplicating_an_existing_one(
        string sql, int maxRows, string expectedTail)
    {
        var enforced = SqlGuard.EnforceRowLimit(sql, maxRows);

        Assert.EndsWith(expectedTail, enforced);
        // A second LIMIT would be a syntax error rather than a tighter cap.
        Assert.Equal(1, enforced.Split("LIMIT", StringSplitOptions.None).Length - 1);
    }
}
