using SportsQa.Api.Contracts;

namespace SportsQa.EvalRunner;

/// <summary>
/// Console reporting. A reviewer should be able to read a failure without opening the code:
/// which golden, which assertion, expected versus actual, and what the pipeline actually did.
/// </summary>
public static class Report
{
    private const int LabelWidth = 34;

    public static void Header(string goldensPath, int count)
    {
        Console.WriteLine();
        Console.WriteLine($"Running {count} goldens from {Path.GetFileName(goldensPath)}");
        Console.WriteLine(new string('=', 78));
    }

    public static void Golden(Verdict verdict, AskResponse response)
    {
        var golden = verdict.Golden;
        var status = verdict.Passed ? "PASS" : "FAIL";

        Write(verdict.Passed ? ConsoleColor.Green : ConsoleColor.Red, $"{status}  ");
        Console.WriteLine($"{golden.Id.PadRight(LabelWidth)} [{golden.FailureClass}]");

        if (verdict.Passed)
        {
            return;
        }

        Console.WriteLine($"        question: {golden.Question}");

        if (golden.Slots is { Count: > 0 })
        {
            Console.WriteLine($"        slots given: {Render(golden.Slots)}");
        }

        foreach (var failure in verdict.Failures)
        {
            Console.WriteLine($"        {failure.Label}:");
            Console.WriteLine($"          expected: {failure.Expected}");
            Console.WriteLine($"          actual:   {failure.Actual}");
        }

        Console.WriteLine($"        pipeline: intent={response.Diagnostics.Intent} " +
                          $"tier={response.Diagnostics.Tier} " +
                          $"sql={response.Diagnostics.SqlSource?.ToString() ?? "none"}");

        if (golden.GroundTruthSql is not null)
        {
            Console.WriteLine($"        verify with: {golden.GroundTruthSql}");
        }

        Console.WriteLine();
    }

    public static bool Summary(IReadOnlyList<Verdict> verdicts)
    {
        var passed = verdicts.Count(verdict => verdict.Passed);
        var failed = verdicts.Count - passed;

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{passed}/{verdicts.Count} passed, {failed} failed");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Failing classes: " + string.Join(", ", verdicts
                .Where(verdict => !verdict.Passed)
                .Select(verdict => verdict.Golden.FailureClass)
                .Distinct()));
        }

        Console.WriteLine();
        Console.WriteLine("Coverage by failure class:");
        foreach (var group in verdicts.GroupBy(verdict => verdict.Golden.FailureClass)
                     .OrderBy(group => group.Key))
        {
            var groupPassed = group.Count(verdict => verdict.Passed);
            Console.WriteLine($"  {group.Key.PadRight(LabelWidth)} {groupPassed}/{group.Count()}");
        }

        Console.WriteLine();
        return failed == 0;
    }

    private static string Render(Dictionary<string, string> slots) =>
        string.Join(", ", slots.Select(slot => $"{slot.Key}={slot.Value}"));

    private static void Write(ConsoleColor colour, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
