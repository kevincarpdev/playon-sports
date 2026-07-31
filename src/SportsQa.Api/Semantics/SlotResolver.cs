using SportsQa.Api.Configuration;
using SportsQa.Api.Contracts;
using SportsQa.Api.Data;

namespace SportsQa.Api.Semantics;

public sealed record SlotResolution(
    Dictionary<string, string> Values,
    List<Clarification> Clarifications)
{
    public bool IsComplete => Clarifications.Count == 0;
}

/// <summary>
/// Fills an intent's required slots from the question text, the database lexicon, and any
/// clarifications the caller already answered. Anything it cannot resolve confidently becomes
/// a clarifying question with concrete options rather than a guess.
///
/// Resolution is grounded: entity candidates come from the data, so we never offer an option
/// that would return nothing.
/// </summary>
public sealed class SlotResolver(SchemaCatalog catalog, DatasetFacts facts, SportsQaOptions options)
{
    public SlotResolution Resolve(
        string question, IntentPlan plan, IReadOnlyDictionary<string, string>? provided)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clarifications = new List<Clarification>();

        var supplied = provided is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(provided, StringComparer.OrdinalIgnoreCase);

        var mentioned = catalog.FindMentioned(question);
        var schools = mentioned
            .Where(entity => entity.Kind == "school")
            .DistinctBy(entity => entity.Value)
            .ToList();

        var entity = ResolveEntity(mentioned);

        // Entity before sport: a resolved player pins the sport, so we can answer that slot
        // ourselves instead of asking a question the data already settles.
        foreach (var slot in plan.RequiredSlots.OrderBy(SlotOrder))
        {
            // A caller-supplied slot is untrusted input, not a resolved fact. Taking it verbatim
            // let an invalid value satisfy slot-completeness and then miss every certified
            // template, falling through to the model's own SQL — producing exactly the
            // cross-sport, tie-blind answer the certified templates exist to prevent.
            if (supplied.TryGetValue(slot, out var answered) && !string.IsNullOrWhiteSpace(answered))
            {
                if (!IsAcceptable(slot, answered))
                {
                    clarifications.Add(BuildClarification(slot, question, plan, schools));
                    continue;
                }

                values[slot] = answered;
                continue;
            }

            var resolved = slot switch
            {
                Slots.Sport => ResolveSport(question, plan, entity),
                Slots.Metric => ResolveMetric(question, plan),
                Slots.Entity => entity?.Value,
                Slots.SchoolA => Pick(schools, 0),
                Slots.SchoolB => Pick(schools, 1),
                _ => null,
            };

            if (resolved is not null)
            {
                values[slot] = resolved;
            }
            else
            {
                clarifications.Add(BuildClarification(slot, question, plan, schools));
            }
        }

        return new SlotResolution(values, clarifications);
    }

    /// <summary>
    /// Whether a caller-supplied slot value is one we recognise. Every slot in this dataset has
    /// a closed domain — sports and entities come from the data, metrics from a fixed set — so
    /// an unrecognised value is rejected rather than passed downstream.
    /// </summary>
    private bool IsAcceptable(string slot, string value) => slot switch
    {
        Slots.Sport => facts.Sports.Contains(value, StringComparer.OrdinalIgnoreCase),
        Slots.Metric => Metric.Find(value) is not null,
        Slots.Entity or Slots.SchoolA or Slots.SchoolB => catalog.IsKnownEntity(value),
        _ => false,
    };

    /// <summary>
    /// Sport comes from the intent, then the question text, then the resolved entity. The last
    /// step is what stops us asking "which sport?" about a player who only plays one.
    /// </summary>
    private string? ResolveSport(string question, IntentPlan plan, EntityMatch? entity) =>
        plan.FixedSport
        ?? facts.MatchSport(question)
        ?? entity?.UnambiguousSport;

    /// <summary>Entity resolves before sport so the sport can be derived from it.</summary>
    private static int SlotOrder(string slot) => slot switch
    {
        Slots.Entity => 0,
        Slots.SchoolA or Slots.SchoolB => 1,
        _ => 2,
    };

    private static string? ResolveMetric(string question, IntentPlan plan)
    {
        if (plan.FixedMetric is not null)
        {
            return plan.FixedMetric.Key;
        }

        var lowered = question.ToLowerInvariant();
        return Metric.All
            .FirstOrDefault(metric => lowered.Contains(metric.Key.Replace('_', ' ')))
            ?.Key;
    }

    /// <summary>
    /// A single unshadowed entity resolves. Several of the same kind resolves to the first,
    /// which is correct for head-to-head questions naming two schools. Otherwise it stays
    /// unresolved and becomes a clarifying question — that is the bare "Jackson" case, which is
    /// a school, a city and a player surname at once.
    /// </summary>
    private static EntityMatch? ResolveEntity(IReadOnlyList<EntityMatch> mentioned)
    {
        var distinct = mentioned.DistinctBy(Key).Where(entity => !entity.IsShadowed).ToList();

        return distinct.Count switch
        {
            0 => null,
            1 => distinct[0],
            _ => distinct.DistinctBy(entity => entity.Kind).Count() == 1 ? distinct[0] : null,
        };
    }

    private Clarification BuildClarification(
        string slot, string question, IntentPlan plan, IReadOnlyList<EntityMatch> schools) =>
        slot switch
        {
            Slots.Sport => new Clarification(
                Slots.Sport,
                "Which sport did you mean?",
                "Both sports could answer this, and they can disagree.",
                facts.Sports
                    .Select(sport => new ClarificationOption(
                        sport, sport, $"{sport} {facts.SeasonFor(sport)}"))
                    .ToList(),
                AllowOther: false),

            Slots.Metric => new Clarification(
                Slots.Metric,
                "What should \"best\" mean?",
                "This dataset has no definition of best. Totals also favour players whose " +
                "team played more games.",
                MetricOptions(plan),
                AllowOther: true),

            Slots.SchoolA or Slots.SchoolB => new Clarification(
                slot,
                "Which two schools did you mean?",
                $"I found {schools.Count} school name(s) in that question and need two.",
                Candidates(catalog.FindPartial(question).Where(e => e.Kind == "school")),
                AllowOther: true),

            _ => new Clarification(
                Slots.Entity,
                "Which one did you mean?",
                "That name matches more than one thing in this dataset.",
                Candidates(catalog.FindPartial(question)),
                AllowOther: true),
        };

    private List<ClarificationOption> MetricOptions(IntentPlan plan) =>
        Metric.All
            .Where(metric => plan.FixedSport is null || metric.OnlyForSport is null
                             || metric.OnlyForSport == plan.FixedSport)
            .Take(options.Execution.MaxClarificationOptions)
            .Select(metric => new ClarificationOption(metric.Key, metric.Label, metric.OnlyForSport))
            .ToList();

    private List<ClarificationOption> Candidates(IEnumerable<EntityMatch> matches) =>
        matches
            .DistinctBy(Key)
            .Take(options.Execution.MaxClarificationOptions)
            .Select(entity => new ClarificationOption(
                entity.Value, $"{entity.Value} ({entity.Kind})", entity.Detail))
            .ToList();

    private static string? Pick(IReadOnlyList<EntityMatch> schools, int index) =>
        index < schools.Count ? schools[index].Value : null;

    private static string Key(EntityMatch entity) => $"{entity.Kind}:{entity.Value}";
}
