using SportsQa.Api.Semantics;
using Xunit;

namespace SportsQa.Tests;

/// <summary>
/// Invariants over the intent catalog itself, asserted across every plan rather than the ones
/// someone remembered to check.
///
/// These exist because a judgement call went wrong: I granted PreferModelSql on the intents whose
/// recorded SQL I had executed and found correct, which is a statement about one execution rather
/// than about the query. The recorded SQL interpolates its filters as literals, so on an intent
/// that takes slots it silently answers a different question than the one asked.
/// </summary>
public sealed class IntentPolicyTests
{
    /// <summary>
    /// The recorded model SQL is a static string with its filters baked in as literals, so it
    /// cannot respond to caller input. An intent that requires slots must therefore use its
    /// certified template, where values arrive as bound parameters.
    ///
    /// Without this, flagging team_wins made "how many games did Oak Hill win" return Jackson
    /// Prep's total of 3 instead of Oak Hill's 4 — a plausible number, no error, wrong question.
    /// </summary>
    [Fact]
    public void Model_authored_sql_is_never_granted_to_an_intent_that_takes_slots()
    {
        var violations = IntentCatalog.All
            .Where(plan => plan.PreferModelSql && plan.RequiredSlots.Count > 0)
            .Select(plan => $"{plan.Intent} (slots: {string.Join(", ", plan.RequiredSlots)})")
            .ToList();

        Assert.True(violations.Count == 0,
            "These intents may not use model-authored SQL, because a static recorded query "
            + "cannot honour a caller-supplied slot: " + string.Join("; ", violations));
    }

    /// <summary>
    /// A refused intent never runs SQL at all, so declaring a preference about who authors it is
    /// contradictory and would hide the refusal if the refusal were ever removed.
    /// </summary>
    [Fact]
    public void A_refused_intent_does_not_also_claim_a_sql_preference()
    {
        var violations = IntentCatalog.All
            .Where(plan => plan is { IsRefused: true, PreferModelSql: true })
            .Select(plan => plan.Intent)
            .ToList();

        Assert.True(violations.Count == 0,
            "Refused intents execute no SQL: " + string.Join("; ", violations));
    }

    /// <summary>
    /// Guards the flag against silent drift in either direction: a route added without evidence,
    /// or a route removed without updating the argument for it in IntentCatalog's comment.
    /// </summary>
    [Fact]
    public void Exactly_the_verified_slotless_intents_use_model_authored_sql()
    {
        var actual = IntentCatalog.All
            .Where(plan => plan.PreferModelSql)
            .Select(plan => plan.Intent)
            .OrderBy(intent => intent)
            .ToList();

        Assert.Equal(["count_teams", "top_scorer_basketball"], actual);
    }
}
