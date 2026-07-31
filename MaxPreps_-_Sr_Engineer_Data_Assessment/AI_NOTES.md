# AI Notes

How I actually used AI on this, including where it was wrong.

## Tools

- **Claude Code (Opus)** — the main driver. Data forensics, the semantic model draft, the C#
  implementation, and the writeups.
- **`sqlite3` CLI** — every factual claim in `SEMANTIC_MODEL.md` and `FINDINGS.md` was verified
  by a query I ran and read myself. This is the important discipline: AI proposed hypotheses
  about the data, SQL decided which were true.
- **Web fetches** of the public MaxPreps football and track & field sections, to ground
  `PRODUCTION_NOTES.md` in the real domain rather than my assumptions about it.

No live LLM is in the graded path. The submission runs offline with no keys.

---

## The prompt pattern that worked

The single highest-value move was **making the agent do forensics before writing anything**.
Rather than "implement `/ask`", I ran a recon pass first and required every claim to be backed
by a query. The framing was roughly:

> Before any implementation: catalogue the recorded model responses against the actual schema.
> For each one, tell me what would happen if we executed it. Then check the data for
> disagreements between `player_season_totals` and `player_game_stats`, orphaned foreign keys,
> ties at the top of any ranking, and columns that don't exist. Verify everything with SQL —
> don't infer from the schema.

Why it worked: it inverted the default order. Writing code first would have produced a
plausible pipeline that executed the model's SQL and returned 165 for Marcus Bell — a passing
build with a wrong answer. Doing forensics first meant the architecture was a *response to
evidence*. The certified-query design exists because I could count five distinct model failure
modes before writing a line.

The second pattern that paid off: **asking for the distribution, not the max.** "What's the
highest rebound total" gives you 12. "Show me the distribution near the top" reveals 52 rows
piled at 12 with a smooth curve below — a clipped column, which is a completely different
finding. Aggregates hide shape.

---

## Where AI was wrong, and how I caught it

### 1. It under-counted a tie by an order of magnitude

Early in recon I asked for the top rebound performances and got a `LIMIT 5` result: five
players at 12. I wrote "at least 5 players tied at 12" into the semantic model and the plan.

That was wrong — it was an artifact of the `LIMIT 5`, not the data. When I later built the
golden I needed an exact count and ran `COUNT(*) WHERE rebounds = 12`: **52 stat lines across
34 players**. Then the distribution query showed the ceiling.

**How I caught it:** writing a golden forced an exact number. A prose claim tolerated
"at least 5"; an assertion did not. That's an argument for writing evals early — they're a
forcing function on your own sloppiness, not just a regression net.

I corrected `SEMANTIC_MODEL.md` and `PLAN.md`, and added §6.9 on clipped columns.

### 2. It silently resolved "Jackson" to a city

My first `SlotResolver` resolved an entity when exactly one lexicon entry matched the question
exactly. For "How many points did Jackson score this season?" only the **city** "Jackson"
matched exactly — "Jackson Prep" and "Tony Jackson" are longer strings not present verbatim.

So it resolved to the city, would have queried it as a school, and returned zero rows. A
plausible-looking answer to a question nobody asked. This is precisely the failure class the
whole exercise is about, and I'd written it myself while building the defence against it.

**How I caught it:** the `resolvedSlots` diagnostic. The response *looked* fine — it asked a
sensible-seeming clarifying question about sport — but the diagnostics showed
`entity: "Jackson"` already filled. I'd only added that field for observability; it caught a
real bug.

Fix: precompute **shadowing** at lexicon load. An entity whose token set is a strict subset of
another's can never auto-resolve from a bare mention. Paired with **subsumption** so
"Tony Jackson" still resolves cleanly.

### 3. It let a word-count heuristic refuse a legitimate question

The capability router classified tier from question text. "How many points did Jackson score
this season?" is 8 words with no aggregate keyword, so it routed as `Lookup`, which lacks
`AggregateQuery`, so the request was refused with `capability_not_granted`.

The heuristic was mine and the AI implemented it faithfully. Both of us were wrong: word count
is a bad proxy for complexity.

**How I caught it:** smoke-testing every one of the 17 supported questions rather than the
happy ones. It was the only question that failed for a reason unrelated to the data.

Fix: the text-derived tier is a **prior**; the resolved intent is **evidence**. `EscalateFor`
raises the tier once the intent is known. This forced a distinction I should have drawn from
the start — **tier is cost control, role is security** — and `Capabilities.IsSecurityBoundary`
now marks the one capability escalation may never grant.

### 4. Tie detection keyed on a hardcoded column name

The first `CaveatEngine` looked for a column literally named `value`. That worked for ranked
player queries and silently missed the highest-scoring-game tie, whose column is
`total_points`. The response returned both tied games — correct data — with **no tie caveat**,
which is arguably worse than missing the tie entirely: the answer looks confident.

**How I caught it:** I'd predicted the 73–73 tie during recon and expected a caveat. It wasn't
there.

Fix: each certified query **declares** its `RankedValueColumn`. Detection reads the declaration
instead of guessing.

---

## Judgement calls where I overrode AI

**Certified queries over guarded model SQL.** The obvious implementation of this brief is
"validate the model's SQL, then run it". I rejected that after forensics: with five distinct
failure modes in 17 samples, validation can only ever *reject*, never *repair*. It can't turn
`SUM(s.touchdowns)` into the right answer — only into a clean error. Inverting to
intent-classification-plus-reviewed-SQL is what makes the answer 15 instead of a 422.

The tradeoff, stated honestly: certified templates don't generalise to unseen questions. In
production the model would author SQL for the long tail while high-traffic intents run
reviewed templates. That's the shape I'd actually ship.

**Clarify rather than refuse on ambiguity.** Early drafts refused subjective and ambiguous
questions. Refusal is defensible but poor product. `NeedsClarification` with grounded options
is a one-round-trip fix, and separating it from `CannotAnswer` is what makes the difference
meaningful — one says *we need more from you*, the other says *no amount of clarification
helps*.

**Scope discipline against the brief.** Asked to add RBAC, an admin dashboard, Google Vertex AI
and cloud infrastructure, I pushed back on three of the four. The brief lists auth, UI and
deployment under *what we don't care about*, and requires an offline run with no keys. I shipped
RBAC as **tool-grant policy** — genuinely the model-trust problem, and graded under
LLM-in-the-loop robustness — plus two read-only ops endpoints, and wrote the dashboard, Vertex
and cloud topology up as designed seams. Chasing all four would have cost the semantic model
and the evals, which carry the weight.

**Not asking the model for ground truth.** Every golden's expected value came from a query I
wrote. Tempting shortcut: run the pipeline, eyeball the output, freeze it. That produces a
suite that locks in current behaviour instead of correctness — it would have happily
canonicalised 165 for Marcus Bell.

**Reading `SUPPORTED_QUESTIONS.md` as adversarial input.** The file says the model is not
always right. I treated that as a claim to verify rather than accept, checked all 17
interpretations against the schema, and found the specific defects. The three that execute
cleanly and still return wrong numbers were the ones worth finding — nothing flags those.

---

## What I'd tell someone doing this again

1. **Do the forensics first.** The architecture should be a response to evidence. Every good
   decision here traces to something I found in the data before writing code.
2. **Write a golden the moment you make a factual claim.** It's what turns "at least 5" into
   52.
3. **Add diagnostics before you need them.** `resolvedSlots` and `sqlSource` were for
   observability and caught two real bugs.
4. **Query distributions, not aggregates.** `MAX()` hid the clipped column completely.
5. **Test every supported input, not the happy path.** Two of my four bugs only appeared on
   questions I nearly didn't try.
