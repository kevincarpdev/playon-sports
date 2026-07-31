using Microsoft.Data.Sqlite;
using SportsQa.Api.Llm;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["SportsQa:DatabasePath"]
             ?? throw new InvalidOperationException("SportsQa:DatabasePath not configured");
var responsesPath = builder.Configuration["SportsQa:FakeLlmResponsesPath"]
             ?? throw new InvalidOperationException("SportsQa:FakeLlmResponsesPath not configured");

builder.Services.AddSingleton<ILlmClient>(_ => new FakeLlmClient(responsesPath));

var app = builder.Build();

// Proves the wiring works out of the box: DB reachable, fake LLM loaded.
app.MapGet("/health", (ILlmClient llm) =>
{
    using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT COUNT(*) FROM teams";
    var teamCount = (long)command.ExecuteScalar()!;
    return Results.Ok(new { status = "ok", teams = teamCount });
});

// ---------------------------------------------------------------------------
// YOUR WORK STARTS HERE.
//
// Implement POST /ask. Contract (see ASSESSMENT.md for details):
//   Request:  { "question": "How many teams are in the database?" }
//   Response: your design — but it must be structured (not raw model text), it must
//   distinguish answered / cannot-answer / error outcomes, and it must never surface an
//   unhandled exception to the caller.
//
// Flow: take the question -> ILlmClient.InterpretAsync(question, semanticContext) ->
// decide what to do with the interpretation (validate? execute? refuse?) -> respond.
// How much you trust the interpretation is the design problem.
//
// Structure the code however you like — extract services/modules as you see fit. The
// skeleton is deliberately minimal; its shape is not a constraint.
// ---------------------------------------------------------------------------
app.MapPost("/ask", (AskRequest request, ILlmClient llm) =>
{
    return Results.StatusCode(StatusCodes.Status501NotImplemented);
});

app.Run();

public sealed record AskRequest(string Question);
