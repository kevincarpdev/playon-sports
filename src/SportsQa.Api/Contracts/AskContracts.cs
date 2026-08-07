using System.Text.Json.Serialization;

namespace SportsQa.Api.Contracts;

/// <summary>
/// A question, plus any clarifications the caller has already answered. Sending slots back
/// is what closes the clarification loop — without them a follow-up would ask forever.
/// </summary>
/// <param name="StubIntent">
/// Eval-harness override so goldens can exercise intents the Fake LLM does not know.
/// The public <c>/ask</c> endpoint clears this; production traffic must not set it.
/// </param>
// TODO(contract): AskRequest optional StubIntent; Golden.StubIntent; EvalRunner wires it
public sealed record AskRequest(
    string Question,
    Dictionary<string, string>? Slots = null,
    string? StubIntent = null);

public enum AskOutcome
{
    /// <summary>Validated SQL ran and produced data.</summary>
    Answered,

    /// <summary>A required slot is missing or ambiguous. Recoverable — answer and retry.</summary>
    NeedsClarification,

    /// <summary>The data cannot support this question. Clarifying would not help.</summary>
    CannotAnswer,

    /// <summary>Our fault. Never carries internals.</summary>
    Error,
}

/// <summary>Where the executed SQL came from. Callers deserve to know.</summary>
public enum SqlSource
{
    /// <summary>A reviewed template owned by the semantic layer.</summary>
    Certified,

    /// <summary>The model's own SQL, validated but not curated.</summary>
    Model,
}

public sealed record AskResponse
{
    public required AskOutcome Outcome { get; init; }
    public required string Question { get; init; }

    /// <summary>Our confidence, derived from validation and result shape — not the model's.</summary>
    public double Confidence { get; init; }

    public AnswerPayload? Answer { get; init; }
    public IReadOnlyList<Clarification> Clarifications { get; init; } = [];
    public IReadOnlyList<Caveat> Caveats { get; init; } = [];
    public RefusalReason? Refusal { get; init; }
    public required Diagnostics Diagnostics { get; init; }
}

/// <summary>Tabular result plus a scalar shortcut when the answer is a single cell.</summary>
public sealed record AnswerPayload(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    object? Scalar,
    bool IsTie,
    string Scope);

public sealed record Clarification(
    string Slot,
    string Question,
    string Reason,
    IReadOnlyList<ClarificationOption> Options,
    bool AllowOther);

public sealed record ClarificationOption(string Value, string Label, string? Detail = null);

public sealed record Caveat(string Code, string Message);

public sealed record RefusalReason(string Code, string Message, string? WhatWouldBeNeeded);

/// <summary>
/// Observable decisions. Exposed so routing, authorization and SQL provenance are testable
/// from the outside rather than inferred.
/// </summary>
public sealed record Diagnostics
{
    public required string Intent { get; init; }
    public required string Tier { get; init; }
    public required string Role { get; init; }
    public SqlSource? SqlSource { get; init; }

    /// <summary>Set when we discarded the model's SQL in favour of a certified template.</summary>
    public string? ModelSqlRejectedBecause { get; init; }

    public double ModelReportedConfidence { get; init; }
    public IReadOnlyDictionary<string, string> ResolvedSlots { get; init; } =
        new Dictionary<string, string>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }
}
