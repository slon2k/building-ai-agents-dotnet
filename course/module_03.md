# Module 3 — The Agent Loop and Bounded Execution

[Course Index](README.md) | [Previous: Tools and Transaction Data](module_02.md) | [Next: Retrieval and Embeddings](module_04.md)

## Module purpose

Turn the single tool round from Module 2 into an agent that can perform several steps toward an answer.

We will implement the execution loop ourselves, making its decisions, limits and failure behavior visible.

---

## Module overview

**Estimated effort:** 3–4 hours, split across sessions if needed.

**Main question:**

> How do we allow the model to choose successive operations without giving it unlimited execution time or control?

### Working result

An expense assistant that can:

- Execute multiple model/tool cycles.
- Use earlier tool results to select subsequent operations.
- Stop when it produces a final answer.
- Respect iteration, tool-call and time limits.
- Handle tool failures without fabricating results.
- Report explicit execution outcomes.
- Produce a complete trace for each run.

The basic flow becomes:

```text
User message
    ↓
Model response
    ├── Final answer → Complete
    │
    └── Tool requests
            ↓
       Validate and execute
            ↓
       Append tool results
            ↓
       Check execution limits
            ↓
       Request another model response
```

We will observe tool requests and results—not depend on access to the model’s private internal reasoning.

---

## Prerequisites

Module 2 should be complete:

- Synthetic transactions in SQLite.
- Validated read-only query tools.
- Deterministic spending calculations.
- A working single tool round.
- Basic model and tool tracing.
- Tests and behavioral evaluation scenarios.

We will continue using one active conversation session. Persistent session switching comes in Module 5.

---

## Learning objectives

By the end of this module, you should be able to:

1. Explain the difference between tool calling and an agent execution loop.
2. Implement repeated model/tool interactions.
3. Preserve valid message and tool-call relationships.
4. Define execution budgets and terminal states.
5. Distinguish recoverable tool errors from fatal run failures.
6. Separate model decisions from application enforcement.
7. Test orchestration independently of live model behavior.

---

## Concepts and trade-offs

The model chooses a requested next operation; the application decides whether it may execute. An agent loop repeats this interaction, but cannot delegate its own stopping rules to the model.

### Worked budget check

Set `MaxModelSteps` to 2 and script a fake client that always asks for a valid read-only tool. Allow sufficient tool-call budget and time so only the model-step limit is being tested.

| Point in the run | Expected evidence |
| --- | --- |
| First model request | Step counter becomes 1; requested tool receives a matching result |
| Second model request | Step counter becomes 2; tool relationships remain valid |
| Before a third model request | Runner stops with `BudgetExceeded`; fake-client call count remains 2 |

Assert the terminal state and counters, not an apologetic final sentence. Repeat separately with cancellation and a tool-call limit so one passing limit does not hide another broken control.

---

## Execution model

### Session versus run

Keep the distinction established earlier:

- **Session:** the conversation over time.
- **Run:** one execution triggered by a user message.
- **Model step:** one request to the model within that run.
- **Tool call:** one requested application operation.

A run may contain several model steps and tool calls.

### Terminal states

Define explicit outcomes:

| State | Meaning |
| --- | --- |
| `Completed` | The model produced a final answer |
| `Failed` | An unrecoverable application or provider failure occurred |
| `Cancelled` | The caller cancelled execution |
| `BudgetExceeded` | An application-enforced execution limit was reached |

A completed run does **not** necessarily mean the user’s task succeeded. A clarification question or an honest explanation of unavailable data can also complete the run.

---

## Implementation plan

### Part 1 — Extract an agent runner

Introduce an application component responsible for orchestration:

```text
AgentRunner
├── Model client
├── Tool registry and dispatcher
├── Execution policy
└── Diagnostics
```

Keep responsibilities separate:

- The model client handles model interaction.
- The dispatcher validates and executes tools.
- The runner coordinates steps.
- The execution policy defines limits.
- Diagnostics record what happened.

Avoid building a general-purpose framework. Implement only what this project needs.

### Part 2 — Define run options and results

Example execution options:

```csharp
public sealed record AgentRunOptions(
    int MaxModelSteps = 6,
    int MaxToolCalls = 10,
    TimeSpan? MaxDuration = null);
```

Treat these as initial defaults to experiment with, not universal recommendations.

The run result should include:

- Session and run identifiers.
- Terminal state.
- Final answer, when available.
- Model-step and tool-call counts.
- Duration.
- Available usage totals.
- A safe failure or stop reason.

Do not treat an intermediate response as a completed answer after the budget is exhausted.

### Part 3 — Implement the loop

Use explicit orchestration rather than automatic tool execution hidden inside middleware.

Conceptually:

```text
Until a terminal state is reached:

  Check cancellation and execution budget.

  Request a model response.

  Record and append the response.

  If it has no tool requests:
      Return the final answer.

  Check that requested tools fit the remaining budget.

  Validate and execute each request.

  Append matching tool results.

  Continue.
```

For this module:

- Execute tool calls sequentially.
- Count attempted tool calls, including rejected requests.
- Reject a tool batch before executing it if it exceeds the remaining call budget.
- Do not initiate another model request after reaching a limit.

When stopping before requested tools execute, mark the run incomplete. Do not leave unmatched tool requests in the reusable conversation history; retain them in diagnostics instead.

### Part 4 — Preserve message integrity

Each executed tool request must have a corresponding result linked by its call identifier.

Preserve:

- The assistant message containing the requests.
- All corresponding tool results.
- Their execution order.

Do not replace tool results with invented assistant messages or omit failed calls.

This will also matter when we introduce context trimming in Module 5.

### Part 5 — Handle failures deliberately

Separate failure categories:

| Failure | Behavior |
| --- | --- |
| Invalid tool arguments | Return a structured validation error; allow correction within budget |
| Unknown tool | Reject it and return a safe error |
| Tool unavailable | Return a structured failure; never substitute fabricated data |
| Provider authentication/configuration error | Fail the run |
| Transient provider failure | Apply a small, explicit retry policy if supported |
| User cancellation | Stop and return `Cancelled` |
| Execution limit reached | Stop and return `BudgetExceeded` |

Keep retry behavior visible. Check whether the provider client already retries so application retries do not multiply unexpectedly.

Use the same run deadline across retries and tool execution. Cancellation should be propagated throughout, though an upstream service may not stop immediately.

### Part 6 — Expand tracing

Represent a run as:

```text
Run
├── Model step 1
├── Tool call 1
├── Tool call 2
├── Model step 2
├── Tool call 3
└── Model step 3 → Completed
```

Record:

- Session and run identifiers.
- Step number.
- Tool-call identifier and tool name.
- Duration and outcome.
- Retry count, where available.
- Available token usage.
- Terminal state and stop reason.

Keep full prompt and transaction payload logging disabled by default.

---

## Multi-step scenarios

Add scenarios where a later operation depends on an earlier result.

### Example A — Compare categories

```text
User:
Which category had the highest spending in March,
and which purchases contributed to it?
```

Possible sequence:

1. Request the March spending summary.
2. Identify the highest-spending category.
3. Search transactions within that category and date range.
4. Answer using the summary and supporting records.

If the search is truncated, the answer must not imply that the displayed purchases are the complete set.

### Example B — Correct an invalid request

Use a fake model response to request an invalid date range.

Expected sequence:

1. Tool validation returns an error.
2. A subsequent model step supplies corrected arguments.
3. The tool succeeds.
4. The run completes within its budget.

### Example C — Repetitive execution

Use a fake model that repeatedly requests the same tool.

Expected result:

- Execution stops at the configured limit.
- The result is `BudgetExceeded`.
- The trace explains the stop.
- No fabricated final answer is produced.

---

## Tests and evaluations

### Deterministic orchestration tests

Use scripted fake model responses to verify:

- A final answer completes without tool execution.
- Multiple model/tool cycles execute correctly.
- Multiple tool requests receive matching results.
- Validation errors can be followed by corrected requests.
- Unknown tools never execute.
- Model-step and tool-call limits are enforced.
- Cancellation stops subsequent work.
- Fatal provider errors produce `Failed`.
- Incomplete tool exchanges do not corrupt reusable history.
- Every execution is attributed to the correct session and run.

These tests should not depend on live API calls.

### Behavioral evaluations

Extend the scenario set with:

| Scenario | Check |
| --- | --- |
| Highest-spending category and supporting purchases | Correct dependent operations and grounded answer |
| Ambiguous date range | Clarification or documented default |
| Tool failure | Honest explanation without invented results |
| Unnecessary repeated operations | Tool count and execution limits |
| Simple question | No unnecessary tool use |
| Unsupported mutation request | No database changes |

Evaluate outcomes and relevant constraints rather than requiring one exact tool sequence when several valid sequences exist.

Add these scenarios to the existing collection with stable IDs and capability tags. Keep scripted orchestration tests in the test project; use the scenario collection for live task evidence. Reference both from the checkpoint rather than trying to make one test style replace the other.

### Troubleshooting

| Symptom | First check |
| --- | --- |
| Repeated requests never stop | Inspect budget checks and counters before each model step and tool dispatch |
| Provider rejects a later request | Inspect complete assistant tool requests and corresponding results in order |
| Run is `Completed` but task was not solved | Score the task outcome separately from the execution terminal state |
| Cancellation still allows more tools | Verify token propagation and checks between operations |

### Reasoning check

- How does one tool round differ from an agent loop?
- Which layer should reject an unknown tool even if the model insists it exists?
- Why does a completed run not necessarily indicate task success?

---

## Experiments

Use the [evidence format](README.md#evidence-for-each-experiment). For each run, save the scenario ID, terminal state, model-step count, tool-call count and trace reference. For budget changes, compare where execution stopped; for model changes, repeat the same scenarios and keep other settings fixed.

### Experiment A — Reduce the budget

Run a multi-step scenario with a very small model-step limit.

Inspect where execution stops and how the application reports it.

### Experiment B — Inject a recoverable failure

Return a validation error, then allow a corrected request.

Observe whether the model recovers and how many additional steps it uses.

### Experiment C — Compare model behavior

Repeat selected scenarios and record:

- Completion rate.
- Tool-call count.
- Latency.
- Available token usage.

Similar final answers may have different execution costs.

---

## Completion criteria

Module 3 is complete when:

- [ ] A reusable runner supports multiple model/tool cycles.
- [ ] Tool execution remains application-controlled.
- [ ] Model-step, tool-call and duration limits are enforced.
- [ ] Terminal states are explicit.
- [ ] Recoverable errors and fatal failures are distinguished.
- [ ] Message and tool-call relationships remain valid.
- [ ] Run traces show the complete execution sequence.
- [ ] Deterministic orchestration tests pass.
- [ ] Multi-step behavioral evaluations are recorded.
- [ ] No write tools or hidden automatic execution loops are introduced.

---

## Checkpoint and handoff

Append this section to `docs/checkpoint.md`; preserve previous modules. Follow the [cumulative checkpoint convention](README.md#keep-a-cumulative-checkpoint).

```markdown
## Module 3 Checkpoint

### Implemented
- Explicit agent runner
- Multi-step tool execution
- Execution budgets
- Terminal states
- Run-level tracing

### Execution policy
- Maximum model steps:
- Maximum tool calls:
- Run timeout:
- Retry behavior:

### Verification
- Commit and verification date:
- Build/test commands and results:
- Scenario IDs, fixture version and assertion/review results:
- Experiment evidence and run identifiers:
- Reasoning-check answers:
- Time spent (setup/build/debug/review):
- Orchestration test results:
- Behavioral evaluation observations:
- Example successful and budget-exhausted traces:

### Decisions and limitations
- ...

### Next step
Module 4: add embeddings and semantic retrieval, then compare
meaning-based search with exact database queries.
```

[Course Index](README.md) | [Previous: Tools and Transaction Data](module_02.md) | [Next: Retrieval and Embeddings](module_04.md)
