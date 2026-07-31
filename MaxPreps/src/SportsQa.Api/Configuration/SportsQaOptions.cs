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
    public RoutingOptions Routing { get; init; } = new();
    public AuthorizationOptions Authorization { get; init; } = new();
}

public sealed class ExecutionOptions
{
    /// <summary>Hard ceiling on rows returned to a caller, enforced while reading.</summary>
    public int MaxRows { get; init; } = 100;

    public int CommandTimeoutSeconds { get; init; } = 5;

    /// <summary>Rows a clarification may offer before we stop listing candidates.</summary>
    public int MaxClarificationOptions { get; init; } = 4;
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
}

public sealed class RoutingOptions
{
    /// <summary>Question length in words above which we escalate past the Lookup tier.</summary>
    public int LookupMaxWords { get; init; } = 8;

    public string[] AggregateSignals { get; init; } =
        ["most", "top", "best", "highest", "lowest", "average", "total", "leader", "rank"];

    public string[] DeepSignals { get; init; } =
        ["beat", "versus", " vs ", "compare", "better", "both", "between", "against"];
}

public sealed class AuthorizationOptions
{
    /// <summary>Role assumed when no principal header is supplied.</summary>
    public string DefaultRole { get; init; } = "Subscriber";

    /// <summary>Header carrying the caller's role in this exercise; real OIDC replaces it.</summary>
    public string RoleHeader { get; init; } = "X-SportsQa-Role";
}
