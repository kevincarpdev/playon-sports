# Production Notes, Applying This to PlayOn's Real Data

This exercise is a 16-team, two-sport slice. PlayOn's actual surface is MaxPreps and NFHS
across every state, every sanctioned sport, and 20+ years of history. This document is the
bridge: which findings from the toy dataset are *the same problem* in production, what breaks
that the toy data cannot show, and what I'd own if this were the job.

Framed for the role as I understand it, modernising the data and tech stack end to end, on
Postgres, with grounded AI as a consumer of that stack rather than a bolt-on.

---

## 1. The thesis

**The semantic layer is the product, not the chatbot.**

A text-to-SQL system's accuracy ceiling is set by how well the data's meaning is written down,
not by model quality. Everything I found in `sports.db`, stale rollups, clipped stats,
untracked positions, ambiguous entities, sport-dependent answers, is a *semantics* problem.
None of it is fixed by a better model. All of it is fixed by a contract the model must obey
plus a validation layer that assumes the model won't.

That contract has a second consumer, and this is the part I think is underexploited: **the same
entity lexicon that fills a chatbot's slots is what site search should be using for
autocomplete.** One artifact, two products. §4.

---

## 2. Football at real scale

Football first because it's the flagship and because every structural problem shows up there.

### 2.1 The `teams` grain explodes, and this is already the toy dataset's #1 trap

In `sports.db`, `teams` is one row per school × sport: 16 rows, 10 schools. That alone made
"how many teams" (16) and "how many schools" (10) different questions.

Production grain is at minimum:

```
school × sport × season × level × gender
```

A single high school in one football season fields varsity, JV, and freshman teams. Add girls
flag football, now a growth sport in many states, and gender is load-bearing rather than
implied.

**What breaks:** "How many wins does Mater Dei have?" is unanswerable without `level`. A naive
query sums varsity and JV into a meaningless number. Our toy data has exactly this shape at
smaller scale, which is why `sport` is already a required slot.

**What our architecture does:** `level` and `gender` become required slots resolved from
context or clarified, with the same machinery as `sport`. `DatasetFacts` already reads
coverage from the data instead of hardcoding it, so the option lists stay correct as coverage
changes.

### 2.2 Schools are not stable identifiers

Real complications, none of which exist in the toy data:

- Schools **rename** (rebrandings, consolidations).
- Schools **merge and split**.
- **Co-op teams**: two or three small schools field one team. The team maps to N schools.
- **Prep schools and national programs** sit outside state structures, MaxPreps carries a
  "Prep Schools" bucket alongside the 50 states plus DC.

**What breaks:** any query keyed on `school` text. Our certified templates match
`teams.school = $school`, which is fine for 10 schools and catastrophic for 20 years of
renames, "how many titles has this program won" silently splits across two names.

**Fix:** a `program` entity with stable surrogate ID, and `school_name` as a versioned
attribute with validity dates. Entity resolution targets `program_id`, never a display string.
This is the single highest-value schema change I'd push for.

### 2.3 Wins are not `home_score > away_score`

Our `TeamWins` template computes exactly that. It is correct for football in this dataset, which
has no drawn games and no forfeits, but note that **four basketball games here are tied**
(FINDINGS §1.7), so `>` already silently drops results rather than counting them as draws. In
production it is worse:

- **Forfeits.** A team can win without playing; the score may be 0–0, 1–0, or 2–0 by state
  convention.
- **Vacated wins.** Ineligible-player rulings retroactively flip results, sometimes months
  later.
- **Ties.** Legal in football in some states historically, so `>` silently drops them.
- **Overtime.** Affects records and margin analysis.

**Fix:** an explicit `result` enum (`win` / `loss` / `tie` / `forfeit_win` / `forfeit_loss` /
`vacated`) on a team-game fact, and never derive outcome from scores in a certified query. The
toy data let me take the shortcut. Production would not.

### 2.4 Coach-entered stats make the fact table retroactively mutable

This is our stale-rollup finding (FINDINGS §1.1), one level worse.

MaxPreps statistics are largely **entered by coaches and team statisticians**, which means:

- Stats arrive **late** and **incomplete**, a Friday game may not be entered until Monday.
- Stats are **corrected** after publication. The fact table itself changes retroactively.
- Entry quality varies by program. Some schools never enter defensive stats.
- **Typos** produce impossible values, a 700-yard rushing game from a misplaced digit.

In our toy data the *rollup* was stale while the fact table was truth. In production **both**
move.

**What our architecture does right:** answers carry a scope string and caveats, and
`/admin/rollup-freshness` compares `updated_at` against the last game of the season. That
generalises directly into a freshness SLO per (sport, season, state).

**What it needs to add:**

1. **As-of semantics.** Every answer states the data's as-of timestamp. "Leading rusher in
   Texas" is a different answer Friday night and Monday morning, and users need to know which
   they got.
2. **Verification status** as a first-class dimension, verified vs coach-reported vs
   unverified, surfaced as a caveat, exactly like `tied_result`.
3. **Plausibility bounds** in the semantic model. Our clipped-rebounds finding (FINDINGS §1.3)
   is the friendly version. The production version is a typo'd outlier winning a leaderboard.
   Bounds turn that into a caveat instead of a headline.

### 2.5 Classification and division make cross-comparisons invalid

Every state organises differently: Texas UIL runs 6A–1A; California CIF has sections and
divisions; other states use enrollment-based classes.

**What breaks:** "Who's the best team in the country?" and even "best team in California"
without a division. This is *structurally identical* to our cross-sport `points` rule: a
number that is arithmetically computable and semantically meaningless.

**What our architecture does:** the same refusal-or-clarify path. Comparing a 6A program to a
1A program is the football version of adding basketball points to football points, and it gets
the same treatment, a required slot, or a caveat naming the scope.

### 2.6 Athletes transfer, and our schema cannot express it

`players.team_id` is a single FK, one player, one team, forever. In production athletes
transfer mid-season, play two sports, and repeat grades. MaxPreps tracks athletes with career
IDs precisely because an athlete outlives any one roster.

Our `player_season_totals` PK of `(player_id, season)` already can't hold a two-sport athlete
(FINDINGS §1.10). Transfers make it worse: one athlete, one season, two schools.

**Fix:** an `athlete` entity plus an `enrollment` fact (`athlete_id`, `team_id`, `date_from`,
`date_to`). Stats join through enrollment, so "his numbers at his previous school" becomes
expressible rather than impossible.

### 2.7 Ties stop being an edge case

Our toy data already has a 52-way tie on rebounds and a 2-way tie on highest-scoring game.
Across every football program in the country, ties on integer stats are **guaranteed**,
hundreds of athletes will share "3 touchdowns in a game".

`LIMIT 1` is not a minor bug at that scale. It is a permanently wrong answer generator. The
`DENSE_RANK()` approach in `CertifiedQueries` is the only defensible shape, and the
`tied_result` caveat becomes a routine part of answers rather than an exception.

---

## 3. Track & field, where a naive text-to-SQL system fails hardest

Football stresses the *grain*. Track & field breaks the **measurement model** outright, and
it's the best argument I can make for why the semantic layer has to carry unit and legality
metadata rather than just column names.

### 3.1 A "mark" is not a number

Our entire `Metric` abstraction assumes a comparable integer and that **higher is better**.
Track & field violates both immediately:

| Event type | Measure | Better is | Example format |
|---|---|---|---|
| Sprints (100m, 200m, 400m) | time | **lower** | `10.41` |
| Middle distance (800m, 1600m) | time | **lower** | `1:52.34` |
| Distance (3200m) | time | **lower** | `9:14.88` |
| Hurdles (110H, 300H) | time | **lower** | `13.82` |
| Jumps (high jump, long jump) | distance/height | higher | `6-08`, `23-01.50` |
| Throws (shot, discus, javelin) | distance | higher | `61-04` |
| Relays (4x100, 4x400, 4x800) | time | **lower** | `3:18.44` |

Three failures fall straight out:

1. **Direction.** `ORDER BY value DESC` returns the *slowest* runner as the leader. This is the
   single most likely production bug in any naive implementation, and it looks plausible.
2. **Format.** `1:52.34` is not a number. Stored as text it sorts lexically,
   `"1:52.34" < "9.88"`, so a mile time beats a 100m dash. Stored as a float it needs
   normalising to seconds, with the display format preserved separately.
3. **Imperial marks.** `6-08` means 6 feet 8 inches. Naive numeric parsing reads `6`, or
   subtracts.

**Direct consequence for this codebase:** `Metric` needs a direction and a unit. That gap is
real enough that I closed it, `Metric.Direction` now exists and `TopByMetric` orders by it,
rather than assuming `DESC`. It's a one-line change in the toy dataset and a correctness
requirement the moment track & field lands.

### 3.2 A mark can be legal or illegal, and the flag is not optional

**Wind.** In sprints and horizontal jumps, a tailwind over **+2.0 m/s** makes a mark
wind-aided: legal for competition, ineligible for records. A 10.32 with +3.1 wind is not a
school record no matter what the number says.

**Timing method.** Fully Automatic Timing versus hand timing are not comparable; the
conventional adjustment for hand-timed sprints is roughly +0.24s. Comparing across methods
without noting it is exactly the cross-sport-points error in a new costume.

**Rounds.** Prelims, semis, finals. A personal best may be set in a prelim, and "won the
event" refers only to the final.

**What our architecture does:** these become `Caveat` codes and refusal reasons. The machinery
already exists and ships, `CaveatEngine` emits `tied_result`, `uneven_schedules`,
`sport_scoped`, `truncated` and `no_matching_rows` today, so `wind_aided`, `hand_timed` and
`set_in_prelim` would be new codes in an existing mechanism rather than a new mechanism. The
lesson transfers exactly: the model must never be the thing that remembers a legality rule.

### 3.3 Indoor and outdoor are separate universes

Separate record books, and partly separate events, 60m indoor has no outdoor equivalent;
banked versus flat 200m tracks are not comparable. `season` therefore needs a `environment`
dimension, and "school record in the 200" is ambiguous until it's supplied. Another required
slot, same machinery as `sport`.

### 3.4 Relays have a grain that is neither player nor team

A 4x400 mark belongs to **one team, one race, and exactly four athletes in a specific order**.
Our schema has `player_game_stats` (per player per game) and nothing else. A relay mark is a
team fact with an ordered athlete bridge, a genuinely new grain, not a variation.

This is worth stating plainly: the toy schema's two grains (per-player-per-game,
per-player-per-season) do not span PlayOn's real sports. Cross country needs team scoring by
finishing place. Wrestling needs bracket progression. Swimming has relays and splits. A data
architecture that assumes football's shape will need rebuilding, not extending.

### 3.5 Event names need aliasing, not string matching

`1600m`, `Mile`, `1500m` are three different distances that fans use interchangeably. `3200m`
and `2 Mile` likewise. They are *convertible* but **not equal**, and treating them as equal is
a silent semantic error, same category as our stale rollup: right-looking number, wrong
question answered.

Implement as an event dimension with canonical ID, aliases, exact distance, and explicit
conversion factors flagged as approximate. Never as `LIKE '%mile%'`.

---

## 4. Fuzzy search, one lexicon, two products

The highest-leverage thing I noticed, and it applies to the live site rather than just the
chatbot.

### 4.1 The insight

`SchemaCatalog` already builds an entity lexicon (players, schools, cities) from the data and
resolves question text against it, including two refinements I had to add:

- **Subsumption**, "Tony Jackson" beats "Jackson" when the longer name is present.
- **Shadowing.** A bare "Jackson" refuses to auto-resolve, because two longer names contain
  it.

That is **entity linking**, and it is precisely what site search autocomplete needs. Today
those are two separate systems in most products. They should be one service with two
consumers: the search box ranks candidates for a human, the slot resolver ranks candidates for
a model. Same index, same aliases, same disambiguation rules, same popularity signal.

Concretely, the shadowing rule is why typing "Jackson" into search should offer *Jackson Prep*,
*Tony Jackson*, and *Jackson, GA* as distinct grouped results rather than guessing, the exact
behaviour our `NeedsClarification` response produces for the chatbot.

### 4.2 Postgres implementation

The migration off SQL Server makes this straightforward, this is native Postgres territory:

```sql
CREATE EXTENSION pg_trgm;    -- trigram similarity, typo tolerance
CREATE EXTENSION unaccent;   -- accent-insensitive matching

CREATE MATERIALIZED VIEW search_entity AS
SELECT entity_kind, entity_id, display_name,
       unaccent(lower(display_name)) AS normalised,
       state, sport, level, gender,
       follower_count                          -- popularity, for ranking and tie-breaking
FROM ( /* programs UNION athletes UNION teams UNION events */ ) e;

CREATE INDEX search_entity_trgm
  ON search_entity USING gin (normalised gin_trgm_ops);
```

- `word_similarity()` for prefix-ish autocomplete as the user types.
- `similarity()` with a threshold for typo tolerance, "Riverisde" → Riverside.
- A curated **alias table** for what trigrams cannot reach: "Bosco" → St. John Bosco,
  "SJB" → St. John Bosco, "Mater" → Mater Dei. Nicknames are how fans actually search.
- `follower_count` breaks ties toward the entity the user probably meant, MaxPreps already
  has this signal in "most followed" data.
- Facet columns let search scope itself (`state`, `sport`, `level`), which is the same slot
  set the chatbot fills.

### 4.3 Why this matters commercially

Better entity resolution improves three things at once from one investment: search
conversion on the site, chatbot groundedness, and internal analytics joins. It's also the
cheapest possible fix for the ambiguity class of wrong answers, a clarification round-trip
costs one request; a confidently wrong answer costs trust.

---

## 5. Postgres, cost, speed, and where our patterns land

Context from our conversation: PlayOn moved off traditional SQL Server to Postgres, and the
motivation was cost and performance. That shapes where I'd put things.

### 5.1 Mapping this codebase over

Deliberately thin, because the abstraction boundaries hold:

| Concern | Here (SQLite) | Postgres |
|---|---|---|
| Read-only | `Mode=ReadOnly` | read-replica DSN + `default_transaction_read_only` |
| Row cap | enforced while reading | keep it; add `statement_timeout` |
| Ranking | `DENSE_RANK()` | identical, window functions are the same |
| Parameters | `$name` | `$1` positional, or Npgsql named |
| Schema catalog | `pragma_table_info` | `information_schema.columns` |
| Freshness check | `date()` comparison | `::date`, or a materialized-view refresh log |

`SchemaCatalog` reads the live catalog rather than hardcoding, so it ports by swapping one
query. That was the point of building it that way.

### 5.2 Where the cost savings actually are

1. **Partition the fact table by season.** 20 years of stat lines with almost all queries
   scoped to one season is the textbook case. Declarative partitioning turns full scans into
   single-partition reads.
2. **BRIN indexes on `game_date`.** Append-mostly, naturally date-ordered, and vastly cheaper
   than btree at this size.
3. **Materialized views replace the nightly rollup job.** `player_season_totals` is exactly a
   materialized view, and `REFRESH MATERIALIZED VIEW CONCURRENTLY` removes the staleness
   window that produced our headline bug. The refresh log then *is* the freshness SLO.
4. **Read replicas for the AI path.** Model-generated queries are unpredictable; they should
   never contend with transactional writes. This is also why the row cap and timeout are
   non-negotiable.
5. **`pg_stat_statements` as a regression detector.** When a model or template change starts
   emitting an expensive plan, that's where it shows up first.

### 5.3 Row-level security is the right home for our role grants

`SqlGuard` enforces a per-role table allow-list in the application. That's correct as defence
in depth, but in Postgres the boundary belongs in the database:

```sql
ALTER TABLE athlete_game_stat ENABLE ROW LEVEL SECURITY;

CREATE POLICY subscriber_reads_stats ON athlete_game_stat
  FOR SELECT TO sportsqa_subscriber USING (true);
-- no policy for sportsqa_anonymous: per-game stat lines are a paid surface
```

Then even a bug in the guard cannot leak paid data, because the connection's role cannot see
it. Application-layer validation stays for fast, friendly refusals; RLS is the thing that is
actually true.

---

## 6. What I'd own, in order

If this were the mandate, modernising the data architecture with grounded AI on top:

**Foundation (the unglamorous part that everything else depends on)**

1. **Stable entity IDs.** `program_id`, `athlete_id`, `event_id`, with names as versioned
   attributes. Nothing downstream is trustworthy until identity is.
2. **Explicit grain contracts** per fact table, written down and enforced in CI. Every wrong
   answer I found in this exercise traces to an unstated grain.
3. **Outcome as data, not derivation**, the forfeit/vacated problem (§2.3).

**Trust**

4. **Freshness and verification as first-class dimensions**, surfaced in every answer.
   Generalises `/admin/rollup-freshness` into a monitored SLO per sport, season, and state.
5. **Plausibility bounds** on coach-entered stats, so typos become caveats not headlines.
6. **The semantic model as a versioned, reviewed artifact.** It's a prompt payload here; it
   should be a build artifact with goldens gating changes, a semantic-model edit that
   regresses an answer should fail CI exactly like a code change.

**Leverage**

7. **The unified entity service** (§4), one index behind site search and AI slot filling.
8. **Postgres physical design** (§5.2), partitioning, BRIN, materialized rollups, replicas.
9. **RLS for tiering** (§5.3).

**AI**

10. **Certified query library per sport**, extending `CertifiedQueries`. The model classifies
    and disambiguates; reviewed SQL executes. This is the pattern that scales, because a
    reviewed template is auditable and a model's SQL is not.
11. **Eval suite per sport**, gating deploys, the `EvalRunner` pattern, with goldens derived
    from data and covering failure classes rather than happy paths.

---

## 7. AI in the delivery pipeline, not just in the product

Most of this document is about AI as a *feature*. This section is about AI as *engineering
practice*, because at the scale of a data-platform rebuild that is where the leverage is.

### The semantic model is code, so it gets a code's pipeline

The most dangerous change in this system is not a code change. It is someone editing
`SEMANTIC_MODEL.md`, redefining a metric, relaxing a season rule, adding a synonym, because
it silently alters what every answer *means* with no compile error and no test failure.

That is why the goldens run in CI (`.github/workflows/ci.yml`). A semantic-model edit that
changes an answer fails the merge. Six gates, all offline:

1. Build clean.
2. The 55 unit tests pass: every known `SqlGuard` bypass, plus hostile-input coverage
   (prompt injection, jailbreaks, SQL injection through every slot, privilege escalation).
3. The 24 goldens pass, with expected values derived from the data rather than from the system.
4. **A deliberately corrupted golden must fail.** This is the one people skip. A suite that
   cannot go red gates nothing, and it degrades silently, so the pipeline proves its own
   teeth on every run.
5. The API starts and reports healthy.
6. `smoke.sh` passes over real HTTP, covering every outcome and authorization boundary.

At PlayOn scale this becomes per-sport golden suites, each gating its own certified query
library, with the freshness and verification dimensions (§2.4) asserted as part of the suite
rather than checked by hand.

### Where an AI reviewer actually earns its keep

Generic "AI reviews your PR" produces comments engineers learn to scroll past. The version
worth running asks one question a human reviewer is genuinely bad at:

> Does this diff change what an answer *means*, grain, metric definition, season scoping, tie
> handling, or role reach, without a corresponding golden?

That is a semantic-drift detector, and it is checkable. It has the context a reviewer lacks
(the whole semantic model) and it is looking for one thing, so its output stays low-volume
enough to be read. The job exists in the workflow, scoped to exactly that prompt, and is
skipped unless a key is configured, the graded path never depends on it.

Two more worth wiring at scale, both narrow for the same reason:

- **Plausibility triage on ingest.** Coach-entered stats (§2.4) produce impossible values. A
  cheap model scoring incoming stat lines against per-sport bounds catches the 700-yard rushing
  game before it wins a leaderboard.
- **Alias mining for search.** The nicknames fans type ("Bosco", "SJB") are exactly what
  trigram matching misses (§4.2). Mining query logs for terms that fail to resolve, then
  proposing aliases for human approval, is a good use of a model: it generates candidates and a
  person decides.

Note the shape both share. The model **proposes**, a deterministic system **decides**. That is
the same posture as the runtime architecture, applied to the toolchain.

### Multi-model review, matched to strengths

For anything high-stakes, one model's opinion is one model's blind spots. The review of this
submission was split by failure class, each model given the slice it is strongest on and a
prompt written to attack rather than assess, verification on the most persistent model,
bypass-hunting on the code-specialised one, architectural judgement on a model from a
different training lineage than the one that helped write it. `AI_NOTES.md` has the matrix.

The principle generalises to production: route by task, keep prompts narrow, and never let the
model that produced an artifact be the only one that reviews it.

### Authoring discipline

AI wrote most of the code in this submission. It did not choose the architecture.

Component boundaries were decided first, then each component written against a stated contract,
one at a time. The alternative, a broad prompt for a whole subsystem, yields a design nobody
selected, and by the third such prompt it has drifted somewhere unowned. Narrow prompts against
chosen boundaries keep every file a reviewable unit, which is why the bugs found during this
build were all local and cheap to fix.

For a platform rebuild this is the difference between AI as an accelerant and AI as technical
debt with good grammar.

## 8. Honest limits of this document

I read the public MaxPreps football and track & field sections to ground the sport-specific
structure (state and season faceting, stat leader categories, the individual-versus-team
distinction, event types). I have **not** seen PlayOn's actual schema, so §2 and §3 describe
problems the domain guarantees, not defects I've observed in their systems. The Postgres
context in §5 comes from our conversation rather than independent verification.

The parts I'd stand behind hardest are the ones I proved on the data in this exercise:
grain ambiguity, rollup staleness, tie handling, entity ambiguity, and untracked-versus-zero.
Those are not scale-dependent. They're the same bugs at every size, and they're the reason the
semantic layer has to be an artifact rather than a convention.
