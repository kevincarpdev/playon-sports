#!/usr/bin/env bash
# Concatenates the whole submission into one markdown file for review by a person or a model
# that cannot browse the repo. Writes review-bundle.md in the package root.
set -euo pipefail

#   ./make-review-bundle.sh          one file, review-bundle.md
#   ./make-review-bundle.sh --split  two smaller files, for tools with upload limits
cd "$(dirname "$0")"

SPLIT=false
[[ "${1:-}" == "--split" ]] && SPLIT=true

OUT="review-bundle.md"

emit() {
  local path="$1" lang="${2:-}"
  [[ -f "$path" ]] || { echo "  skip (missing): $path" >&2; return; }
  {
    printf '\n\n---\n\n## FILE: `%s`\n\n' "$path"
    if [[ -n "$lang" ]]; then
      printf '```%s\n' "$lang"
      cat "$path"
      printf '\n```\n'
    else
      cat "$path"
    fi
  } >> "$OUT"
}

cat > "$OUT" <<'HEADER'
# Review Bundle — SportsQa

A natural-language question-answering service over a high-school sports SQLite dataset, built
as a take-home assessment. The whole submission is concatenated below: documentation first,
then source, then the eval goldens.

**What this system does.** It accepts a plain-English question, asks a (deliberately
imperfect, recorded) LLM to interpret it, and then decides what to do with that interpretation
— validate it, override it, ask a clarifying question, or refuse. The central design decision
is that the model classifies *intent* and surfaces *entities*, while the application owns the
*SQL*.

**Constraints it was built under.** Hard 2–3 hour time box. No live LLM, no network, no API
keys. The fake LLM client and its recorded responses were not editable.

Documentation order below is the recommended reading order.
HEADER

echo "Building $OUT..."

# In split mode the single-file header is discarded and part 1 starts fresh.
$SPLIT && rm -f "$OUT" && OUT="review-part1-docs.md" && cat > "$OUT" <<'H2'
# Review Bundle, part 1 of 2 — Documentation

Design docs and findings. Part 2 carries the source and the eval goldens.
H2

# Docs — reading order matters, architecture first.
for doc in README.md ARCHITECTURE.md SEMANTIC_MODEL.md FINDINGS.md PRODUCTION_NOTES.md \
           AI_NOTES.md PLAN.md ASSESSMENT.md SUPPORTED_QUESTIONS.md data/schema.md; do
  emit "$doc"
done

if $SPLIT; then
  OUT="review-part2-code.md"
  cat > "$OUT" <<'H3'
# Review Bundle, part 2 of 2 — Source and goldens

C# source in dependency order, then the eval goldens and the recorded LLM responses.
Part 1 carries the design docs.
H3
fi

# Application source, in dependency order.
for src in \
  src/SportsQa.Api/Program.cs \
  src/SportsQa.Api/Configuration/SportsQaOptions.cs \
  src/SportsQa.Api/appsettings.json \
  src/SportsQa.Api/Contracts/AskContracts.cs \
  src/SportsQa.Api/Security/Authorization.cs \
  src/SportsQa.Api/Routing/CapabilityRouter.cs \
  src/SportsQa.Api/Data/SchemaCatalog.cs \
  src/SportsQa.Api/Data/DatasetFacts.cs \
  src/SportsQa.Api/Semantics/Slots.cs \
  src/SportsQa.Api/Semantics/IntentCatalog.cs \
  src/SportsQa.Api/Semantics/CertifiedQueries.cs \
  src/SportsQa.Api/Semantics/SlotResolver.cs \
  src/SportsQa.Api/Sql/SqlGuard.cs \
  src/SportsQa.Api/Sql/SqlExecutor.cs \
  src/SportsQa.Api/Quality/CaveatEngine.cs \
  src/SportsQa.Api/Pipeline/QuestionPipeline.cs \
  src/SportsQa.Api/Pipeline/SemanticContextProvider.cs \
  src/SportsQa.Api/Llm/ILlmClient.cs \
  src/SportsQa.Api/Llm/FakeLlmClient.cs \
  src/SportsQa.EvalRunner/Program.cs \
  src/SportsQa.EvalRunner/Golden.cs \
  src/SportsQa.EvalRunner/Verifier.cs \
  src/SportsQa.EvalRunner/Report.cs ; do
  emit "$src" csharp
done

emit "src/SportsQa.EvalRunner/goldens.json" json
emit "src/SportsQa.Api/Llm/fake_llm_responses.json" json
emit "smoke.sh" bash

{
  printf '\n\n---\n\n## Dataset schema (authoritative, from the live database)\n\n```sql\n'
  sqlite3 data/sports.db ".schema" 2>/dev/null || echo "-- sqlite3 unavailable"
  printf '```\n'
} >> "$OUT"

for f in review-bundle.md review-part1-docs.md review-part2-code.md; do
  [[ -f "$f" ]] && printf 'Wrote %-26s %6s  ~%s words\n' \
    "$f" "$(du -h "$f" | cut -f1)" "$(wc -w < "$f" | tr -d ' ')"
done
