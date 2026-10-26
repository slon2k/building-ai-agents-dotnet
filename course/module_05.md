# Module 5 — Sessions, Persistence and Context Management

[Course Index](README.md) | [Previous: Retrieval and Embeddings](module_04.md) | [Next: Safe Actions](module_06.md)

## Module purpose

Give the assistant independent, persistent conversations and control what information each model request receives.

We will separate stored conversation history from the context sent to the model, introduce summaries and verify that information does not leak between sessions.

---

## Module overview

**Estimated effort:** 3–5 hours, split across sessions if needed.

**Main question:**

> How do we preserve useful conversation continuity without sending unlimited history or mixing independent conversations?

### Working result

An expense assistant that can:

- Create, list, switch and resume conversation sessions.
- Persist messages and run records in SQLite.
- Build model context from session-specific information.
- Keep recent exchanges and summarize older ones.
- Preserve valid tool-call/result relationships.
- Enforce an application-level context budget.
- Trace context construction and summarization.
- Pass session-isolation and persistence tests.

Example:

```text
/new
→ Session A created

User: Show my travel expenses in March.
Assistant: ...

/new
→ Session B created

User: How much did I spend on groceries?
Assistant: ...

/use <session-a-id>
User: Which of those travel expenses was largest?
Assistant: ...
```

Session B must not receive Session A’s history or summary.

---

## Prerequisites

Module 4 should be complete:

- A bounded agent runner.
- Exact and semantic retrieval tools.
- SQLite transaction storage.
- Session and run identifiers.
- Execution tracing.
- Deterministic tests and behavioral evaluations.

Write actions and approval workflows are not implemented yet. We will establish their session ownership in this module and implement them in Module 6.

---

## Learning objectives

By the end of this module, you should be able to:

1. Distinguish conversation history, model context, summaries and application state.
2. Persist and resume independent sessions.
3. Build context without cross-session leakage.
4. Apply a practical token budget.
5. Compact history without breaking tool-message relationships.
6. Treat summaries as lossy conversational aids rather than authoritative records.
7. Evaluate continuity, isolation and summarization failures.

---

## Concepts and trade-offs

Persistence answers "what happened?" Context construction answers "what should the next model request receive?" Keeping these separate allows you to reduce request size without destroying the original history or confusing a summary with database truth.

### Worked compaction boundary

Suppose a session contains two completed conversation blocks: message sequences 1-4 and 5-8, each including intact tool exchanges. A new user message receives sequence 9.

| Representation | After compacting the older block |
| --- | --- |
| Stored history | Messages 1-9 remain available |
| Summary coverage | Summary explicitly covers through sequence 4 |
| Next model context | Stable instructions, labeled summary of 1-4, recent block 5-8, current message 9 |

Messages 1-4 must not also appear verbatim in that request. Do not move the boundary into the middle of a tool exchange. If summary generation fails, keep the old boundary and follow the documented context-limit policy rather than pretending compaction succeeded.

Before building, explain how you would detect both duplicated history and a summary accidentally loaded from another session.

---

## State model and architectural rules

### Different kinds of state

| State | Purpose | Authority |
| --- | --- | --- |
| Conversation history | Records user, assistant and tool exchanges | Evidence of what was exchanged |
| Context summary | Compresses older conversation | Lossy aid to continuity |
| Transaction database | Stores current transaction facts | Authoritative transaction data |
| Run records | Track execution status and diagnostics | Authoritative execution state |
| Action proposals and approvals | Track proposed changes and authorization | Application-managed state; added in Module 6 |

A summary must never become the authoritative source for transaction amounts, current categories or approval status.

### Session ownership

Each session owns:

- Its message history.
- Its context summary.
- Its runs and associated traces.
- Its future action proposals and approvals.

The transaction database remains shared.

This is a **single-user, multiple-session application** for now. Session isolation is not a substitute for user ownership checks or authorization in a multi-user service.

---

## Implementation plan

### Lab stages and verification gates

Complete these stages in order. Each is a stopping point for a separate study session, not permission to skip the remaining safety requirements.

| Lab | Parts | Gate before continuing |
| --- | --- | --- |
| A. Persist and resume | 1-3 | Restart with two sessions; verify isolated history and safe handling of an unfinished run |
| B. Assemble and compact context | 4-7 | With fake summaries and a small budget, verify coverage, no duplication and intact tool exchanges |
| C. Revalidate current facts | 8, then tests and experiments | Change a shared transaction, resume an older session and verify that a current-state question uses fresh data |

Save test results and one diagnostic artifact at each gate. Run all prior deterministic tests after integrating each stage.

### Part 1 — Add persistent session storage

Introduce SQLite storage for:

#### Sessions

- `SessionId`
- Display name
- Creation timestamp
- Last activity timestamp

#### Messages

- Message identifier
- `SessionId`
- `RunId`, where applicable
- Monotonic sequence number within the session
- Role and serialized content
- Tool-call identifiers where relevant

Preserve the message structure needed to reconstruct tool exchanges. Do not store only the visible text.

#### Summaries

- `SessionId`
- Summary text
- Covered-through message sequence
- Summary version
- Creation timestamp
- Model and prompt-version metadata

#### Runs

- `RunId`
- `SessionId`
- Start and end timestamps
- Execution status
- Safe stop reason
- Usage and execution counters

Use parameterized queries and explicit session scoping.

### Part 2 — Add session commands

Implement a small console interface:

```text
/new [name]
/sessions
/use <session-id>
/history
/context
```

Expected behavior:

- `/new` creates and selects a new session.
- `/sessions` lists existing sessions.
- `/use` selects an existing session.
- `/history` displays stored conversation history.
- `/context` displays a diagnostic description of the assembled context.

For `/context`, show message counts, summary coverage and estimated token usage by default—not full sensitive content.

Keep the active session selection in the console layer. Pass the selected `SessionId` explicitly into each run.

Do not rely on a shared mutable message list inside the agent runner.

### Part 3 — Persist exchanges safely

Persist messages and run transitions as execution progresses.

For this course:

- Allow only one active run per session.
- Await or cancel the current run before switching sessions.
- Record interrupted runs rather than silently treating them as completed.
- Append complete tool-result batches transactionally where practical.

On restart, identify unfinished runs and mark them as interrupted failures according to a documented recovery policy.

Do not automatically replay unfinished tool calls. This will become especially important when write tools are introduced.

Incomplete tool exchanges may remain in stored diagnostic history, but must not be sent back to the model as though they were valid completed exchanges.

### Part 4 — Extract a context builder

Introduce a component responsible for constructing each model request:

```text
ContextBuilder
├── Stable application instructions
├── Session summary
├── Recent eligible history
├── Current user message
└── Current run's tool exchanges
```

The full stored history and the current model context are different objects.

History compaction should not delete the original messages. It changes which messages are selected for model requests.

Add the summary as clearly labeled contextual data, not as higher-priority application instructions.

### Part 5 — Introduce a context budget

Configure an application input budget that leaves room for:

- Model output.
- Tool definitions.
- Message-format overhead.
- A safety margin for estimation errors.

Conceptually:

```text
Model context capacity
    − reserved output
    − safety margin
    = available input capacity
```

Tool definitions and instructions consume part of that input capacity.

Use a compatible tokenizer where available. Otherwise use a documented approximation and conservative margin. Post-request usage metadata is useful for calibration, but cannot guarantee that the next request fits.

Check the budget before **every model step**, not just at the start of a run.

If the current user message or a tool result cannot fit:

- Do not truncate silently.
- Return a clear context-limit outcome or use a documented, safe reduction policy.
- Never shorten structured financial results in a way that changes their meaning.

For this module, fail safely when the active run alone cannot fit. More advanced tool-result compression can be a later extension.

### Part 6 — Summarize older conversation

When history exceeds the configured threshold:

1. Select older, completed conversation blocks.
2. Exclude the active run.
3. Combine the previous summary with the newly covered exchanges.
4. Generate a replacement summary.
5. Persist it with an updated coverage boundary.
6. Retain recent exchanges verbatim.

A useful summary captures:

- The topics discussed.
- User-stated constraints.
- Relevant date ranges.
- Previously referenced transaction identifiers.
- Unresolved questions.
- Explicit uncertainty.

It should not invent durable preferences or assert that a proposed action was approved.

If summarization fails, retain the previous summary and coverage boundary. Do not silently discard additional history.

Count summarization requests separately in usage and diagnostics.

### Part 7 — Preserve tool-exchange integrity

Treat completed tool exchanges as indivisible context blocks:

```text
Assistant tool requests
    +
Corresponding tool results
```

Never retain:

- A tool result without its request.
- A request without its results.
- Only some results from a multi-tool batch.

Perform ordinary history compaction between completed runs. During a run, preserve its complete exchanges and stop safely if they exceed the available budget.

### Part 8 — Handle stale information

A shared transaction may change while another session retains an old answer.

The assistant should use fresh tools when answering questions about **current** transaction state.

For example:

```text
Session A:
A transaction was previously categorized as Travel.

Session B:
The category is changed later.

Session A resumes:
“What category does it have now?”
→ Query the database again.
```

A historical answer or summary is not evidence of the current database value.

---

## Tests and evaluations

### Deterministic tests

Use fake model and summarization responses to verify:

- Session creation, selection and persistence.
- Session-scoped message retrieval.
- History survives application restart.
- Session A’s messages and summary never enter Session B’s context.
- Summaries cover the correct message range.
- Recent messages are not duplicated after compaction.
- Tool exchanges remain intact.
- Failed summarization does not advance coverage.
- Context budgets include tool definitions and output reservation.
- Interrupted runs are not automatically replayed.
- Concurrent runs in the same session are rejected or serialized.
- Traces carry the correct session and run identifiers.

### Behavioral evaluations

| Scenario | Expected behavior |
| --- | --- |
| Follow-up within one session | Resolves references using that session’s context |
| Same follow-up in a new session | Requests clarification rather than using another session |
| Resume after restart | Continues from persisted context |
| Long conversation | Maintains relevant continuity after summarization |
| Important detail omitted from summary | Requests clarification or retrieves evidence rather than guessing |
| Shared transaction changed elsewhere | Uses a fresh query for current state |
| Older text contains misleading instructions | Does not promote that text into application instructions |

Use unique synthetic markers in isolation tests so cross-session leakage is easy to detect.

Prompt-injection defenses receive more attention in Module 6; summarization itself is not a security boundary.

Add multi-turn scenarios to the existing collection rather than replacing earlier tool and retrieval scenarios. Record initial sessions, ordered messages and session selection explicitly; Module 7 will make this setup reproducible in its runner. Use fake summaries for exact coverage assertions and live summaries only for behavioral review.

### Troubleshooting

| Symptom | First check |
| --- | --- |
| A resumed conversation loses history | Inspect committed records and selected session before changing summarization |
| Old exchanges appear twice | Compare summary coverage with the recent-history selection boundary |
| Compaction produces invalid tool messages | Select completed blocks, not individual messages by count |
| Context fits initially but fails after a tool result | Check the budget before every model step, including active-run exchanges |
| Old answers contradict current data | Inspect whether a fresh database tool was used; summaries are not current facts |

### Reasoning check

- Why does compacting request context not require deleting stored history?
- What should happen to summary coverage when generation fails?
- Which checks distinguish session isolation from authenticated user authorization?

---

## Experiments

Use the [evidence format](README.md#evidence-for-each-experiment). Record scenario/session IDs, selected message ranges, summary coverage, estimated input usage, configured budget, tool-exchange validity and review results. For restart experiments, save state before and after restart. Do not copy full conversation payloads into traces merely to prove isolation.

### Experiment A — Lower the context budget

Force compaction after a short conversation.

Inspect:

- Which exchanges remain verbatim.
- What the summary preserves.
- Whether follow-up answers remain accurate.

### Experiment B — Inspect summary drift

Summarize the same conversation more than once and compare results.

Look for lost uncertainty, changed dates and unsupported assumptions.

### Experiment C — Resume and switch

Alternate between two conversations, restart the application and resume both.

Verify continuity without leakage.

### Experiment D — Simulate an interrupted run

Stop execution after tool requests but before all results are persisted.

Verify that restart handling does not replay the run or assemble invalid model context.

---

## Completion criteria

Module 5 is complete when:

- [ ] Sessions can be created, listed, switched and resumed.
- [ ] History, summaries and runs are persisted with explicit session ownership.
- [ ] The runner receives session context explicitly.
- [ ] Stored history is separate from assembled model context.
- [ ] Context budgets are checked before every model request.
- [ ] Older completed exchanges can be summarized.
- [ ] Summary coverage is tracked without deleting original history.
- [ ] Tool-call/result relationships remain valid.
- [ ] Interrupted runs have a documented recovery policy.
- [ ] Session-isolation and persistence tests pass.
- [ ] Context diagnostics and summarization usage are inspectable.
- [ ] Summaries are not treated as current financial data or approval authority.

---

## Checkpoint and handoff

Append this section to `docs/checkpoint.md`; preserve previous modules. Follow the [cumulative checkpoint convention](README.md#keep-a-cumulative-checkpoint).

```markdown
## Module 5 Checkpoint

### Implemented
- Persistent conversation sessions
- Session commands
- Context builder
- Context budgets
- Versioned summaries
- Interrupted-run handling

### Context policy
- Input budget:
- Reserved output:
- Token estimation method:
- Recent-history retention:
- Summarization trigger:
- Oversized-message behavior:

### Verification
- Commit and verification date:
- Build/test commands and results:
- Lab A/B/C gate evidence:
- Scenario IDs, fixture version and assertion/review results:
- Experiment evidence and run identifiers:
- Reasoning-check answers:
- Time spent (setup/build/debug/review):
- Persistence test results:
- Session-isolation test results:
- Follow-up and summarization observations:
- Interrupted-run recovery results:

### Decisions and limitations
- ...

### Next step
Module 6: implement session-owned action proposals, explicit
approval, execution-time revalidation and trust-boundary tests.
```

[Course Index](README.md) | [Previous: Retrieval and Embeddings](module_04.md) | [Next: Safe Actions](module_06.md)
