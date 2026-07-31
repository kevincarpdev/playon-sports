using SportsQa.Api.Configuration;

namespace SportsQa.Api.Pipeline;

/// <summary>
/// Holds the semantic model that a real LLM would receive as system context. Read once at
/// startup — it is the most expensive part of a real prompt, so it is never re-read per
/// request. At scale this becomes a retrieval step that returns only the relevant sections.
/// </summary>
public sealed class SemanticContextProvider
{
    public string Content { get; }
    public bool IsAvailable { get; }

    public SemanticContextProvider(SportsQaOptions options, ILogger<SemanticContextProvider> logger)
    {
        var path = options.SemanticModelPath;

        if (File.Exists(path))
        {
            Content = File.ReadAllText(path);
            IsAvailable = true;
            return;
        }

        // Non-fatal: the fake client ignores this argument. A real client would not, so this
        // is loud rather than silent.
        logger.LogWarning("Semantic model not found at {Path}. Model context will be empty.", path);
        Content = "";
        IsAvailable = false;
    }
}
