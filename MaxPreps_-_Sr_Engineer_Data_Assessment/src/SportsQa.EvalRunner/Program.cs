using System.Text.Json;

// ---------------------------------------------------------------------------
// EVAL RUNNER — YOUR WORK.
//
// Build a runnable pass/fail eval harness for the /ask endpoint (or for the pipeline
// invoked directly — your choice; see ASSESSMENT.md).
//
// Requirements:
//   - Reads a goldens file (>= 6 goldens; format is yours — goldens.example.json shows a
//     starting shape and two verified examples, extend or replace it as you see fit).
//   - Expected values must come from the DATA (your own independent queries), never from
//     the model's answers.
//   - Runs every golden, compares expected vs actual, and prints a report a reviewer can
//     read: per-golden pass/fail with expected vs actual, plus a summary line.
//   - Exit code 0 when all pass, non-zero otherwise (so it can gate CI).
//
// The skeleton below just proves the goldens file parses. Replace freely.
// ---------------------------------------------------------------------------

var goldensPath = args.Length > 0 ? args[0] : "goldens.example.json";
var json = File.ReadAllText(goldensPath);
using var doc = JsonDocument.Parse(json);
var goldens = doc.RootElement.GetProperty("goldens");

Console.WriteLine($"Loaded {goldens.GetArrayLength()} goldens from {goldensPath}:");
foreach (var golden in goldens.EnumerateArray())
{
    Console.WriteLine($"  [{golden.GetProperty("id").GetString()}] " +
                      golden.GetProperty("question").GetString());
}

Console.WriteLine();
Console.WriteLine("TODO: run them. This harness is yours to build.");
return 1; // a harness that runs nothing must not pretend to pass
