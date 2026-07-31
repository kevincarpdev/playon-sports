# Senior Software Engineer, Data — Take-Home Assessment

**Time budget:** 2–3 hours, and it's a hard time box — we'd rather see what you prioritize in 3 hours than everything in 6. The scaffolding exists so your time goes to judgment, not plumbing; leave TODOs where you'd invest more.
**AI use:** Expected and evaluated. Use whatever tools you'd use on the job (Claude Code, Cursor, Copilot, ChatGPT, agents, MCP servers — anything).
**Requirements:** .NET SDK 8 or later. No API keys, no accounts, no network calls needed — everything runs locally.
**Deliverable:** A single ZIP or a public Git repo link containing everything in the *Submission* section below.

---

## The Problem

MaxPreps sits on 20+ years of high school sports data. We're building features that let fans ask questions about that data in plain English and get **accurate** answers back. This exercise is a miniature of that system.

You get:

- `data/sports.db` — a small SQLite dataset (two sports, one season each). See `data/schema.md`.
- `src/` — a runnable .NET solution: an API with a working `/health` endpoint, a stubbed `POST /ask` endpoint, and an `EvalRunner` console project.
- A **recorded fake LLM** (`FakeLlmClient`) behind an `ILlmClient` interface. It deterministically "interprets" the 17 questions in `SUPPORTED_QUESTIONS.md` into SQL. **Like a real model, it is not always right.** Some of its interpretations are flawed in realistic ways. Which ones, and how your system copes, is the exercise. Don't edit the fake or its recorded responses.

There is deliberately no live model: everything you'd want a real LLM for is either recorded (the interpretations) or is an artifact you author (the semantic model). This keeps the exercise free, deterministic, and runnable anywhere — and it means your validation and eval work is what we can see.

## Part 1 — The `/ask` endpoint

Implement `POST /ask` in `SportsQa.Api`:

1. Accept `{ "question": "..." }`.
2. Get the model's interpretation via `ILlmClient.InterpretAsync(question, semanticContext)` — pass your Part 2 artifact here; the fake ignores it, but we read the call site.
3. Decide what to do with it. How much do you trust it? What do you validate before executing SQL from a model against your database? What happens when the SQL is wrong, references things that don't exist, or the question can't be answered from this data at all?
4. Return a **structured** response of your own design. It must distinguish answered / cannot-answer / error outcomes, carry the data (not raw model prose), and never surface an unhandled exception. Whether and how you expose confidence and caveats is a design decision we want to see you make.

Restructure the project however you like — the skeleton's shape is not a constraint.

## Part 2 — The semantic model

Author `SEMANTIC_MODEL.md` (or `.yaml` — your call): the description of this dataset you would hand to a **real** LLM so it can query the data accurately. Tables, relationships, grain, metrics, dimensions, synonyms, and — most importantly — the caveats and sharp edges a model would need to know to avoid confidently wrong answers.

The fake client ignores this artifact at runtime. We don't. **It's the single most heavily weighted deliverable**, because it shows whether you can look at unfamiliar data and build the contract that makes LLM access to it trustworthy. Write it for the model, not for us — it should be directly usable as system-prompt/tool context.

## Part 3 — The eval harness

Make `EvalRunner` real:

- **At least 6 goldens** drawn from the supported questions (format is yours; `goldens.example.json` has two verified examples). Include at least one question you believe the model handles *incorrectly* and at least one that *cannot* be answered from this data.
- Expected values must come from the **data** — your own independent queries — never from the model's answers.
- Runs end-to-end (against the API or the pipeline directly), prints a per-golden pass/fail report with expected vs actual, and exits non-zero on failure so it could gate CI.

## Part 4 — Writeups

- `FINDINGS.md` — What you learned about this data and this model: data-quality issues you found, model interpretations you distrust and why, ambiguities you hit and the decisions you made. If you found something we didn't plant, say so — that's the good stuff.
- `AI_NOTES.md` — How you actually used AI on this exercise: tools and models, at least one prompt that worked and why, at least one place AI was wrong and how you caught it, and any judgment calls where you overrode it.

## Optional flourish (unscored)

If you want, add a real `ILlmClient` implementation behind an environment variable (your own key, any provider, or a local model). It must stay optional: the graded path is the fake client, and the submission must run fully without a key. We will not grade the real-model path — it exists purely if you want to show us something.

## What we care about

- **Semantic modeling judgment** — does your model of the data capture grain, semantics, and sharp edges, or just restate the schema?
- **Eval rigor** — do your goldens come from the data, cover failure classes, and would they actually catch a regression?
- **LLM-in-the-loop robustness** — is the model treated as an untrusted collaborator, with validation and graceful failure?
- **Code craft** — clear structure, honest error handling, idiomatic for the language you're strongest in (we know .NET may be new for some candidates; clean, well-reasoned code matters more than fluent C# idiom).

## What we don't care about

Auth, deployment, UI, containerization, exhaustive test coverage of the plumbing, or answering all 17 questions perfectly. A submission that handles 10 questions brilliantly beats one that handles 17 blandly.

## Submission

```
your-submission/
├── src/                  # the solution, with your /ask implementation and EvalRunner
├── data/                 # unchanged from this package — include it so your submission runs
├── SEMANTIC_MODEL.md     # or .yaml
├── FINDINGS.md
├── AI_NOTES.md
└── README.md             # exact commands to build, run the API, and run the evals
```

Verify before sending: `dotnet build` is clean, the API starts, `/health` returns ok, your evals run with one command.
