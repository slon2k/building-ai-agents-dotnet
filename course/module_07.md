# Module 7 — Evaluations, Diagnostics and Engineering Review

[Course Index](README.md) | [Previous: Safe Actions](module_06.md)

## Module purpose

Turn the tests, scenarios and traces accumulated throughout the course into a repeatable way to assess the assistant.

We will measure behavior, investigate failures and compare a controlled change against a baseline.

---

## Module overview

**Estimated effort:** 3–5 hours, split across sessions if needed.

**Main question:**

> How do we know whether a prompt, tool description or model change improves the application without introducing regressions?

### Working result

A reference project with:

- A repeatable evaluation runner.
- Separate deterministic tests and live behavioral evaluations.
- Isolated evaluation fixtures.
- Reports covering correctness, safety, latency and usage.
- Trace-linked failure analysis.
- One documented baseline-versus-candidate comparison.
- A README describing architecture, operation and limitations.

A small evaluation suite provides regression evidence—not proof that the agent behaves correctly in every situation.

---

## Prerequisites

Module 6 should be complete:

- A bounded, observable agent loop.
- Exact and semantic retrieval.
- Persistent, isolated sessions.
- Context budgets and summaries.
- Session-owned action proposals.
- Application-enforced approval.
- Atomic mutation and audit recording.
- Tests and evaluation scenarios from earlier modules.

Use synthetic data and a separate evaluation database.

---

## Learning objectives

By the end of this module, you should be able to:

1. Distinguish deterministic tests, behavioral evaluations and retrieval evaluations.
2. Define observable success criteria for agent tasks.
3. Build reproducible evaluation fixtures.
4. Measure variability across repeated model runs.
5. Use traces to locate failures.
6. Compare configurations without changing several variables at once.
7. Document what the evaluation evidence does—and does not—support.

---

## Concepts and trade-offs

An evaluation result depends on its fixture, assertions and configuration. Reproducible inputs make comparisons interpretable; they do not make live model outputs deterministic.

### Worked result interpretation

Suppose a scenario succeeds in four of five repetitions. The fifth run reaches its execution budget. Report 4/5 task success, one budget-exhausted run and the range of execution counts. Do not omit the failed run and report 100% success.

If no unauthorized mutation occurs in those five runs, report zero observed safety violations separately. Neither result establishes a universal reliability or safety guarantee. Inspect the failed run and add a deterministic regression test if it reveals a code defect.

---

## Evaluation strategy

### Three complementary layers

| Layer | What it checks | Typical execution |
| --- | --- | --- |
| Deterministic tests | Validation, orchestration, isolation, budgets and approval enforcement | Local and CI; fake model responses |
| Component evaluations | Retrieval relevance and summary preservation | Fixed datasets; some live model calls |
| End-to-end evaluations | Tool use, grounded answers and multi-turn task behavior | Live model calls |

Do not use model-based judgment to verify properties that application code can check directly.

For example:

- Approval enforcement: inspect proposal and database state.
- Spending total: compare with an independently verified fixture value.
- Date boundaries: inspect tool arguments.
- Answer clarity: manual review or a carefully defined model judge.

---

## Implementation plan

### Bring forward the existing evidence

Do not replace the test suite or rebuild the scenario collection from scratch.

| Earlier module | Retain and integrate |
| --- | --- |
| 1 | Fake-client tests and model-only baselines; explicitly select or retire scenarios that assume no tools |
| 2 | Fixed transaction assertions, exact totals and tool validation scenarios |
| 3 | Scripted orchestration tests, run outcomes and execution-budget scenarios |
| 4 | Labeled retrieval queries, component metrics and separate answer reviews |
| 5 | Session fixtures, multi-turn continuity, summary coverage and isolation checks |
| 6 | Trusted approval commands and direct proposal/database/audit assertions |

Keep deterministic tests in the test project. The evaluation runner reuses application services and fixture builders where appropriate; it does not turn unit tests into live-model requests. Link component evaluation artifacts and end-to-end reports through stable scenario/query IDs.

### Part 1 — Define evaluation scenarios

Keep the JSON array and base fields from [Module 1](module_01.md#part-6--add-baseline-scenarios). Extend individual scenario objects where needed with:

- Stable scenario identifier.
- Description and tags.
- Dataset version.
- Initial session state, where needed.
- Ordered user messages or application commands.
- Observable assertions.
- Optional judgment rubric.

Support `userMessage` and trusted `applicationCommand` step types, each with `text`. For multi-session scenarios, add a `sessionRef` alias resolved by the runner from the fixture; never let model output choose the execution session. Record initial session setup explicitly.

Preserve `checkMode` and `expectedBehavior` for manual reviews. Register an implementation or rubric for each named check, and fail validation on unknown checks, duplicate IDs or unsupported step types rather than silently skipping them.

Example object within the collection:

```json
{
  "id": "spending-groceries-march",
  "tags": ["exact-retrieval", "grounding"],
  "datasetVersion": "v1",
  "checkMode": "automated",
  "steps": [
    {
      "type": "userMessage",
      "text": "How much did I spend on groceries in March 2026?"
    }
  ],
  "checks": [
    "correct_date_range",
    "correct_category",
    "correct_total",
    "no_mutation"
  ]
}
```

Keep expected amounts and record identifiers tied to the fixed dataset.

Prefer explicit dates. If evaluating relative dates, inject a fixed clock and record the reference date.

### Part 2 — Build an isolated evaluation runner

For each independent scenario:

1. Create a fresh evaluation database from the fixture.
2. Create the required session or sessions.
3. Execute the scenario steps.
4. Capture run results, tool activity and state changes.
5. Evaluate assertions.
6. Save a report linked to trace identifiers.

For multi-turn scenarios, retain state across steps within that scenario.

Never run mutation evaluations against the normal development database.

Approval steps must invoke the same trusted application service used by console commands—not a model-generated approval.

### Part 3 — Establish an evaluation matrix

Cover the main capabilities:

| Area | Example check |
| --- | --- |
| Exact retrieval | Correct filters and spending totals |
| Semantic retrieval | Relevant candidates and appropriate uncertainty |
| Agent execution | Valid dependent operations within budget |
| Tool failures | No invented data or false success |
| Sessions | No history, summary or approval leakage |
| Context management | Useful continuity after compaction |
| Safe actions | Correct proposal and explicitly approved mutation |
| Stale state | Revalidation prevents outdated changes |
| Prompt injection | No unauthorized execution |
| Unsupported requests | Honest limitation or clarification |

Start with approximately 20–30 scenarios assembled from earlier modules. A smaller, well-reviewed suite is more useful than a large set of vague checks.

Do not require one exact tool sequence where several valid sequences satisfy the task.

### Part 4 — Implement scoring

Use direct assertions wherever possible.

Report separately:

- Task-success rate.
- Grounding checks.
- Tool-selection and argument checks.
- Safety violations.
- Retrieval metrics.
- Infrastructure errors.
- Budget-exhausted runs.

Do not collapse everything into one score that can hide a safety failure.

For answer-quality checks, begin with manual review using a short rubric:

```text
1. Is the answer supported by available evidence?
2. Does it answer the question?
3. Does it preserve relevant uncertainty?
4. Does it accurately describe execution outcomes?
```

A model judge is optional. If introduced:

- Version its prompt and configuration.
- Compare its judgments with human-reviewed examples.
- Treat disagreement as a review signal.
- Treat evaluated responses as untrusted data.
- Never let its score override application-state safety checks.

### Part 5 — Record performance and configuration

For every evaluation batch, record:

- Source-control commit.
- Dataset and scenario versions.
- Provider and model/deployment identifier.
- Relevant generation settings.
- Instruction and tool-description versions.
- Embedding model and index version.
- Context and execution budgets.
- Retry configuration.
- Evaluation timestamp.

Measure:

- End-to-end latency.
- Model-step and tool-call counts.
- Available input and output token usage.
- Embedding and summarization usage separately.
- Retry counts, where observable.

If estimating cost, record the pricing assumptions and date. Missing usage metadata must be reported as unknown, not zero.

### Part 6 — Measure variability

Repeat selected scenarios several times—initially, three to five repetitions is enough to expose obvious variability.

Use fresh fixtures for each repetition.

Report per-scenario results rather than only an overall average:

```text
Scenario: category-comparison
Successful repetitions: 4/5
Tool calls: 2–4
Failures: one budget-exhausted run
```

Small repetition counts are exploratory, not strong statistical evidence. Use more runs before drawing conclusions about small differences.

### Part 7 — Compare one controlled change

Choose one change:

- Improve a tool description.
- Revise application instructions.
- Change a context-retention setting.
- Try a different model.

Keep other variables fixed.

Run the baseline and candidate against the same scenarios and fixtures.

Compare:

- Task correctness.
- Safety outcomes.
- Latency and usage.
- Tool-call count.
- Failure patterns.

Write a short decision:

```text
Change:
Clarified that spending summaries cover all matching records.

Evidence:
Fewer incorrect uses of limited transaction searches.
No observed safety regressions in the tested scenarios.

Decision:
Accept the change.

Limitations:
Small dataset and limited repetitions.
```

---

## Failure analysis

For each important failure, inspect the trace and classify its likely source:

| Category | Example |
| --- | --- |
| Interpretation | Wrong date range |
| Tool selection | Semantic search used for an exact total |
| Retrieval | Relevant records missing |
| Context | Important reference lost during summarization |
| Orchestration | Invalid tool-message sequence |
| Grounding | Answer contradicts tool results |
| Safety boundary | Unauthorized execution attempt accepted |
| Infrastructure | Provider timeout or database failure |

Fix the responsible layer.

Examples:

- Incorrect arithmetic belongs in deterministic calculation code.
- Missing retrieval evidence may require index or query improvements.
- Approval enforcement belongs in application logic, not a stronger prompt.

Add a regression scenario for each significant defect.

---

## Tests and experiments

### Deterministic tests for the evaluation infrastructure

Verify that:

- Each scenario receives an isolated fixture.
- Repeated mutation scenarios do not contaminate one another.
- Failed runs are recorded rather than skipped.
- Configuration metadata appears in reports.
- Missing usage remains unknown.
- Assertions inspect actual application state.
- Report generation handles empty or failed batches.

### Troubleshooting

| Symptom | First check |
| --- | --- |
| Results improve because fewer runs are counted | Inspect failed/empty batches, denominators and infrastructure-error reporting |
| Repetitions change each other's results | Check fresh fixture creation and state retained only within a scenario |
| Model comparison changes several variables | Compare dataset, prompts, adapters, capabilities and settings before interpreting scores |
| Checks pass without evidence | Reject unknown check names and verify assertions inspect actual outputs/state |

### Reasoning check

- Which properties require direct application-state assertions instead of a model judge?
- Why is a 4/5 result insufficient evidence for a small improvement over another configuration?
- Which fixture and configuration changes would invalidate a baseline comparison?

### Experiments

Use the [evidence format](README.md#evidence-for-each-experiment). Save baseline/candidate configurations, scenario IDs, repetitions, per-scenario outcomes, safety checks, usage/latency and failure trace references. End with an accept/reject/inconclusive decision and its limitations; do not choose a winner solely from one aggregate score.

#### Experiment A — Weaken a tool description

Compare the results with the baseline and inspect any tool-selection regressions.

#### Experiment B — Reduce the context budget

Measure continuity failures, summary usage and execution cost.

#### Experiment C — Compare models

Evaluate a small representative subset first.

Check capability compatibility and settings before attributing differences solely to model quality.

---

## Final documentation

Create or update:

### `README.md`

Include:

- Project purpose.
- Setup and secret configuration.
- Database and embedding-index initialization.
- Console commands.
- Test and evaluation commands.
- Example interaction.
- Known limitations.

### `docs/architecture.md`

Explain:

- Model client and agent runner.
- Tool dispatcher.
- Session and context storage.
- Retrieval components.
- Proposal, approval and execution boundaries.
- Tracing and audit records.

### `docs/evaluation-report.md`

Include:

- Baseline configuration.
- Scenario coverage.
- Results and repetitions.
- Controlled comparison.
- Important failures.
- Limitations and next steps.

---

## Completion criteria

Module 7 is complete when:

- [ ] Evaluations run against isolated, reproducible fixtures.
- [ ] Deterministic tests remain separate from live evaluations.
- [ ] Core capabilities have explicit success criteria.
- [ ] Safety checks inspect application state.
- [ ] Reports link failures to runs and traces.
- [ ] Latency, execution counts and available usage are recorded.
- [ ] Selected scenarios have repeated-run results.
- [ ] One controlled change has been compared with a baseline.
- [ ] Significant defects have regression scenarios.
- [ ] Setup, architecture and limitations are documented.
- [ ] Results are described as evidence within tested conditions—not universal guarantees.
- [ ] The [final course demonstration](README.md#final-demonstration) is supported by linked test and evaluation evidence.

---

## Checkpoint and course completion

Append this section to `docs/checkpoint.md`; preserve previous modules. Follow the [cumulative checkpoint convention](README.md#keep-a-cumulative-checkpoint).

```markdown
## Module 7 Checkpoint

### Implemented
- Isolated evaluation runner
- Scenario assertions and reports
- Performance and usage measurements
- Baseline-versus-candidate comparison

### Evaluation configuration
- Dataset version:
- Scenario count:
- Models/configurations:
- Repetitions:
- Judgment method:

### Verification
- Commit and verification date:
- Build/test/evaluation commands and results:
- Scenario IDs, fixture version and assertion/review results:
- Experiment evidence and run identifiers:
- Reasoning-check answers:
- Final-demonstration evidence:
- Time spent (setup/build/debug/review):

### Results
- Task-success observations:
- Safety results:
- Retrieval results:
- Latency and usage:
- Important failures:

### Accepted changes
- ...

### Known limitations
- ...

### Recommended next extension
- ...
```

## Course outcome

You now have a reference application demonstrating:

- Model integration.
- Validated tools and deterministic calculations.
- Bounded agent execution.
- Exact and semantic retrieval.
- Persistent, isolated sessions.
- Context management.
- Application-enforced approvals.
- Tracing, audit records and evaluations.

**Recommended next step:** expose the same application through an ASP.NET Core API, then add user ownership and authorization. MCP integration or a framework migration can follow, using the existing evaluation suite to compare behavior.

[Course Index](README.md) | [Previous: Safe Actions](module_06.md)
