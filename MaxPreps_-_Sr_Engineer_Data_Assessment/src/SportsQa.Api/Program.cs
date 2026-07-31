using System.Text.Json.Serialization;
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

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(SportsQaOptions.SectionName).Get<SportsQaOptions>()
              ?? throw new InvalidOperationException(
                  $"Configuration section '{SportsQaOptions.SectionName}' is missing.");

builder.Services.AddSingleton(options);

// Read once at startup: schema, coverage facts, and the semantic model. None of these change
// per request, and re-reading them would be the hot path's biggest avoidable cost.
builder.Services.AddSingleton(_ => SchemaCatalog.Load(options));
builder.Services.AddSingleton(_ => DatasetFacts.Load(options));
builder.Services.AddSingleton<SemanticContextProvider>();

builder.Services.AddSingleton<ILlmClient>(_ => new FakeLlmClient(options.FakeLlmResponsesPath));
builder.Services.AddSingleton<PrincipalResolver>();
builder.Services.AddSingleton<CapabilityRouter>();
builder.Services.AddSingleton<SlotResolver>();
builder.Services.AddSingleton<CertifiedQueries>();
builder.Services.AddSingleton<SqlGuard>();
builder.Services.AddSingleton<SqlExecutor>();
builder.Services.AddSingleton<CaveatEngine>();
builder.Services.AddSingleton<QuestionPipeline>();

builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.MapGet("/health", (SchemaCatalog catalog, SemanticContextProvider semantics) =>
    Results.Ok(new
    {
        status = "ok",
        tables = catalog.Tables.Count,
        semanticModelLoaded = semantics.IsAvailable,
    }));

app.MapPost("/ask", async (
    AskRequest request,
    HttpContext context,
    QuestionPipeline pipeline,
    PrincipalResolver principals,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var principal = principals.Resolve(context.Request.Headers);

    try
    {
        var response = await pipeline.AskAsync(request, principal, cancellationToken);

        return response.Outcome switch
        {
            // A clarifying question is a normal result, not a failure.
            AskOutcome.Answered or AskOutcome.NeedsClarification => Results.Ok(response),
            AskOutcome.CannotAnswer => Results.Json(response, statusCode: 422),
            _ => Results.Json(response, statusCode: 500),
        };
    }
    catch (Exception exception)
    {
        // Last line of defence. A caller never sees an unhandled exception; the correlation
        // id is what ties their report to our logs.
        var correlationId = context.TraceIdentifier;
        logger.LogError(exception, "Unhandled failure answering question. Correlation {Id}",
            correlationId);

        return Results.Json(new AskResponse
        {
            Outcome = AskOutcome.Error,
            Question = request.Question ?? "",
            Confidence = 0,
            Refusal = new RefusalReason("internal_error",
                "Something went wrong on our side. Nothing was executed against the data.",
                null),
            Diagnostics = new Diagnostics
            {
                Intent = "unknown",
                Tier = "unknown",
                Role = principal.Role.ToString(),
                CorrelationId = correlationId,
            },
        }, statusCode: 500);
    }
});

// Internal operations surface. Read-only, and gated on the Admin role rather than merely
// hidden — the live data-quality defect here is rollup staleness, so that is what it exposes.
var admin = app.MapGroup("/admin");

admin.MapGet("/rollup-freshness", async (
    CertifiedQueries certified,
    SqlExecutor executor,
    PrincipalResolver principals,
    HttpContext context,
    CancellationToken cancellationToken) =>
{
    if (principals.Resolve(context.Request.Headers).Role != Role.Admin)
    {
        return Results.NotFound();
    }

    var query = certified.StaleRollupRows();
    var execution = await executor.ExecuteAsync(
        query.Sql, options.Execution.MaxRows, cancellationToken, query.Parameters);

    return execution.Succeeded
        ? Results.Ok(new
        {
            staleRows = execution.Data!.Rows.Count,
            columns = execution.Data.Columns,
            rows = execution.Data.Rows,
        })
        : Results.Problem("Freshness check failed.");
});

admin.MapGet("/schema", (
    SchemaCatalog catalog,
    DatasetFacts facts,
    PrincipalResolver principals,
    HttpContext context) =>
    principals.Resolve(context.Request.Headers).Role == Role.Admin
        ? Results.Ok(new
        {
            tables = catalog.Tables,
            sports = facts.Sports,
            seasons = facts.SeasonBySport,
            singleSeasonPerSport = facts.HasSingleSeasonPerSport,
        })
        : Results.NotFound());

app.Run();
