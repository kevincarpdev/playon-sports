using SportsQa.Api.Configuration;
using SportsQa.Api.Contracts;

using SportsQa.Api.Llm;
using SportsQa.Api.Quality;
using SportsQa.Api.Routing;
using SportsQa.Api.Security;
using SportsQa.Api.Semantics;
using SportsQa.Api.Sql;

namespace SportsQa.Api.Pipeline;

/// <summary>
/// Turns a question into an answer, a clarifying question, a refusal, or an error — and
/// nothing else. This is the only component that knows the order of operations; every stage
/// it calls is independently testable.
///
/// The trust posture: the model classifies intent and surfaces entities. Where we recognise
/// the intent, we run our own certified SQL rather than the model's. Model SQL is the
/// fallback for unrecognised intents, and even then it only runs after static validation.
/// </summary>
public sealed class QuestionPipeline(
    ILlmClient llm,
    SemanticContextProvider semanticContext,
    CapabilityRouter router,
    SlotResolver slotResolver,
    CertifiedQueries certified,
    SqlGuard guard,
    SqlExecutor executor,
    CaveatEngine caveats,
    SportsQaOptions options,
    ILogger<QuestionPipeline> logger)
{
    public async Task<AskResponse> AskAsync(
        AskRequest request, Principal principal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return Refuse(request, "empty_question",
                new RefusalReason("empty_question", "Ask a question about the data.", null),
                "unknown", ModelTier.Lookup, principal);
        }

        var routing = router.Route(request.Question, principal);

        var interpretation = await llm.InterpretAsync(
            request.Question, semanticContext.Content, cancellationToken);

        var plan = IntentCatalog.For(interpretation.Intent);

        // Ops intents are invisible to client roles: refuse as unsupported rather than
        // forbidden, so the internal tool surface cannot be enumerated by probing.
        if (IntentCatalog.IsOpsIntent(plan.Intent) && !routing.Grants(Capability.OpsIntents))
        {
            plan = IntentCatalog.For("unrecognised");
        }

        if (plan.IsRefused)
        {
            return Refuse(request, plan.Intent, plan.Refusal!, plan.Intent, routing.Tier, principal,
                interpretation.Confidence);
        }

        routing = router.EscalateFor(routing, plan.RequiredCapability);

        if (!routing.Grants(plan.RequiredCapability))
        {
            return Refuse(request, plan.Intent,
                new RefusalReason("capability_not_granted",
                    "That question needs a level of access this request does not have.",
                    "A higher subscription tier."),
                plan.Intent, routing.Tier, principal, interpretation.Confidence);
        }

        var slots = slotResolver.Resolve(request.Question, plan, request.Slots);
        if (!slots.IsComplete)
        {
            return new AskResponse
            {
                Outcome = AskOutcome.NeedsClarification,
                Question = request.Question,
                Confidence = 0,
                Clarifications = slots.Clarifications,
                Diagnostics = Diagnose(plan.Intent, routing, principal, interpretation, slots.Values),
            };
        }

        return await ExecuteAsync(request, plan, slots, routing, principal, interpretation,
            cancellationToken);
    }

    private async Task<AskResponse> ExecuteAsync(
        AskRequest request,
        IntentPlan plan,
        SlotResolution slots,
        RoutingDecision routing,
        Principal principal,
        LlmInterpretation interpretation,
        CancellationToken cancellationToken)
    {
        var query = BuildCertified(plan, slots.Values);
        var source = SqlSource.Certified;
        string? modelRejection = null;

        if (query is null)
        {
            // No certified template. Fall back to the model's SQL, but only if it survives
            // static validation against the live schema and the caller's grant.
            var verdict = guard.Validate(interpretation.Sql ?? "", routing);
            if (!verdict.IsAllowed)
            {
                return Refuse(request, plan.Intent,
                    new RefusalReason(verdict.Code!, verdict.Detail!,
                        "A supported question, or a corrected query."),
                    plan.Intent, routing.Tier, principal, interpretation.Confidence);
            }

            query = new CertifiedQuery(interpretation.Sql!, new Dictionary<string, object?>(),
                "Scope not verified: executed the model's own query.");
            source = SqlSource.Model;
        }
        else if (interpretation.Sql is not null)
        {
            var verdict = guard.Validate(interpretation.Sql, routing);
            modelRejection = verdict.IsAllowed
                ? "Superseded by a certified template for this intent."
                : $"{verdict.Code}: {verdict.Detail}";
        }

        // Certified templates go through the same guard as model SQL. Defence in depth, and
        // it applies the role table allow-list uniformly.
        var guarded = guard.Validate(query.Sql, routing);
        if (!guarded.IsAllowed)
        {
            return Refuse(request, plan.Intent,
                new RefusalReason(guarded.Code!, guarded.Detail!, null),
                plan.Intent, routing.Tier, principal, interpretation.Confidence);
        }

        var execution = await executor.ExecuteAsync(
            query.Sql, routing.MaxRows, cancellationToken, query.Parameters);

        if (!execution.Succeeded)
        {
            logger.LogWarning("Query failed for intent {Intent}: {Detail}",
                plan.Intent, execution.ErrorDetail);

            return Refuse(request, plan.Intent,
                new RefusalReason("query_failed",
                    "The query for that question could not be executed against this dataset.",
                    null),
                plan.Intent, routing.Tier, principal, interpretation.Confidence);
        }

        var result = execution.Data!;
        var evaluated = caveats.Evaluate(plan, slots.Values, result, query.RankedValueColumn);
        var isTie = evaluated.Any(caveat => caveat.Code == CaveatCodes.TiedResult);

        return new AskResponse
        {
            Outcome = AskOutcome.Answered,
            Question = request.Question,
            Confidence = Score(source, evaluated),
            Answer = new AnswerPayload(result.Columns, result.Rows, result.Scalar, isTie, query.Scope),
            Caveats = evaluated,
            Diagnostics = Diagnose(plan.Intent, routing, principal, interpretation, slots.Values)
                with { SqlSource = source, ModelSqlRejectedBecause = modelRejection },
        };
    }

    /// <summary>
    /// Maps a resolved intent to its certified query. Returns null when we have no template,
    /// which is the signal to fall back to guarded model SQL.
    /// </summary>
    private CertifiedQuery? BuildCertified(IntentPlan plan, IReadOnlyDictionary<string, string> slots)
    {
        var sport = Slot(slots, Slots.Sport) ?? plan.FixedSport;
        var entity = Slot(slots, Slots.Entity);
        var metric = Metric.Find(Slot(slots, Slots.Metric) ?? plan.FixedMetric?.Key ?? "");

        return plan.Intent switch
        {
            "count_teams" => certified.CountTeams(),
            "schools_both_sports" => certified.SchoolsInBothSports(),

            "top_scorer_basketball" or "top_scorer_overall" when sport is not null && metric is not null =>
                certified.TopByMetric(sport, metric, topN: 1),

            "top5_scorers_basketball" when sport is not null && metric is not null =>
                certified.TopByMetric(sport, metric, topN: options.Execution.TopListSize),

            "best_player" when sport is not null && metric is not null =>
                certified.TopByMetric(sport, metric, topN: 1),

            "max_rebounds_single_game" when sport is not null =>
                certified.SingleGameMax(sport, "rebounds", "rebounds"),

            "highest_scoring_game" when sport is not null =>
                certified.HighestScoringGame(sport),

            "roster_count" when entity is not null && sport is not null =>
                certified.RosterCount(entity, sport),

            "team_wins" when entity is not null && sport is not null =>
                certified.TeamWins(entity, sport),

            "entity_points" when entity is not null && sport is not null =>
                certified.TeamPointsFor(entity, sport),

            "player_passing_yards" or "player_touchdowns" or "player_total_points" or "player_avg_ppg"
                when entity is not null && sport is not null && metric is not null =>
                certified.PlayerMetric(entity, sport, metric),

            "head_to_head" when sport is not null =>
                certified.HeadToHead(
                    Slot(slots, Slots.SchoolA)!, Slot(slots, Slots.SchoolB)!, sport),

            "ops:rollup_freshness" => certified.StaleRollupRows(),

            _ => null,
        };
    }

    /// <summary>
    /// Confidence is priced from what we observed about the result, never from what the model
    /// reported about itself. A tie means there is no single answer; no usable value means there
    /// is no answer at all, which is the larger penalty of the two.
    /// </summary>
    private double Score(SqlSource source, IReadOnlyList<Caveat> caveats)
    {
        var trust = options.Trust;
        var score = source == SqlSource.Certified
            ? trust.CertifiedQueryConfidence
            : trust.ModelQueryConfidenceCap;

        if (caveats.Any(caveat => caveat.Code == CaveatCodes.TiedResult))
        {
            score -= trust.TiePenalty;
        }

        if (caveats.Any(caveat => caveat.Code is CaveatCodes.NoMatchingRows
                                             or CaveatCodes.StatNotApplicable))
        {
            score -= trust.NoDataPenalty;
        }

        return Math.Round(Math.Max(score, 0), 2);
    }

    private AskResponse Refuse(
        AskRequest request,
        string intent,
        RefusalReason refusal,
        string diagnosticIntent,
        ModelTier tier,
        Principal principal,
        double modelConfidence = 0) => new()
    {
        Outcome = AskOutcome.CannotAnswer,
        Question = request.Question,
        Confidence = 0,
        Refusal = refusal,
        Diagnostics = new Diagnostics
        {
            Intent = diagnosticIntent,
            Tier = tier.ToString(),
            Role = principal.Role.ToString(),
            ModelReportedConfidence = modelConfidence,
        },
    };

    private static Diagnostics Diagnose(
        string intent,
        RoutingDecision routing,
        Principal principal,
        LlmInterpretation interpretation,
        Dictionary<string, string> slots) => new()
    {
        Intent = intent,
        Tier = routing.Tier.ToString(),
        Role = principal.Role.ToString(),
        ModelReportedConfidence = interpretation.Confidence,
        ResolvedSlots = slots,
    };

    private static string? Slot(IReadOnlyDictionary<string, string> slots, string name) =>
        slots.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
