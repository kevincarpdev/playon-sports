#!/usr/bin/env bash
# Builds a small, task-specific bundle for a reviewer that should look at one thing hard.
# Whole-submission review is make-review-bundle.sh; this is for targeted passes.
#
#   ./make-focus-bundle.sh semantics   is the data contract real, or a restated schema?
#   ./make-focus-bundle.sh security    adversarial pass on the SQL guard
#   ./make-focus-bundle.sh evals       would these goldens catch a regression?
set -euo pipefail
cd "$(dirname "$0")"

FOCUS="${1:-}"
API=src/SportsQa.Api
EVAL=src/SportsQa.EvalRunner

case "$FOCUS" in
  semantics)
    OUT=focus-semantics.md
    BRIEF="Judge whether the semantic model is a genuine data contract or a restated schema."
    FILES=(SEMANTIC_MODEL.md data/schema.md SUPPORTED_QUESTIONS.md FINDINGS.md
           "$API/Llm/fake_llm_responses.json" "$API/Semantics/CertifiedQueries.cs")
    ;;
  security)
    OUT=focus-security.md
    BRIEF="Adversarial review of the SQL validation and authorization boundary."
    FILES=(data/schema.md "$API/Sql/SqlGuard.cs" "$API/Sql/SqlExecutor.cs"
           "$API/Security/Authorization.cs" "$API/Routing/CapabilityRouter.cs"
           "$API/Pipeline/QuestionPipeline.cs")
    ;;
  evals)
    OUT=focus-evals.md
    BRIEF="Judge whether these goldens would catch a real regression."
    FILES=(data/schema.md "$EVAL/goldens.json" "$EVAL/Verifier.cs" "$EVAL/Golden.cs"
           "$EVAL/Program.cs" FINDINGS.md)
    ;;
  *)
    echo "usage: $0 {semantics|security|evals}" >&2
    exit 2
    ;;
esac

{
  printf '# Focus bundle — %s\n\n%s\n\n' "$FOCUS" "$BRIEF"
  printf 'Context: a natural-language question-answering service over a high-school sports\n'
  printf 'SQLite dataset, built as a take-home against a deliberately imperfect *recorded* LLM\n'
  printf 'whose responses were not editable. The model classifies intent and surfaces entities;\n'
  printf 'the application owns the SQL via reviewed templates. Only the files relevant to this\n'
  printf 'review are included.\n'
} > "$OUT"

for f in "${FILES[@]}"; do
  [[ -f "$f" ]] || { echo "  skip (missing): $f" >&2; continue; }
  lang=""
  case "$f" in *.cs) lang=csharp ;; *.json) lang=json ;; esac

  {
    printf '\n\n---\n\n## FILE: `%s`\n\n' "$f"
    [[ -n "$lang" ]] && printf '```%s\n' "$lang"
    cat "$f"
    [[ -n "$lang" ]] && printf '\n```\n'
  } >> "$OUT"
done

printf 'Wrote %-24s %6s  ~%s words\n' \
  "$OUT" "$(du -h "$OUT" | cut -f1)" "$(wc -w < "$OUT" | tr -d ' ')"
