using System.Text.Json.Serialization;

namespace SportsQa.EvalRunner;

public sealed record GoldenFile(List<Golden> Goldens);

public sealed record Golden
{
    public required string Id { get; init; }
    public required string Question { get; init; }
    public string FailureClass { get; init; } = "unclassified";
    public string? Role { get; init; }
    public Dictionary<string, string>? Slots { get; init; }
    public required Expectation Expect { get; init; }

    /// <summary>The query used to derive the expected value, for reviewer re-verification.</summary>
    public string? GroundTruthSql { get; init; }

    public string Rationale { get; init; } = "";
}

/// <summary>
/// What a golden asserts. Every field is optional except Outcome, so a golden can pin an
/// outcome only (a refusal), a value, a tie, or a caveat — whatever that failure class needs.
/// </summary>
public sealed record Expectation
{
    public required string Outcome { get; init; }

    /// <summary>Result column the expected value lives in.</summary>
    public string? Column { get; init; }

    [JsonConverter(typeof(ScalarConverter))]
    public object? Value { get; init; }

    /// <summary>Absolute tolerance for floating-point comparison.</summary>
    public double? Tolerance { get; init; }

    public bool? IsTie { get; init; }
    public int? RowCount { get; init; }
    public string? HasCaveat { get; init; }
    public string? RefusalCode { get; init; }
    public List<string>? ClarificationSlots { get; init; }

    public string? SqlSource { get; init; }
}
