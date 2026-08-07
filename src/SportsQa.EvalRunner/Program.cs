using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SportsQa.Api.Configuration;
using SportsQa.Api.Contracts;
using SportsQa.Api.Data;
using SportsQa.Api.Llm;
using SportsQa.Api.Pipeline;
using SportsQa.Api.Quality;
using SportsQa.Api.Routing;
using SportsQa.Api.Security;
using SportsQa.Api.Semantics;
using SportsQa.Api.Sql;
using SportsQa.EvalRunner;

// Runs the goldens through the real pipeline in-process. No server, no ports, no network — so
// this is one command in CI, and a failing golden is a failing build.

var root = RepoRoot.Find();
var goldensPath = args.Length > 0
    ? args[0]
    : Path.Combine(root, "src", "SportsQa.EvalRunner", "goldens.json");

var options = new SportsQaOptions
{
    DatabasePath = Path.Combine(root, "data", "sports.db"),
    FakeLlmResponsesPath = Path.Combine(root, "src", "SportsQa.Api", "Llm", "fake_llm_responses.json"),
    SemanticModelPath = Path.Combine(root, "SEMANTIC_MODEL.md"),
};

var pipeline = BuildPipeline(options);

var json = await File.ReadAllTextAsync(goldensPath);
var file = JsonSerializer.Deserialize<GoldenFile>(json,
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? throw new InvalidOperationException($"Could not parse {goldensPath}");

Report.Header(goldensPath, file.Goldens.Count);

var verdicts = new List<Verdict>();
foreach (var golden in file.Goldens)
{
    var role = Enum.TryParse<Role>(golden.Role, ignoreCase: true, out var parsed)
        ? parsed
        : Role.Subscriber;

    // TODO(contract): AskRequest optional StubIntent; Golden.StubIntent; EvalRunner wires it
    var response = await pipeline.AskAsync(
        new AskRequest(golden.Question, golden.Slots, golden.StubIntent),
        new Principal(role), CancellationToken.None);

    var verdict = Verifier.Verify(golden, response);
    verdicts.Add(verdict);
    Report.Golden(verdict, response);
}

return Report.Summary(verdicts) ? 0 : 1;

static QuestionPipeline BuildPipeline(SportsQaOptions options)
{
    var catalog = SchemaCatalog.Load(options);
    var facts = DatasetFacts.Load(options);

    return new QuestionPipeline(
        new FakeLlmClient(options.FakeLlmResponsesPath),
        new SemanticContextProvider(options, NullLogger<SemanticContextProvider>.Instance),
        new CapabilityRouter(options),
        new SlotResolver(catalog, facts, options),
        new CertifiedQueries(facts),
        new SqlGuard(catalog),
        new SqlExecutor(options),
        new CaveatEngine(),
        options,
        NullLogger<QuestionPipeline>.Instance);
}

/// <summary>
/// Locates the package root by walking up for data/sports.db, so the runner works from any
/// working directory — a bin folder in CI, or the project folder locally.
/// </summary>
internal static class RepoRoot
{
    public static string Find()
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

        throw new InvalidOperationException(
            "Could not locate the package root (expected data/sports.db above this directory).");
    }
}
