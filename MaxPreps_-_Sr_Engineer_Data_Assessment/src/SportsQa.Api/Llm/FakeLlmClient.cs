using System.Text.Json;
using System.Text.RegularExpressions;

namespace SportsQa.Api.Llm;

/// <summary>
/// Deterministic recorded LLM. Matches incoming questions against the recorded set in
/// fake_llm_responses.json (case-, whitespace- and punctuation-insensitive) and returns the
/// recorded interpretation. Unknown questions get a low-confidence "I don't know" response.
///
/// Do not edit this class or the recorded responses — they are the fixed model you are
/// building around. (If you spot a bug in the harness itself, note it in FINDINGS.md.)
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Dictionary<string, LlmInterpretation> _recorded;

    public FakeLlmClient(string responsesPath)
    {
        using var stream = File.OpenRead(responsesPath);
        var doc = JsonSerializer.Deserialize<ResponseFile>(stream, JsonOptions)
                  ?? throw new InvalidOperationException($"Could not parse {responsesPath}");
        _recorded = doc.Responses.ToDictionary(
            r => Normalize(r.Question),
            r => new LlmInterpretation(r.Intent, r.Sql, r.Confidence, r.Notes));
    }

    public Task<LlmInterpretation> InterpretAsync(string question, string semanticContext,
        CancellationToken cancellationToken = default)
    {
        var interpretation = _recorded.TryGetValue(Normalize(question), out var hit)
            ? hit
            : new LlmInterpretation(
                Intent: "unknown",
                Sql: null,
                Confidence: 0.0,
                Notes: "I don't know this dataset well enough to answer that question.");
        return Task.FromResult(interpretation);
    }

    private static string Normalize(string question)
    {
        var lowered = question.ToLowerInvariant();
        var stripped = Regex.Replace(lowered, @"[^a-z0-9\s]", "");
        return Regex.Replace(stripped, @"\s+", " ").Trim();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record ResponseFile(List<RecordedResponse> Responses);

    private sealed record RecordedResponse(
        string Question, string Intent, string? Sql, double Confidence, string Notes);
}
