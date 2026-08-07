namespace SportsQa.Api.Configuration;

/// <summary>
/// Every tunable in the system. Nothing in the pipeline is allowed to hardcode a limit,
/// threshold or path — it comes from here so it can be retuned per environment without a
/// rebuild.
/// </summary>
public sealed class SportsQaOptions
{
    public const string SectionName = "SportsQa";

    public string DatabasePath { get; init; } = "";
    public string FakeLlmResponsesPath { get; init; } = "";
    public string SemanticModelPath { get; init; } = "";

    public ExecutionOptions Execution { get; init; } = new();
    public TrustOptions Trust { get; init; } = new();
    public AuthorizationOptions Authorization { get; init; } = new();
}

public sealed class ExecutionOptions
{
    /// <summary>Hard ceiling on rows returned to a caller, enforced while reading.</summary>
    public int MaxRows { get; init; } = 100;

    public int CommandTimeoutSeconds { get; init; } = 5;

    /// <summary>Rows a clarification may offer before we stop listing candidates.</summary>
    public int MaxClarificationOptions { get; init; } = 4;

    /// <summary>Size of a "top N" list when the question asks for one.</summary>
    public int TopListSize { get; init; } = 5;

    /// <summary>
    /// Minimum gap between the best and second-best fuzzy scores required to auto-resolve.
    /// Near-ties clarify rather than guess. Score floor is <see cref="TrustOptions.MinSlotConfidence"/>.
    /// </summary>
    public double FuzzyAutoResolveMinGap { get; init; } = 0.1;
}

public sealed class TrustOptions
{
    /// <summary>
    /// Below this, a slot is treated as unfilled and triggers a clarifying question.
    /// </summary>
    public double MinSlotConfidence { get; init; } = 0.75;

    /// <summary>Confidence we report for an answer produced by a certified template.</summary>
    public double CertifiedQueryConfidence { get; init; } = 0.99;

    /// <summary>Ceiling on reported confidence when we executed the model's own SQL.</summary>
    public double ModelQueryConfidenceCap { get; init; } = 0.70;

    /// <summary>Penalty applied when the result set is a tie at the cut line.</summary>
    public double TiePenalty { get; init; } = 0.15;

    /// <summary>
    /// Penalty applied when the query returned no usable value. The SQL was valid and the
    /// scope was right, so this is not an error — but it is not a number either, and reporting
    /// it at full confidence is how a NULL gets read as an answer.
    /// </summary>
    public double NoDataPenalty { get; init; } = 0.4;
}

public sealed class AuthorizationOptions
{
    /// <summary>Role assumed when no principal header is supplied.</summary>
    public string DefaultRole { get; init; } = "Subscriber";

    /// <summary>Header carrying the caller's role in this exercise; real OIDC replaces it.</summary>
    public string RoleHeader { get; init; } = "X-SportsQa-Role";
}
