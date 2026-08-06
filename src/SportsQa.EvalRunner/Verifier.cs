using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SportsQa.Api.Contracts;

namespace SportsQa.EvalRunner;

public sealed record Check(bool Passed, string Label, string Expected, string Actual);

public sealed record Verdict(Golden Golden, IReadOnlyList<Check> Checks)
{
    public bool Passed => Checks.All(check => check.Passed);
    public IEnumerable<Check> Failures => Checks.Where(check => !check.Passed);
}

/// <summary>
/// Compares a response against a golden's expectations. Each assertion is reported separately
/// so a failure says which property drifted, not merely that the golden broke.
/// </summary>
public static class Verifier
{
    public static Verdict Verify(Golden golden, AskResponse response)
    {
        var checks = new List<Check>
        {
            Check("outcome", golden.Expect.Outcome, response.Outcome.ToString()),
        };

        var expect = golden.Expect;

        if (expect.RefusalCode is not null)
        {
            checks.Add(Check("refusal code", expect.RefusalCode, response.Refusal?.Code ?? "(none)"));
        }

        if (expect.ClarificationSlots is not null)
        {
            var actual = response.Clarifications.Select(c => c.Slot).OrderBy(slot => slot);
            checks.Add(Check("clarification slots",
                string.Join(", ", expect.ClarificationSlots.OrderBy(slot => slot)),
                string.Join(", ", actual)));
        }

        if (expect.Value is not null)
        {
            checks.Add(VerifyValue(expect, response));
        }

        if (expect.RowCount is not null)
        {
            checks.Add(Check("row count",
                expect.RowCount.Value.ToString(),
                (response.Answer?.Rows.Count ?? 0).ToString()));
        }

        if (expect.IsTie is not null)
        {
            checks.Add(Check("is tie",
                expect.IsTie.Value.ToString(),
                (response.Answer?.IsTie ?? false).ToString()));
        }

        if (expect.HasCaveat is not null)
        {
            checks.Add(Check("caveat present",
                expect.HasCaveat,
                response.Caveats.Any(c => c.Code == expect.HasCaveat)
                    ? expect.HasCaveat
                    : $"(absent; got: {Codes(response)})"));
        }

        if (expect.SqlSource is not null)
        {
            checks.Add(Check("sql source", expect.SqlSource,
                response.Diagnostics.SqlSource?.ToString() ?? "(none)"));
        }

        return new Verdict(golden, checks);
    }

    private static Check VerifyValue(Expectation expect, AskResponse response)
    {
        var label = expect.Column is null ? "value" : $"value[{expect.Column}]";
        var expected = Render(expect.Value);

        if (response.Answer is null)
        {
            return new Check(false, label, expected, "(no answer payload)");
        }

        var actual = Extract(response.Answer, expect.Column);
        if (actual is null)
        {
            return new Check(false, label, expected,
                $"(column not found; got: {string.Join(", ", response.Answer.Columns)})");
        }

        return new Check(Matches(expect, actual), label, expected, Render(actual));
    }

    /// <summary>
    /// Reads the named column from the first row, since a golden asserts the top result. Falls
    /// back to the scalar shortcut when no column is named.
    /// </summary>
    private static object? Extract(AnswerPayload answer, string? column)
    {
        if (column is null)
        {
            return answer.Scalar;
        }

        for (var index = 0; index < answer.Columns.Count; index++)
        {
            if (answer.Columns[index].Equals(column, StringComparison.OrdinalIgnoreCase))
            {
                return answer.Rows.Count == 0 ? null : answer.Rows[0][index];
            }
        }

        return null;
    }

    private static bool Matches(Expectation expect, object actual)
    {
        if (TryNumber(expect.Value, out var expectedNumber) && TryNumber(actual, out var actualNumber))
        {
            return Math.Abs(expectedNumber - actualNumber) <= (expect.Tolerance ?? 0);
        }

        return string.Equals(Render(expect.Value), Render(actual), StringComparison.Ordinal);
    }

    private static bool TryNumber(object? value, out double number)
    {
        switch (value)
        {
            case null:
                number = 0;
                return false;
            case double d:
                number = d;
                return true;
            case long l:
                number = l;
                return true;
            case int i:
                number = i;
                return true;
            default:
                return double.TryParse(value.ToString(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out number);
        }
    }

    private static string Render(object? value) => value switch
    {
        null => "(null)",
        double d => d.ToString("0.####", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)",
    };

    private static string Codes(AskResponse response) =>
        response.Caveats.Count == 0 ? "none" : string.Join(", ", response.Caveats.Select(c => c.Code));

    private static Check Check(string label, string expected, string actual) =>
        new(string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase), label, expected, actual);
}

/// <summary>
/// Reads a golden's expected value as a number or string without forcing the JSON to declare
/// which. Keeps the goldens file readable.
/// </summary>
public sealed class ScalarConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var integer) => integer,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}
