# Module 2 — Tools, Transaction Data and Deterministic Business Logic

[Course Index](README.md) | [Previous: Model Interaction](module_01.md) | [Next: The Agent Loop](module_03.md)

## Module purpose

Connect the model to real application capabilities without giving it unrestricted access to the database.

We will introduce synthetic transaction data, implement read-only tools and let the model request their execution. Application code will validate requests, query the database and calculate results.

---

## Module overview

**Estimated effort:** 3–4 hours, which can be split across multiple sessions.

**Main question:**

> How do we turn a model’s request into a reliable, controlled application operation?

### Working result

An expense assistant that can:

- Request transaction searches.
- Request spending summaries.
- Execute approved read-only tools through application code.
- Return structured results and safe errors.
- Produce a final answer grounded in tool results.
- Record basic traces of model and tool execution.

We will implement **one bounded tool round** in this module:

```text
User request
    ↓
Model requests tools
    ↓
Application validates and executes them
    ↓
Tool results are sent back to the model
    ↓
Final answer
```

Repeated model/tool cycles and general agent orchestration belong to Module 3.

---

## Prerequisites

Module 1 should be complete:

- A working `IChatClient` integration.
- Configuration and credentials managed outside source control.
- Cancellation and basic error handling.
- Session and run identifiers.
- An initial test project and evaluation scenarios.

No real financial data is required.

---

## Learning objectives

By the end of this module, you should be able to:

1. Explain the distinction between a tool definition, a tool request and tool execution.
2. Design tool schemas and descriptions that a model can use.
3. Validate model-generated arguments as untrusted input.
4. Use deterministic code for financial calculations.
5. Return structured tool results and distinguish failures from empty results.
6. Preserve tool-call/result relationships in conversation messages.
7. Inspect model and tool activity through basic tracing.

---

## Concepts and trade-offs

A tool definition describes a capability. A tool request is untrusted model output asking to use it. Execution is an application decision after name and argument validation. Giving the model a tool definition does not authorize arbitrary database access.

### Worked fixture: search is not aggregation

Include a small known subset in your fixed dataset, with no other matching records:

| ID | Date | Category | Amount in minor units |
| --- | --- | --- | --- |
| TX-G1 | 2026-03-02 | Groceries | 1250 |
| TX-G2 | 2026-03-14 | Groceries | 875 |
| TX-G3 | 2026-04-01 | Groceries | 500 |

For the interval `[2026-03-01, 2026-04-01)`, a search limited to one result returns one March purchase and indicates omitted results. The summary must still return 2125 minor units, two transactions and the fixture currency. Establish this expectation in a direct service test before asking the model the same question.

The omitted April record tests the exclusive end boundary. A model explaining the correct arithmetic does not substitute for this database-level assertion.

---

## Data model and scope

### Synthetic transaction dataset

Create a reproducible dataset of approximately 100–200 transactions containing:

| Field | Purpose |
| --- | --- |
| `Id` | Stable transaction identifier |
| `Date` | Transaction date |
| `Merchant` | Merchant name |
| `Description` | Additional transaction text |
| `Category` | Assigned expense category |
| `Amount` | Transaction amount |
| `Currency` | Currency code |

Include several months, repeated merchants and a few ambiguous descriptions.

### Simplifying assumptions

For this module:

- Use one currency.
- Represent expense amounts as positive values.
- Exclude refunds and transfers.
- Use `DateOnly` for transaction dates.
- Use `decimal` in application code for monetary values.
- Store amounts as integer minor units in SQLite to avoid floating-point calculations.

Document these assumptions. They make the exercise manageable but are not a complete financial-data model.

---

## Implementation plan

### Part 1 — Seed SQLite

Add a repeatable database initialization process.

It should:

- Create the transaction table.
- Load synthetic data.
- Avoid duplicate records when run again.
- Allow tests to start from a known dataset.

Keep a fixed reference dataset for evaluations so expected answers do not change unexpectedly.

### Part 2 — Implement deterministic query services

Build the application capabilities before exposing them to the model.

Start with two operations:

#### `SearchTransactions`

Supported arguments:

- Start date, inclusive.
- End date, exclusive.
- Optional category.
- Optional merchant text.
- Result limit.

Return:

- Matching transactions.
- Returned count.
- Whether additional results were omitted.

#### `GetSpendingSummary`

Supported arguments:

- Start date, inclusive.
- End date, exclusive.
- Optional category.

Return:

- Total spending.
- Transaction count.
- Currency.
- Totals grouped by category.

**Important:** Calculate summaries over all matching records—not only a limited search result.

The model must not receive an arbitrary SQL execution capability. Use parameterized queries and controlled filters.

### Part 3 — Define tool contracts

Expose the query operations as tools through the selected client integration.

For each tool, define:

- A stable name.
- A clear purpose.
- Typed arguments.
- Required and optional fields.
- A structured result.
- Relevant constraints.

Example description:

```text
GetSpendingSummary

Calculate total expenses for a date range, optionally restricted
to one category. Use this tool for spending totals rather than
adding amounts from a limited transaction search.

The start date is inclusive; the end date is exclusive.
```

Discuss the difference between:

- **Structured tool arguments:** the model requests an operation using a schema.
- **Structured tool results:** application code returns a predictable data shape.
- **Structured final output:** an optional format for the assistant’s answer.

A schema improves interoperability; it does not replace validation.

### Part 4 — Validate requests before execution

Validate:

- Date formats and date ordering.
- Allowed categories.
- Result-limit bounds.
- Required arguments.
- Supported tool names.

Distinguish three outcomes:

```text
Successful query with records
Successful query with no records
Failed query
```

A validation failure must not become an empty result or a fabricated total.

Use a consistent result envelope, for example:

```json
{
  "success": false,
  "error": {
    "code": "INVALID_DATE_RANGE",
    "message": "The end date must be after the start date."
  }
}
```

Keep internal exception details out of model-facing responses.

### Part 5 — Execute one bounded tool round

Implement the following flow explicitly:

1. Send the user request with available tool definitions.
2. Inspect any returned tool requests.
3. Validate and execute recognized tools.
4. Match each result to its tool-call identifier.
5. Send the original tool requests and corresponding results back to the model.
6. Request a final response without further tool execution.

If the model requests multiple tools, execute them sequentially for now, within a small configurable cap.

Do not enable a hidden automatic execution loop. We want to inspect the mechanics before introducing general orchestration.

### Part 6 — Add basic tracing

Create an execution trace containing:

```text
Run
├── Initial model request
├── Tool validation and execution
└── Final model request
```

Attach:

- `SessionId`
- `RunId`
- Tool-call identifier
- Tool name
- Duration
- Outcome or error code
- Available model usage metadata

Avoid logging full prompts, transaction descriptions or tool payloads by default. Even though this dataset is synthetic, establish safe logging habits.

Use structured logs and `ActivitySource` spans; a tracing backend is optional at this stage.

---

## Tests and evaluations

### Deterministic tests

Verify that:

- Date boundaries are applied correctly.
- Category and merchant filters work.
- Result limits are enforced.
- Summaries use the complete matching dataset.
- Monetary totals are exact.
- Invalid arguments are rejected.
- Unknown tools are not executed.
- Tool results retain their corresponding call identifiers.
- Cancellation reaches database operations.
- Database failures do not produce invented results.

Use fake model responses to test orchestration without live API calls.

### Behavioral evaluations

Add scenarios such as:

| Request | Expected behavior |
| --- | --- |
| “How much did I spend on groceries in March?” | Requests a spending summary with the correct date range and category |
| “Show my purchases from Green Market.” | Requests a transaction search |
| “What was my total spending?” | Requests clarification or follows an explicitly documented date-range default |
| “Show expenses in a period with no transactions.” | Reports an empty result accurately |
| “Change this transaction’s category.” | Does not claim to modify data; no write tool exists |
| A query whose tool execution fails | Reports the failure without fabricating an answer |

For repeatable date tests, prefer explicit dates. Relative expressions such as “last month” will need an application-provided reference date.

Record both the selected tool and its arguments—not only the final answer.

Extend the [Module 1 scenario contract](module_01.md#part-6--add-baseline-scenarios), adding `datasetVersion` to every fixture-backed scenario. Store expected totals and record IDs in versioned fixture assertions, not values copied from the model response.

The `missing-transaction-data` scenario is no longer applicable to the normal tool-enabled configuration. Retain it in a model-only baseline subset or explicitly retire it with a reason; do not silently reinterpret its expected result.

### Troubleshooting

| Symptom | First check |
| --- | --- |
| Total changes with the search limit | Ensure aggregation queries all matching records rather than summing a truncated list |
| Incorrect month boundary | Inspect inclusive start and exclusive end arguments before changing the prompt |
| Tool result is rejected by the provider | Check message ordering, serialization and matching call identifiers |
| Query failure appears as zero spending | Return a typed failure distinct from a successful empty result |

### Reasoning check

- Why does a valid JSON argument still need application validation?
- Which assertion proves the assistant did not mutate the database?
- Why must the expected spending total be established independently of model output?

---

## Experiments

For each experiment, use the [evidence format](README.md#evidence-for-each-experiment). Save scenario and fixture versions, tool arguments, assertion/review results and run identifiers. Compare the same inputs before and after a description change; distinguish a tool-selection failure from an incorrect query implementation.

### Experiment A — Tool descriptions

Make a description vague, rerun the same scenarios, then improve it.

Observe whether tool selection or arguments change.

### Experiment B — Search versus aggregation

Ask for a total when the search tool returns only a few records.

Verify that the assistant uses the summary tool instead of treating the partial list as complete.

### Experiment C — Failure injection

Simulate a database failure.

Check whether the final response honestly distinguishes “the query failed” from “there were no transactions.”

### Experiment D — Unsupported capability

Ask the assistant to delete a transaction.

Verify that no database mutation occurs, regardless of what the model says.

---

## Completion criteria

Module 2 is complete when:

- [ ] Synthetic transactions are reproducibly stored in SQLite.
- [ ] Query services work independently of the model.
- [ ] Two read-only tools are exposed.
- [ ] Tool arguments are validated by application code.
- [ ] Financial totals are calculated deterministically.
- [ ] One bounded tool round produces a grounded final answer.
- [ ] Tool-call/result relationships are preserved.
- [ ] Basic model and tool traces are available.
- [ ] Deterministic tests pass.
- [ ] Behavioral scenarios cover tool selection, arguments and failures.
- [ ] No write capability or arbitrary SQL execution is exposed.

---

## Checkpoint and handoff

Append this section to `docs/checkpoint.md`; preserve previous modules. Follow the [cumulative checkpoint convention](README.md#keep-a-cumulative-checkpoint).

```markdown
## Module 2 Checkpoint

### Implemented
- Synthetic transaction database
- Read-only query services
- Tool contracts and validation
- One bounded tool round
- Basic execution tracing

### Data assumptions
- Currency:
- Amount representation:
- Date-range convention:

### Verification
- Commit and verification date:
- Build/test commands and results:
- Scenario IDs, fixture version and assertion/review results:
- Experiment evidence and run identifiers:
- Reasoning-check answers:
- Time spent (setup/build/debug/review):
- Deterministic test results:
- Behavioral evaluation observations:

### Decisions and limitations
- ...

### Next step
Module 3: generalize the tool round into a bounded agent loop
with multi-step execution, explicit terminal states and budgets.
```

[Course Index](README.md) | [Previous: Model Interaction](module_01.md) | [Next: The Agent Loop](module_03.md)
