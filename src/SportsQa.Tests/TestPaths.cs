namespace SportsQa.Tests;

/// <summary>
/// Locates the package root by walking up for data/sports.db, so tests run from any working
/// directory — the same approach the eval runner uses.
/// </summary>
internal static class TestPaths
{
    public static string Root { get; } = Find();

    public static string Database => Path.Combine(Root, "data", "sports.db");

    public static string FakeLlmResponses =>
        Path.Combine(Root, "src", "SportsQa.Api", "Llm", "fake_llm_responses.json");

    public static string SemanticModel => Path.Combine(Root, "SEMANTIC_MODEL.md");

    private static string Find()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "data", "sports.db")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate data/sports.db above the test binary.");
    }
}
