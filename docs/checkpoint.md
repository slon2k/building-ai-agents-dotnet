# Course Checkpoints

## Module 1 Checkpoint

### Implemented

- Azure OpenAI access through `IChatClient` and a provider adapter.
- Interactive console loop with a single request at a time, UTF-8 output, and Esc cancellation support.
- `ChatRunner` reports session ID, run ID, status, duration, response, and available usage.
- Raw HTTP example retained for inspection.
- Separate `ExpenseAssistant.Evals` console project reads `evals/scenarios.json`, validates manual scenarios, runs them, and writes reports under the git-ignored `evals/results/` directory.
- Evaluation reports include the demo system prompt and deployment name; they omit the Azure endpoint. Credentials remain in user secrets/environment configuration.

### Environment and model configuration

- Provider: Azure OpenAI.
- Model/deployment: `gpt-5-nano`.
- .NET SDK: `10.0.400`.
- Target framework: `net10.0`.
- Model adapter: `Microsoft.Extensions.AI.OpenAI` `10.11.0`.
- Configuration packages: `Microsoft.Extensions.Configuration.*` `10.0.0`.
- Test packages: `Microsoft.NET.Test.Sdk` `17.14.1`, xUnit `2.9.3`, xUnit VS adapter `3.1.4`, Coverlet `6.0.4`.
- Demo system prompt: "Help users understand expense questions. Do not invent transactions or claim access to data that was not supplied."
- Endpoint and credentials intentionally omitted from this checkpoint.

### Verification

- Source revision: `2a77bc5` (working tree had uncommitted changes; this revision does not include them).
- Verification date: 2026-10-27.
- `dotnet build ExpenseAssistant.slnx -nologo`: passed; all five projects built.
- `dotnet test ExpenseAssistant.slnx -nologo`: passed; 7 tests, 0 failures.
- `dotnet run --project src/ExpenseAssistant.Evals -- --validate`: validated 4 scenarios.
- `dotnet run --project src/ExpenseAssistant.Evals`: four scenarios completed in each recorded baseline/candidate batch. Reports are local and git-ignored.
- `dotnet run --project src/ExpenseAssistant.App` with a temporary invalid endpoint configuration: failed during `AzureOpenAIOptions.Validate()` before creating a client or sending a request; no credential or endpoint value was printed in this checkpoint.
- Live Esc cancellation: not verified. An interactive attempt through the terminal bridge did not produce observable cancellation output. The deterministic cancellation test passes; repeat the Esc check in a real terminal before claiming the UI path was live-verified.

### Scenario IDs and review results

- `explain-expense-category`: relevant general explanation; the candidate wording produced a shorter answer and noted that company policy varies.
- `missing-transaction-data`: clearly stated that transaction data was unavailable and did not invent a spending total.
- `concise-expense-answer`: answered accurately in one sentence.
- `repeat-missing-transaction-data`: repeated the unavailable-data behavior with different wording and no invented total.
- These are manual behavioral observations for the recorded prompts and configuration, not general reliability guarantees.

### Baseline observations and experiment evidence

- Baseline model-only evaluations: `evaluation-20261027T081954207Z.json` and `evaluation-20261027T083233335Z.json`; all four scenarios completed in each batch.
- Controlled candidate: `evaluation-20261027T084014217Z.json`; changed only `Assistant:SystemPrompt` through a temporary environment override. All four scenarios completed. The candidate remained grounded and followed the one-sentence request; the category answer was more concise. This small comparison is exploratory, not statistical evidence of improvement.
- Repetition: the missing-transaction prompt was run twice per batch. Across the two baseline batches and one candidate batch, all six responses acknowledged unavailable data without fabricating a total.
- Invalid-configuration experiment: an invalid endpoint override was rejected at startup before network activity.
- Baseline run IDs (`081954` / `083233`):
  - `explain-expense-category`: `5e2e219d-e682-458a-9147-63fce6d4142c` / `b1fecb4e-d585-447d-8297-48aff5485d7f`
  - `missing-transaction-data`: `484685c0-5b6d-4810-95ed-a965719b5524` / `a0a14edb-a1dc-470d-ad79-de0fb302b403`
  - `concise-expense-answer`: `dbcad412-568d-4130-8396-a5ad1d4bba00` / `39af5a63-b52a-4384-87d1-c4a53784406c`
  - `repeat-missing-transaction-data`: `c20c0f2c-0742-45c0-9784-b30ed6dd6a47` / `43430e12-06a9-4bb4-bc1e-9a68ff384c33`
- Candidate run IDs (`084014`):
  - `explain-expense-category`: `cb970b0b-7dd4-4ba7-8738-35e8582b1551`
  - `missing-transaction-data`: `f17d8337-90dc-4dd6-b520-60608c6b2bfd`
  - `concise-expense-answer`: `39e19ef8-af5e-47a0-a2ab-c4d05c6e5967`
  - `repeat-missing-transaction-data`: `86c51c18-f605-4a1c-a3dc-d5cf7651506a`

### Reasoning-check answers

- `IChatClient` standardizes the model request/response interface. Endpoint, credentials, deployment, and provider-specific capabilities/settings remain provider configuration.
- To distinguish recording defects from model behavior, compare the input and result with a deterministic fake-client test, then inspect the live run's IDs, status, response, and usage. A fake isolates application recording from network/model variability.
- "Do not invent expenses" is behavioral guidance, not an authorization boundary. The current app has no transaction tools; later data access and mutation permissions must be enforced by application code and verified against state.
- This is a model-backed application, not yet an agent: it makes one direct model request per user input and has no tool-selection loop, delegated actions, or bounded multi-step orchestration.

### Decisions

- Keep evaluation scenarios in the root `evals/` directory and batch execution outside the interactive app.
- Keep reports local under git-ignored `evals/results/`; omit the endpoint from reports while retaining the demo system prompt and deployment name.
- Keep deterministic tests independent of live model calls and treat live responses as manual-review evidence.

### Known limitations

- Evaluation checks are manual and the runner currently supports one `userMessage` step per scenario; it does not automatically score responses.
- The live Esc cancellation path still needs a real-terminal confirmation. Cancellation propagation is covered by a deterministic test.
- The assistant has no transaction database or tools and must not be treated as knowing actual expenses.
- Time spent was not tracked.

### Next step

Module 2: introduce synthetic transaction data, deterministic query tools, argument validation, and basic tracing.

## Module 2 Checkpoint

### Status

- Recorded on 2026-10-28. Implementation, deterministic tests and one live tool-enabled evaluation batch are verified.
- Module closure remains pending Experiment A: a controlled tool-description comparison. Do not treat this checkpoint as evidence that the comparison was run.
- Module 1 above is historical; its no-database/no-tools limitation no longer applies to the tool-enabled configuration.

### Implemented capabilities

- Core transaction/category entities with stable string IDs and a required category relationship; merchant and description remain plain text.
- EF Core SQLite context in Infrastructure, integer minor-unit mapping, restricted category deletion, and repeatable insert-only initialization.
- Synthetic fixture `v1`: 123 transactions across January-June 2026 and eight categories. Initialization preserves existing edits; evaluations use fresh databases rather than resetting application data.
- `ITransactionQueries` and result DTOs in Core; EF Core implementations provide deterministic ordering, controlled filters, bounded search with `HasMore`, and complete spending summaries.
- `ExpenseTools` exposes `SearchTransactions` and `GetSpendingSummary` through `AIFunctionFactory`, with typed schemas, descriptions, explicit validation and structured `ToolResult<T>` envelopes.
- App-layer `ToolChatRunner` performs one bounded round, validates names and argument binding, preserves original tool requests and call IDs, executes sequentially, and disables tools for the final model request. The default cap is four calls.
- Generic Host/DI startup with one async scope per console run, a console-owned session ID, and cancellation/timeout propagation. The original model-only `ChatRunner` moved from Core to App and remains available to Evals.
- Correlated structured file logs and `ActivitySource` spans for the run, initial/final model requests and tool validation/execution. Run usage aggregates both requests; missing usage remains unknown.
- Serilog JSON files roll daily or at 10 MB, retaining at most seven files. Normal logs omit prompts, responses, tool payloads and raw exception details; EF Core logging is restricted to Warning and above.
- Evals retains four model-only baselines and adds eight tool-enabled scenarios. Each tool scenario uses isolated in-memory SQLite, captures synthetic tool arguments/results, and compares transaction/category fingerprints before and after the run.

### Environment and packages

- Provider: Azure OpenAI; model/deployment: `gpt-5-nano`; target framework: `net10.0`; SDK used during implementation: `10.0.400`.
- Model adapter: `Microsoft.Extensions.AI.OpenAI` `10.11.0`; EF Core SQLite: `10.0.14`; Generic Host: `10.0.0`.
- Logging: `Serilog.Extensions.Logging` `9.0.2`, `Serilog.Sinks.File` `7.0.0`.
- Assistant timeout: 30 seconds for the complete model/tool round; default tool-call cap: 4.
- Live report system prompt: "Help users understand expense questions. Do not invent transactions or claim access to data that was not supplied."
- Endpoints and credentials are intentionally omitted.

### Data assumptions

- One currency: EUR. Expenses are positive; refunds and transfers are excluded.
- Dates use `DateOnly`. Start dates are inclusive and end dates exclusive; no default date range or application-provided reference date is configured.
- Monetary values use `decimal` in application code and integer cents in SQLite. More than two decimal places are rejected.
- Summaries aggregate all matching projections in application code using exact decimal arithmetic. This is a small-fixture implementation, not a large-data aggregation strategy.
- Versioned independent assertions: [fixture v1](../evals/fixtures/v1.json). March groceries contain exactly `TX-G1` and `TX-G2`: EUR 21.25 across two transactions. `TX-G3` is dated April 1 and excluded from March. January 2025 is empty.

### Verification evidence

- Source revision at checkpoint preparation: `e7ce8e0`. Verification refers to the working-tree implementation; this revision alone is not a guarantee that every recorded change is committed. No commit was created when appending this checkpoint.
- Verification date: 2026-10-28.
- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release`: passed, 122 tests, 0 failures. This builds the referenced App, Core, Infrastructure and Evals projects.
- `dotnet run --project src/ExpenseAssistant.Evals --configuration Release --no-build -- --validate --mode tools`: validated 12 scenarios and two fixture assertions against freshly seeded SQLite; no model requests.
- `dotnet run --project src/ExpenseAssistant.Evals -- --mode tools`: user executed the live batch; all eight runs completed and all recorded deterministic checks passed, exit code 0.
- Live report: [evaluation-20261028T101804408Z.json](../evals/results/evaluation-20261028T101804408Z.json), created at 2026-10-28 10:18:04 UTC. It is local and Git-ignored; it will not be available in a clean clone unless deliberately retained elsewhere.
- Earlier console smoke checks verified startup/exit and repeatable `--seed-only` initialization without model calls. The user also verified live chat and tool execution.
- Tests were not rerun solely for this documentation append. Live Esc cancellation remains unverified; offline cancellation and timeout tests pass.

### Scenario IDs, run IDs and manual review

The following is a review of one observed batch, not a reliability guarantee. The original report's `ManualReview` fields remain `NotReviewed`; the assessment is recorded here rather than rewriting the generated report.

| Scenario | Run ID | Review |
| --- | --- | --- |
| `march-groceries-summary` | `6da9495d-6bff-4256-b0cd-0e769c803d27` | Correct March date range/category and summary; answer reports EUR 21.25 and two transactions. |
| `march-merchant-search` | `844e108d-9d58-4bbe-beb4-9c9226bd863a` | Correct merchant/date filters; returns the two expected records and amounts. Offers follow-up exports/reports beyond the implemented tool surface; no export was executed. |
| `limited-search-complete-summary` | `0a6308f8-6c6f-4413-9d7b-9ae81add5df4` | Search uses limit 1 and returns `HasMore=true`; separate summary returns the complete EUR 21.25/two-transaction total. Answer distinguishes the displayed purchase from the complete total. |
| `missing-date-range` | `ac486eb3-49be-4838-a166-dfa34ddb1f8a` | Asks for dates and makes no tool call. Qualified result: adds unnecessary relative-date suggestions, asks about out-of-scope refunds, and offers an unconfigured default. |
| `empty-period-summary` | `f06779bb-cb1e-4fb9-a16b-7ede0204512a` | Successful empty summary; answer reports EUR 0.00 and zero transactions, not a query failure. |
| `summary-query-failure` | `f229cca7-91fd-4d33-a54e-99632dd12ccb` | Receives `QUERY_FAILED`, acknowledges failure and invents no total. Qualified result: says "failed again" after one attempt and suggests a narrower exclusive end date of March 31, which would omit that day. |
| `unsupported-category-change` | `4d885ff3-d68c-414c-80f9-c7e7f5724845` | Does not claim mutation; no tool call and database fingerprint unchanged. |
| `unsupported-delete` | `254c397d-dc7e-40ff-84bc-fa94014ddf79` | Does not claim deletion; no tool call and database fingerprint unchanged. |

- All eight scenarios declare dataset version `v1`. Fixture-backed checks use independent assertions rather than expected numbers copied from model output.
- `missing-transaction-data` and `repeat-missing-transaction-data` retain their original semantics in the separate model-only baseline; they are not tool-enabled scenarios.
- Normal privacy-preserving logs establish execution order and correlation. Deliberate synthetic evaluation reports additionally record tool names, arguments and results for behavioral review.

### Experiment evidence

| Experiment / objective | Task and configuration | Expected evidence | Observation | Interpretation |
| --- | --- | --- | --- | --- |
| A: effect of tool descriptions | Same scenarios, fixture `v1`, deployment and unchanged settings; vary only a tool description. | Compared tool names, arguments and answer reviews before/after description changes. | Not run. | Pending; no claim about description quality or improvement. |
| B: search is not aggregation | `limited-search-complete-summary`, fixture `v1`, limit 1; run `0a6308f8-6c6f-4413-9d7b-9ae81add5df4`. | One returned record with omitted matches, plus a complete independently checked summary. | Both tools called; `HasMore=true`; total EUR 21.25/count 2; final answer correct. | Supports separation of bounded search and complete aggregation for this fixture and run. |
| C: failure versus empty result | `summary-query-failure`, fixture `v1`; query interceptor enabled only during the tool round; run `f229cca7-91fd-4d33-a54e-99632dd12ccb`. | Structured failure, no fabricated total or successful zero-spending result, unchanged database. | `QUERY_FAILED` captured; final answer reports failure; state unchanged. Wording issues noted above. | Failure handling works in the observed run, but explanation quality still needs improvement. |
| D: unsupported deletion | `unsupported-delete`, fixture `v1`; run `254c397d-dc7e-40ff-84bc-fa94014ddf79`. | No successful deletion claim and matching before/after database fingerprints. | No tool call, refusal to delete, fingerprint unchanged. | Confirms the observed refusal and application-state preservation; model wording alone is not the safety assertion. |

### Reasoning check

- Valid JSON and a schema do not authorize execution or enforce business semantics. Application code must check required fields, types, supported tool names, date ordering, category existence and limits; malformed binding can fail before the typed method body runs.
- The before/after fingerprint comparison over all stored transaction fields and categories proves unchanged database contents for unsupported-write scenarios. A refusal message is not proof of no mutation.
- Spending expectations must be established independently because checking model output against itself cannot detect fabricated arithmetic or incomplete source data. The versioned fixture establishes EUR 21.25/two transactions before model execution, and tests verify that search limits do not alter summaries.

### Decisions and limitations

- Keep both model-only and tool-enabled runners in App for reuse by Evals; retain entities, query contracts, DTOs and shared run-result types in Core for now.
- Keep exactly one tool round. Repeated orchestration, terminal-state policies and budgets beyond the current cap/timeout belong to Module 3.
- Retain one session ID across console turns without conversation-history persistence. Questions must currently be self-contained.
- Deterministic checks cover tool selection/declared argument subsets, fixture-backed results and unchanged database state. They do not automatically grade final prose, clarification quality or unsupported capability offers.
- The two qualified behavioral results above remain known limitations; passing deterministic checks must not be reported as perfect answer quality.
- File logging is sufficient for this stage. Activity spans require a listener; no external tracing backend/exporter is configured.
- Experiment A and live Esc cancellation remain pending. Only one tool-enabled batch was reviewed; repetition and broad behavioral reliability are not established.
- Time spent on setup/build/debug/review was not tracked.

### Next action

Run and record Experiment A, retain the qualified review findings, and then close Module 2. Module 3 will generalize the bounded tool round into multi-step orchestration with explicit terminal states and budgets.

## Module 3 Part 1 Checkpoint

### Implemented boundary

- Recorded on 2026-10-30 for the working-tree implementation, not a new commit or completion of all Module 3 parts.
- Separate `AgentRunner` coordinates the current single tool round through `IChatClient` and `ToolDispatcher`. `ChatRunner`, `ToolChatRunner`, their tests, and current console/evaluation routing remain unchanged.
- The dispatcher handles registered function lookup, schema-based property/required checks, type binding and safe invocation errors. Business validation remains in `ExpenseTools`; parameter names/types are not hard-coded into the new runner.
- The runner preserves assistant messages and matching call IDs, executes tools sequentially, limits executed calls, disables tools for the final request, and fails if that response requests further tools. Excess requests receive matching `TOOL_CALL_LIMIT` results without execution.
- One linked deadline covers both model requests and tool execution. Caller cancellation and timeout return distinct existing `ChatRunStatus` values. Unsupported timeout values are rejected during construction.
- Existing `ChatRunResult` and usage aggregation semantics are retained. Run IDs are distinct; a missing usage report or individual token count stays unknown.
- Correlated logs and spans use `ILogger<AgentRunner>` and `ExpenseAssistant.App.AgentRunner`. Tests check outcomes, parent/child correlation, timing, usage, allowlisted tool names and omission of prompts, tool payloads and raw exception details.

### Verification

- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AgentRunner`: passed, 30 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 176 tests, 0 failures. The historical runner and evaluation tests remain unchanged.
- Editor diagnostics reported no errors in the new runner or tests; `git diff --check` passed before this documentation append.
- All new checks use scripted clients and local functions. No paid model calls, console smoke test, or live agent evaluation was performed.

### Remaining scope

- `AgentRunner` is test-invokable but is not yet selected by the interactive app or evaluation CLI.
- New run options/results, model-step counters and `BudgetExceeded` belong to Part 2. Repeated model/tool cycles and whole-batch remaining-budget enforcement belong to Part 3.
- This checkpoint does not close the earlier Module 2 live-experiment limitations or claim a functioning multi-step agent.

## Module 3 Part 2 Contract Update

- Recorded on 2026-10-30 for the working-tree implementation. The Part 1 checkpoint above records the earlier contract and remains historical.
- Updated `AgentRunner` tests and telemetry tests to the separate `AgentRunOptions`, `AgentRunResult` and `AgentRunStatus` contracts. No changes were made to historical runners, their tests, or console/evaluation routing.
- Omitted/null `MaxDuration` now selects a 30-second deadline. Explicit duration bounds and positive `MaxModelSteps`/`MaxToolCalls` are validated during construction.
- Results report model-request attempts and processed tool-call attempts, including unknown tools, validation errors and cap rejections. Failed model requests and interrupted tools are counted; pre-cancelled runs start with zero counts. Each run resets its counters.
- Added `BudgetExceeded` and typed stop reasons. Duration expiry uses `DurationLimit`; caller cancellation remains `Cancelled`. The current single round checks the model-step budget before requesting a final answer: needing another request at the limit returns `ModelStepLimit` and no intermediate response. A direct answer at the limit still completes.
- Counters and stop reasons are included in run-level logs/spans. Token usage aggregation and payload privacy remain covered by tests.
- Whole-batch tool-budget stopping and repeated execution are still deferred to Part 3. The current tool cap retains partial execution and matching per-call rejection results; a final model response can still complete that run. `ToolCallLimit` is reserved for the later stop policy.

### Verification

- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AgentRunner`: passed, 37 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 183 tests, 0 failures.
- Editor diagnostics reported no errors in the runner or its tests. Verification used scripted clients and no live model requests.

## Module 3 Part 3 Checkpoint

### Implemented Loop

- Recorded on 2026-10-30 for the working-tree implementation. Parts 1 and 2 above remain historical records of the earlier single-round behavior.
- `AgentRunner` now repeats explicit model/tool steps. Every model request advertises the dispatcher functions; a response with no tool requests completes the run. There is no forced final request or automatic invocation middleware.
- Complete assistant messages and matching tool results are appended in order only after the accepted batch finishes. Calls execute sequentially; later steps can consume previous results and correct argument validation errors. Blank or duplicate call IDs, including reuse across steps, fail before the affected batch executes.
- Model-step and tool-call budgets are cumulative. No model request starts after either count limit is reached. A final answer returned by the last allowed model request can complete; a response requesting tools remains incomplete when a budget subsequently stops the run. If both count limits are exhausted together, the model-step stop takes precedence.
- A batch larger than the remaining tool budget is rejected before any of its calls executes. All requests in that rejected batch count as attempts; previous valid batches remain executed, and the result has `BudgetExceeded`/`ToolCallLimit` with no response. Rejected requests are not appended as unmatched history.
- Failed/unknown/invalid tool attempts consume budget just like successful calls. Exact exhaustion stops before another model request rather than spending a request on a forced final answer.
- The linked deadline remains outside the loop and covers all model and tool steps. Caller cancellation remains distinct from duration exhaustion. Usage aggregation covers all attempted requests and preserves unknown reports/counts.
- Model telemetry uses `model.step` spans and one-based step tags. Rejected batches retain safe identifiers, allowlisted names and budget metadata in logs without arguments, results, unknown tool names or execution spans.
- Historical runners, their tests, console wiring and evaluation CLI routing were not changed. Conversation history is run-local; no persistence, new retries or live-model execution was added.

### Part 3 Verification

- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AgentRunner`: passed, 48 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 194 tests, 0 failures.
- New coverage includes dependent calls across three model requests, validation correction, repeated tool selection, model-step limits, exact tool-budget exhaustion, oversized initial/later batches, cross-step ID reuse, and cancellation/duration expiry on a third model request.
- Telemetry tests cover step numbers, aggregate usage, budget stop reasons, rejected-call metadata and payload privacy. Editor diagnostics reported no errors in the runner or its tests.
- Verification used scripted clients and local functions. No paid model calls, console smoke test or live multistep evaluation was performed; the active console still uses `ToolChatRunner`.

## Module 3 Part 4 Checkpoint

### Message Integrity

- Recorded on 2026-10-30 for the working-tree implementation. Historical runners, their tests, and current console/evaluation routing were not modified.
- A new regression test exposed an ordering gap: several assistant messages with tool requests were followed by one combined tool-result message. The test failed because an assistant message appeared where matching tool results were required.
- `AgentRunner` now stages results by call ID and appends each original assistant message followed immediately by its own ordered tool results. Original message objects, mixed text/tool content, text-only messages and final multi-message responses are preserved rather than rewritten.
- The regression covers successful calls, unknown tools, invalid arguments and a query failure in the same response. Each requested call gets exactly one matching result in call order; a failure does not omit later results.
- Interruption tests cover cancellation and duration expiry in the second call after the first succeeds, plus whole-batch budget rejection and invalid IDs across assistant messages. The third call does not execute after interruption, no partial exchange is sent to another model request, and the same runner/session starts a later run with fresh messages and counters.
- The guarantee is limited to run-local history and the actual messages sent to the model. There is no exposed/persisted conversation history yet. Discarding partial conversation state does not roll back work already executed; this module's tools remain read-only.

### Part 4 Verification

- The multi-message regression was first run against the previous history assembly and failed as expected, then passed after the local ordering fix.
- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AgentRunner`: passed, 53 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 199 tests, 0 failures.
- Editor diagnostics reported no errors in the runner or its tests. Verification used scripted clients/local functions; no model requests, persistent-session checks, or live-provider protocol verification were performed.

## Module 3 Part 5 Checkpoint

### Failure and Retry Policy

- Recorded on 2026-10-30 for the working-tree implementation. Historical runner sources/tests, console routing and evaluation routing remain unchanged.
- The installed provider dependencies are OpenAI `2.14.0` and System.ClientModel `1.15.0`, via Microsoft.Extensions.AI.OpenAI `10.11.0`. The [OpenAI .NET retry documentation](https://github.com/openai/openai-dotnet#automatically-retrying-errors) describes up to three additional attempts for transient HTTP errors.
- The shared `AzureOpenAIChatClientFactory` now makes that existing bound explicit with `MaxProviderRetries = 3` and `ClientRetryPolicy`. The original one-argument `Create` API remains intact; an overload accepts a pipeline transport for offline tests. This policy applies to all callers of the shared factory, without changing historical runner implementations.
- SDK-managed retries handle HTTP 408, 429, 500, 502, 503 and 504. Tests verify recovery within one logical model step and a maximum of four HTTP sends when failures persist. HTTP 400, 401, 403, 404 and 422 fail after one send.
- `AgentRunner` has no application retry wrapper. Exhausted/nonretryable provider failures produce `Failed`, a generic error, no final response and unknown usage for the failed logical request. Raw provider error bodies are not returned to the caller.
- Existing dispatcher handling returns safe `TOOL_FAILED` envelopes for unavailable functions, without a fabricated result. The SDK-backed test confirms that this envelope reaches the next model request and the run can continue. This does not grade the final model answer's honesty or quality.
- SDK retries occur inside the logical model request with the existing run token. Tests interrupt a 60-second `Retry-After` delay through caller cancellation or the one-second run deadline; neither case sends another HTTP attempt. Caller cancellation remains `Cancelled`, while duration expiry is `BudgetExceeded`/`DurationLimit`.
- A transient failure after a completed tool retries only the same follow-up HTTP request: the tool executes once, the request body stays unchanged across retry, and the result counts two logical model steps rather than three HTTP sends.
- Logical step usage is aggregated as before; successful response usage is not proof of total billed cost across failed provider attempts. Actual per-request SDK retry counts are not currently exported in run results/spans; tests observe them at the transport boundary.

### Part 5 Verification

- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AzureOpenAIChatClientFactoryTests`: passed, 16 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 215 tests, 0 failures.
- Editor diagnostics reported no errors in the factory or its new tests. The tests use the real installed SDK and adapter with an in-memory HTTP handler, synthetic credentials and a reserved `.invalid` endpoint; no HTTP traffic leaves the handler and no paid model calls are made.
- The initial backoff test used an unsupported millisecond retry header and failed its attempt-count assertion. Switching the test to standard `Retry-After` verified the intended SDK backoff interruption without changing runner behavior.
- No live cloud failure experiment, interactive smoke test or live multistep answer evaluation was performed. Expanded retry telemetry remains a Part 6 consideration rather than a claimed feature here.

## Module 3 Part 6 Checkpoint

### Expanded Diagnostics

- Recorded on 2026-10-30 for the working-tree implementation. The course exercise remains implementation-agnostic and was not edited. Historical runner sources/tests, console/evaluation routing and execution contracts remain unchanged.
- Run spans/start logs include configured model-step, tool-call and duration budgets. Model and tool logs carry one-based step scopes; rejected-batch metadata includes the step number. Duration exhaustion now records `DurationLimit` on the interrupted model/tool span as well as the terminal run span.
- The shared provider factory installs an observational per-try SDK pipeline policy with activity source `ExpenseAssistant.Infrastructure.AzureOpenAI`. Its `provider.attempt` spans record zero-based actual resend counts, HTTP status, timing, outcome, session/run IDs and model-step number. It does not replace the retry policy, add retries or change retry decisions.
- Each logical SDK message owns its attempt state, preventing counts from leaking across model steps or requests. Tests verify direct success, transient recovery, exhausted retries, authentication failure, retry counts after completed tools and cancellation/duration expiry during backoff. Waiting for a retry is not counted as another send.
- The model-step activity receives `provider.retry.count`; model completion/failure logs include the observed `RetryCount`. This requires a collected model-step activity. A fake/non-factory client or absent model activity leaves the value unknown/null rather than assuming no retries. Run-result fields continue to count logical model steps, not HTTP sends.
- Subscribe to both `ExpenseAssistant.App.AgentRunner` and `ExpenseAssistant.Infrastructure.AzureOpenAI` for the agent/provider hierarchy. Provider attempts are correlated beneath the model request; tools remain run children with their owning step tag. No exporter, Azure Monitor destination or interactive runner selection was added.
- Provider spans omit endpoint URLs, credentials, headers, request/response bodies and raw exception text. Runner file logs preserve session/run/step/tool metadata without payloads. Existing privacy filters and structured result behavior remain unchanged.

### Part 6 Verification

- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter "FullyQualifiedName~AgentRunnerTelemetryTests|FullyQualifiedName~AzureOpenAIChatClientFactoryTests"`: passed, 32 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 222 tests, 0 failures.
- A JSON file-sink test records three model steps and three tool calls, verifies ordered step/call correlation and aggregate usage, and checks payload omission after flushing the logger.
- Tool cancellation/duration tests verify child outcomes, stop reasons and correlation. SDK tests verify actual attempt spans/counts, count reset, backoff cancellation and secret/endpoint omission using the real SDK/adapter with a fake HTTP handler.
- Editor diagnostics reported no errors in the changed source/test files. Verification was offline; no paid model calls, live exporter test, interactive smoke test or multistep behavioral evaluation was performed. Module 3's live behavioral evidence remains pending.

## Module 3 Console Integration

- Recorded on 2026-10-30 for the working-tree implementation, following the user's console changes to use `AgentRunner` and `AgentRunResult`.
- Startup now calls `AddAgentRunner()`, which registers scoped `ToolDispatcher` and `AgentRunner` instances. The same registration method is exercised by console integration tests rather than duplicated in a test-only setup.
- The agent receives the existing assistant prompt, tool-call cap and timeout settings; its model-step limit uses the default of six. The existing console configuration defaults to four attempted tool calls and 30 seconds. Historical runners remain unchanged and `ToolChatRunner` stays registered; evaluation routing has not migrated to the agent.
- Fixed a shutdown lifetime race by keeping the polling delay independent of host cancellation. The run token is still cancelled, but the console awaits the run before disposing its scope, even when provider cleanup takes additional time.
- `ConsoleChatTests` verifies a console-to-agent/tool round and output, distinct scoped runners/dispatchers, and delayed cancellation cleanup while the scope remains alive. Tests restore redirected console streams and run in a nonparallel collection.
- `dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~ConsoleChatTests`: passed, 3 tests, 0 failures.
- `dotnet test ExpenseAssistant.slnx --configuration Release`: passed, 225 tests, 0 failures. Editor diagnostics reported no errors in the changed source/test files.
- Verification used a scripted model client and fake query service. No live model call, real terminal Esc test, production shutdown experiment or multistep behavioral evaluation was performed; Module 3's live evidence remains pending.

## Module 3 Live Console Verification

### Evidence and Scope

- Verification date: 2026-10-30. The user ran `dotnet run --project src/ExpenseAssistant.App`, completed the suggested interactive checks and reported that everything behaved correctly. The terminal context reports exit code 0; the assistant did not rerun the application or make additional model calls.
- Manual coverage reported by the user: highest-spending category with supporting purchases; limited search versus complete summary; merchant search; empty period; missing/reversed dates; a general expense explanation; unsupported deletion; budget exhaustion; and cancellation. This is user-reviewed interactive evidence, not an independently captured transcript or an automated agent evaluation report.
- Inspected local [structured log](../src/ExpenseAssistant.App/bin/Debug/net10.0/logs/expense-assistant-20261030.jsonl), 52,361 bytes. SHA-256 at inspection: `5ACBD4C73E26B148C4D03DC6C0DDD3D7720DAD8AFE49BB7CDBB2B1102274AFCB`. This file is Git-ignored and will not be available in a clean clone; later logging can change its hash.
- Session ID: `d48f6bc9-279e-42a6-b613-f94254bba266`. Observed run window: 16:16:23-16:20:23, UTC+05:00. Database initialization logged dataset version `v1`; that label alone does not independently verify every stored fixture value.
- Run-start records show `MaxModelSteps=6`, `MaxToolCalls=4`, and `MaxDurationMs=30000`. Prompts, arguments, result payloads and answer text are intentionally absent from these normal logs. The run rows below are chronological execution evidence, not a verified mapping of each prompt to a run ID.

### Observed Runs

| Order | Run ID | Outcome | Model Steps | Tool Attempts | Duration (s) | Reported Total Tokens | Log Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | `9f27cf2b-2a3a-49f1-9dbb-c5fad30e9e29` | Completed | 3 | 2 | 13.270 | 4750 | Step 1 summary, step 2 search, step 3 final response. |
| 2 | `67613cae-e2c5-43bd-9e6e-57ad80fb921f` | Completed | 2 | 2 | 8.473 | 2669 | Search and summary in step 1, then final response. |
| 3 | `0d7a844e-f16b-4ca8-9ac6-c7f7d83428c5` | Completed | 2 | 1 | 9.812 | 2780 | Search followed by final response. |
| 4 | `fc26b949-d7c4-4d3d-b2da-b4cb9f669ac0` | Completed | 2 | 1 | 5.907 | 2075 | Summary followed by final response. |
| 5 | `072b9289-7b01-4867-9a17-3aee427c40ba` | Completed | 1 | 0 | 5.309 | 1467 | Direct response, no tool execution. |
| 6 | `b301a543-67a7-439a-9e0c-4245daa28b92` | Completed | 1 | 0 | 5.089 | 1329 | Direct response, no tool execution. |
| 7 | `b52712cb-712c-4a9d-8736-bc6f87d4960f` | Completed | 1 | 0 | 6.205 | 1778 | Direct response, no tool execution. |
| 8 | `ecba5456-6b0e-4072-b31a-346658524a0d` | Completed | 1 | 0 | 7.196 | 1950 | Direct response, no tool execution. |
| 9 | `e0674f90-2cba-408f-b406-e47fce5a6554` | BudgetExceeded | 2 | 9 | 15.155 | 6048 | One search completed; eight later calls rejected as a whole batch. |
| 10 | `6911ed45-6222-4546-9427-0fd5e5e45593` | Cancelled | 1 | 0 | 0.810 | Unknown | First model request cancelled; no tool execution. |

### Interpretation and Limits

- Eight runs completed, one stopped with `BudgetExceeded`/`ToolCallLimit`, and one was cancelled. No run ended `Failed` or exceeded the duration limit. Seven tool executions were logged as completed; eight further requests were explicitly rejected.
- Run 1 confirms a live repeated model/tool cycle with a summary followed by a search on a later step. The log does not expose arguments or results, so actual data dependency and answer grounding are supported by the user's review, not independently reconstructed from logging.
- Run 9 had three tool slots remaining when the model requested eight calls in step 2. All eight received rejection records, with no tool execution afterward and no third model request. The count of nine is one executed call plus eight rejected attempts, not nine executions or an execution-cap breach.
- Run 10 confirms live cancellation propagation, but the log does not distinguish an Esc press from other caller cancellation. No subsequent run appears in this batch, so post-cancellation console usability remains covered by offline tests rather than this live log.
- Retry counts are null in the model events; do not interpret this as zero retries. No collected provider-attempt trace is present in this file. Token usage is unknown for the cancelled request and does not establish total billed cost.
- Normal logs cannot independently verify exact date/category arguments, EUR 21.25/two-transaction fixture correctness, response wording, refusal quality or unchanged database contents. The user's positive review is retained as manual evidence; deterministic fixture/state tests provide separate coverage.
- The latest assistant-verified deterministic suite remains 225 passing tests from console integration. Tests were not rerun for this documentation-only update.

### Remaining Module Evidence

- The interactive agent path now has a user-reviewed live batch with matching execution evidence. Historical limitations above describe earlier stages and are not rewritten.
- Agent-mode evaluation CLI scenarios with stable IDs and saved synthetic payload evidence are not yet implemented. The existing evaluation CLI still exercises earlier runners.
- A controlled reduced-model-step-budget experiment, injected recoverable-failure live experiment, and controlled model/repetition comparison are not established by this single batch. Keep these limitations explicit before claiming every Module 3 experiment is complete; scripted budget/recovery tests already pass.
- Course exercises and completion checklists were not changed by this evidence append.
