namespace SportsQa.Api.Llm;

/// <summary>
/// The boundary between your service and "the model".
///
/// In this exercise the registered implementation is <see cref="FakeLlmClient"/> — a
/// deterministic, recorded stand-in for a real LLM. It is imperfect on purpose: like a real
/// model, some of its interpretations are wrong in realistic ways. Your service should be
/// designed so that a real client could be swapped in behind this interface without the
/// rest of the system changing.
///
/// <paramref name="semanticContext"/> is the semantic description of the dataset that your
/// system would hand to a real model (see the SEMANTIC_MODEL deliverable). The fake client
/// ignores it, but your call sites should treat it as load-bearing.
/// </summary>
public interface ILlmClient
{
    Task<LlmInterpretation> InterpretAsync(string question, string semanticContext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// What the model returns for a natural-language question: its guess at the intent, a SQL
/// query it believes answers the question (or null when it can't produce one), its own
/// confidence, and free-text notes. None of these are guaranteed to be trustworthy.
/// </summary>
public sealed record LlmInterpretation(
    string Intent,
    string? Sql,
    double Confidence,
    string Notes);
