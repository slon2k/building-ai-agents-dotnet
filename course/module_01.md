# Module 1 — Model Interaction and Application Foundations

[Course Index](README.md) | [Next: Tools and Transaction Data](module_02.md)

## Module purpose

Learn to inspect and test a model interaction before adding tools or an agent loop.

## Module overview

**Estimated effort:** 2–3 hours, with optional exercises.

**Main question:**

> How do we integrate a language model into a .NET application while keeping its behavior inspectable and testable?

We will make a direct model API call, inspect the request and response, then introduce a reusable abstraction.

This module does **not** build an agent yet. It establishes the model-interaction layer that later modules will use.

### Working result

A console application that:

- Sends a user message to a model.
- Displays the response.
- Uses configuration without committing credentials.
- Supports cancellation and basic failure handling.
- Records available usage and timing information.
- Associates requests with explicit session and run identifiers.
- Executes a small set of repeatable baseline scenarios.

---

## Prerequisites

### Development environment

- A supported .NET SDK.
- Your preferred IDE or editor.
- Git.
- Access to either:
  - OpenAI, or
  - Microsoft Foundry with a suitable model deployment.

We will select one provider initially. Supporting another provider is a later comparison exercise, not a prerequisite.

Record the exact SDK, target framework, package versions and provider/model configuration using the [course environment checklist](README.md#environment-and-scope). First verify that an empty solution builds and its test project runs. Configure credentials outside source control before attempting the live request.

`IChatClient` is defined by the `Microsoft.Extensions.AI` abstractions. A provider adapter supplies its implementation; the interface alone does not configure authentication or select a model. Choose compatible package versions using the adapter's documentation and record the versions you install.

### Assumed knowledge

- C# and asynchronous programming.
- HTTP and JSON.
- Dependency injection and configuration.
- Basic automated testing.

No previous agent-development experience is required.

---

## Learning objectives

By the end of this module, you should be able to:

1. Explain the difference between ordinary application logic and probabilistic model behavior.
2. Inspect the messages and parameters sent to a model.
3. Distinguish application instructions from user input.
4. Identify response text, completion status and available usage metadata.
5. Explain what `IChatClient` abstracts—and what remains provider-specific.
6. Handle cancellation and common request failures.
7. Establish a repeatable baseline without assuming identical responses.

---

## Concepts and trade-offs

A model call has two kinds of responsibility. Application code determines the messages, configuration, cancellation and recording policy. The model produces a response whose wording and correctness can vary. Test these responsibilities separately.

### Worked request trace

Consider the question: "How much did I spend on groceries in March 2026?" At this stage, no transaction source exists.

| Stage | Example | What to verify |
| --- | --- | --- |
| Application input | Instruction says not to invent expenses; user asks for a total | Instruction and user input remain separate messages |
| Provider adapter | Sends the messages through the configured `IChatClient` | Model/deployment and credentials come from configuration |
| Model response | Explains that transaction data is unavailable | Review meaning, not one exact sentence |
| Execution record | Session ID, run ID, duration and available usage | A fake client can verify recording without a live request |

After introducing the adapter, compare this trace with the raw HTTP example. The application-facing interface changes; authentication, model capabilities and response variability do not disappear.

Before building, explain why a fake response can verify cancellation propagation but cannot establish that a live model will always acknowledge missing data.

---

## Implementation plan

### Part 1 — Create the project foundation

Create a small solution:

```text
src/
  ExpenseAssistant.Console/
  ExpenseAssistant.Core/
  ExpenseAssistant.Infrastructure/

tests/
  ExpenseAssistant.Tests/

evals/
  scenarios.json

docs/
  decisions.md
  checkpoint.md

COURSE.md
```

Responsibilities:

- **Console:** user interaction and application startup.
- **Core:** application contracts and shared models.
- **Infrastructure:** model-provider integration and, later, persistence.
- **Tests:** deterministic automated tests.
- **Evals:** behavioral scenarios.

Keep the architecture lightweight. Add components only when they serve a concrete purpose.

### Part 2 — Make one raw HTTP request

Implement a direct request to the selected provider.

Inspect:

- Endpoint and authentication.
- Instruction and user messages.
- Model or deployment selection.
- Response content.
- Completion status.
- Available token-usage information.

**Exercise:** Compare the actual request and response JSON with what the application displays.

Do not log credentials or authorization headers.

### Part 3 — Introduce `IChatClient`

Replace the application-facing raw HTTP integration with an `IChatClient` implementation.

Keep the raw example as a learning reference.

Discuss:

- Which provider details disappear behind the interface.
- Which capabilities and configuration remain provider-specific.
- Why a shared interface does not guarantee identical model behavior.

### Part 4 — Add the execution envelope

Introduce:

- `SessionId`: the conversation identifier.
- `RunId`: the identifier for one execution triggered by user input.
- A cancellation token.
- A configurable request timeout.
- Basic error reporting.
- Request duration and available usage metadata.

For now, use one active session. Session persistence and switching belong to Module 5.

These identifiers prepare the application for later isolation and tracing; they do not themselves enforce isolation.

### Part 5 — Establish the assistant’s boundaries

Give the model a small, explicit role:

> Help users understand expense-related questions. Do not invent transaction records or claim to have accessed data that was not supplied.

The application does not yet have a transaction database or tools.

Test the distinction between:

- Explaining how a spending calculation works.
- Calculating from figures supplied in the prompt.
- Claiming knowledge of the user’s actual expenses.

Prompt instructions are behavioral guidance, not an authorization or security boundary.

### Part 6 — Add baseline scenarios

Create `evals/scenarios.json` as a JSON array of scenario objects. Start with the following contract and keep it as the collection grows:

| Field | Meaning |
| --- | --- |
| `id` | Stable, unique identifier |
| `steps` | Ordered objects with `type` and `text`; initially only `userMessage` |
| `checks` | Named observable criteria; each needs an assertion or a review rubric |
| `checkMode` | `manual`, `automated` or `mixed` |
| `expectedBehavior` | Plain-language review guidance, required for manual checks |
| `tags` | Capability labels for selecting subsets |
| `datasetVersion` | Required from Module 2 when a scenario uses transaction fixtures |

Example collection:

```json
[
  {
    "id": "missing-transaction-data",
    "tags": ["model-baseline"],
    "steps": [
      {
        "type": "userMessage",
        "text": "How much did I spend on groceries in March 2026?"
      }
    ],
    "checks": ["acknowledges_missing_transaction_data"],
    "checkMode": "manual",
    "expectedBehavior": "Explains that transaction data is unavailable; does not invent a total."
  }
]
```

For now, a small runner can submit each scenario and save the response for manual review. Do not build Module 7's scoring infrastructure yet. Reject duplicate IDs and malformed records before sending requests.

Initial scenarios:

| Scenario | Expected behavior |
| --- | --- |
| Explain an expense category | Provides a relevant explanation |
| Ask for actual spending without supplying data | States that transaction data is unavailable |
| Request a concise answer | Generally follows the requested format |
| Repeat the same input | May vary in wording while preserving the intended behavior |

Record the model configuration alongside evaluation results.

---

## Tests and experiments

### Deterministic tests

Use a fake model client where appropriate to verify:

- The correct messages reach the client.
- Cancellation is propagated.
- Errors are surfaced without exposing secrets.
- Session and run identifiers accompany execution records.
- The application handles missing optional usage metadata.

Do not make routine unit tests depend on live model calls.

### Live experiments

1. Repeat the same request several times.
2. Change the instruction wording.
3. Ask a question requiring unavailable transaction data.
4. Cancel an in-progress request.
5. Exercise a controlled failure, such as invalid configuration.

For each experiment, record:

- What you expected.
- What happened.
- Whether the explanation lies in application code, configuration or model behavior.

Use the [experiment evidence format](README.md#evidence-for-each-experiment). Include the scenario ID, changed setting, repetitions, review result and run identifiers. Keep cancellation and invalid-configuration checks distinct from judgments about model answers.

### Troubleshooting

| Symptom | First check |
| --- | --- |
| Build cannot resolve `IChatClient` | Check the namespace, package references and adapter version compatibility |
| Request fails authentication | Check configuration loading and the selected endpoint; never print credentials |
| Fake tests pass but a live call fails | Inspect safe provider error details and capabilities; the fake does not exercise the network |
| Usage is absent | Treat it as unknown; do not report zero tokens or zero cost |

### Reasoning check

- What does `IChatClient` standardize, and which configuration remains provider-specific?
- Which evidence would distinguish a recording bug from a model behavior issue?
- Why is "do not invent expenses" useful guidance but not an authorization control?

Record brief answers in the checkpoint and link each answer to a test or observation where possible.

---

## Completion criteria

Module 1 is complete when:

- [ ] The console application successfully calls the selected model.
- [ ] A raw HTTP example is available for inspection.
- [ ] The application uses `IChatClient`.
- [ ] Credentials are kept outside source control.
- [ ] Cancellation and basic failures are handled.
- [ ] Each execution has a session ID and run ID.
- [ ] Duration and available usage information can be inspected.
- [ ] Deterministic tests pass.
- [ ] Baseline scenarios can be rerun without manually retyping inputs.
- [ ] You can explain why this is a model-backed application, not yet an agent.

---

## Checkpoint and handoff

Append the following section to `docs/checkpoint.md`, beneath its `# Course Checkpoints` title. Preserve earlier evidence and date later corrections. Follow the [cumulative checkpoint convention](README.md#keep-a-cumulative-checkpoint).

```markdown
## Module 1 Checkpoint

### Implemented
- ...

### Environment and model configuration
- Provider:
- Model/deployment:
- .NET version:
- Target framework and package versions:

### Verification
- Commit and verification date:
- Build/test commands and results:
- Scenario IDs and review results:
- Baseline observations:
- Experiment evidence and run identifiers:
- Reasoning-check answers:
- Time spent (setup/build/debug/review):

### Decisions
- ...

### Known limitations
- ...

### Next step
Module 2: introduce synthetic transaction data, deterministic
query tools, argument validation and basic tracing.
```

Do not include API keys or other secrets.

---

## Optional extensions

Only attempt these after the completion criteria are met:

- Compare two models using the same baseline scenarios.
- Inspect provider-specific structured-output support.
- Estimate request cost from usage metadata and current provider pricing.

[Course Index](README.md) | [Next: Tools and Transaction Data](module_02.md)
