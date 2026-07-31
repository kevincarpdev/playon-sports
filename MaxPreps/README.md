# SportsQa — grounded natural-language questions over high-school sports data

Ask questions in plain English; get answers that are **correct, honestly qualified, or
explicitly refused** — never confidently wrong.

## Quick start

Requires the .NET SDK 8 or later. No API keys, no accounts, no network calls.

Check the SDK is on your PATH:

```bash
dotnet --version
```

If that says `command not found` but you have the SDK installed to the default per-user
location, add it to your PATH:

```bash
export PATH="$HOME/.dotnet:$PATH"
```

**Every command below runs from this directory** — the package root, containing `src/` and
`data/`. Each block is independent, so paths don't chain.

Build:

```bash
dotnet build src
```

Run the evals — the single most informative command. It runs the pipeline in-process, so no
server needs to be running:

```bash
dotnet run --project src/SportsQa.EvalRunner
```

Exits `0` when all 20 goldens pass, non-zero otherwise, so it gates CI.

Run the API:

```bash
dotnet run --project src/SportsQa.Api
```

Health check, from another terminal:

```bash
curl http://localhost:5000/health
```

Test the HTTP surface — every outcome, status code and authorization boundary. Needs the API
running:

```bash
./smoke.sh
```

The evals prove the *answers* are right; the smoke test proves the *endpoint* behaves. Both
exit non-zero on failure.

Both run in CI on every pull request ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)),
along with a step that corrupts a golden on purpose to prove the suite can still fail.

### If port 5000 is unavailable

Two common causes on macOS. Check what holds it:

```bash
lsof -nP -iTCP:5000 -sTCP:LISTEN
```

- **`ControlCe`** — Control Center's AirPlay Receiver squats on port 5000. Either disable
  AirPlay Receiver in System Settings → General → AirDrop & Handoff, or use another port.
- **`SportsQa.Api`** — you already have an instance running in another terminal. Reuse it, or
  stop it with `pkill -f SportsQa.Api`.

To use a different port, pass it to both the API and the smoke test:

```bash
dotnet run --project src/SportsQa.Api --urls http://localhost:5099
```

```bash
BASE=http://localhost:5099 ./smoke.sh
```

## Try it

```bash
curl -s -X POST http://localhost:5000/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"How many touchdowns did Tony Jackson score this season?"}'
```

Answers **15**, even though the model's SQL references a `touchdowns` column that doesn't
exist. The response records that it discarded the model's query and why.

An ambiguous question asks instead of guessing:

```bash
curl -s -X POST http://localhost:5000/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"Did Riverside beat Oak Hill this season?"}'
```

Returns `needs_clarification` with both sports as options — because the honest answer differs
by sport. Send the slot back to close the loop:

```bash
curl -s -X POST http://localhost:5000/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"Did Riverside beat Oak Hill this season?","slots":{"sport":"Football"}}'
```

Oak Hill won 24–21 — so Riverside did **not**. Ask the same question with
`"sport":"Basketball"` and Riverside won both meetings. A single yes/no would have been wrong
half the time.

Role-scoped access (per-game stat lines are a paid surface):

```bash
curl -s -X POST http://localhost:5000/ask \
  -H 'Content-Type: application/json' \
  -H 'X-SportsQa-Role: Anonymous' \
  -d '{"question":"How many touchdowns did Tony Jackson score this season?"}'
```

Internal operations (Admin only; other roles get 404, so the surface isn't discoverable):

```bash
curl -s -H 'X-SportsQa-Role: Admin' http://localhost:5000/admin/rollup-freshness
```

Reports the two `player_season_totals` rows the nightly job left stale — the live data-quality
defect in this dataset.

## The `/ask` contract

Request: `{ "question": "...", "slots": { "sport": "Football" } }` — `slots` carries
previously-answered clarifications.

Response outcomes:

| Outcome | HTTP | Meaning |
|---|---|---|
| `Answered` | 200 | Validated SQL ran. May carry `caveats[]`. |
| `NeedsClarification` | 200 | A required slot is missing or ambiguous. Recoverable. |
| `CannotAnswer` | 422 | The data can't support it. Clarifying won't help. |
| `Error` | 500 | Correlation id, no internals. Never a stack trace. |

Reported `confidence` is **ours**, derived from validation and result shape — deliberately not
the model's self-reported number, which we've observed at 0.88 on a query against a
nonexistent table.

## Documentation

| Document | What's in it |
|---|---|
| [ARCHITECTURE.md](ARCHITECTURE.md) | **Start here.** Component-by-component overview, request flow, review checklist. |
| [SEMANTIC_MODEL.md](SEMANTIC_MODEL.md) | The data contract, written as system-prompt context for a real model. Grain, metrics, join paths, and nine sharp edges. |
| [FINDINGS.md](FINDINGS.md) | What's wrong with this data and this model, verified with SQL. |
| [PRODUCTION_NOTES.md](PRODUCTION_NOTES.md) | How this behaves on PlayOn's real data — football, then track & field — plus Postgres and fuzzy search. |
| [AI_NOTES.md](AI_NOTES.md) | How I used AI, including four places it was wrong and how I caught them. |
| [PLAN.md](PLAN.md) | The plan written before implementation, after data recon. |

## How it works, in one paragraph

The model classifies **intent** and surfaces **entities**; the semantic layer owns the **SQL**.
Five of the 17 recorded model interpretations are broken in five different ways, and three more
execute cleanly while returning wrong numbers — so validation alone can only reject, never
repair. Instead, recognised intents run reviewed query templates that handle each documented
sharp edge once and correctly. Model SQL is the guarded fallback for unrecognised intents,
validated against the live schema and the caller's role before it runs. Full reasoning in
[ARCHITECTURE.md](ARCHITECTURE.md).

## Configuration

Everything tunable lives under `SportsQa` in
[`appsettings.json`](src/SportsQa.Api/appsettings.json) — row caps, timeouts, confidence
thresholds, routing keywords, default role. No thresholds are hardcoded in the pipeline.

## Layout

```
src/SportsQa.Api/
  Configuration/   all tunables, bound from appsettings
  Contracts/       request and response shapes
  Security/        roles, principals, role grants
  Routing/         capability router (tier → tool grant)
  Data/            live schema catalog, entity lexicon, coverage facts
  Semantics/       intent catalog, slots, certified query templates, slot resolver
  Sql/             static guard, bounded executor
  Quality/         caveat engine
  Pipeline/        orchestrator, semantic context provider
  Llm/             ILlmClient and the recorded fake (unmodified)

src/SportsQa.EvalRunner/
  goldens.json     20 goldens across 15 failure classes, each with its groundTruthSql
```

`data/` is unchanged from the original package.
