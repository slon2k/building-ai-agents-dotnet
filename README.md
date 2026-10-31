# Building AI Agents with .NET

A hands-on course for building an AI expense assistant in C# and .NET. Extend one application through seven modules, from an inspectable model call to a bounded agent with retrieval, persistent conversations, approval-gated actions and repeatable evaluations.

This repository starts as a personal training project. The materials are organized so other developers can follow the same path.

## Current status

The seven course modules are available. Application code, a runnable starter solution and CI are not included yet. SDK/package compatibility and the course time estimates have not been validated through an implementation or learner pilot.

## What you will build

A local console application that can:

- Answer expense questions using validated tools and deterministic calculations.
- Find candidate transactions through semantic search.
- Run multiple model/tool steps within application-enforced limits.
- Resume isolated conversations and manage their context budgets.
- Propose changes that require explicit approval before execution.
- Record traces, audit changes and compare behavior against a baseline.

The project uses synthetic transactions, not personal financial data. It is a learning application, not a production-ready financial service.

## Course modules

Complete the modules in order, extending the same application rather than starting a new project each time.

| Module | Focus |
| --- | --- |
| [1. Model Interaction and Application Foundations](course/module_01.md) | Model requests, client abstraction, cancellation and baseline scenarios |
| [2. Tools, Transaction Data and Deterministic Business Logic](course/module_02.md) | SQLite, read-only tools, validation and exact calculations |
| [3. The Agent Loop and Bounded Execution](course/module_03.md) | Multi-step orchestration, execution budgets and explicit outcomes |
| [4. Retrieval, Embeddings and Semantic Search](course/module_04.md) | Exact filters, semantic candidates and retrieval evaluation |
| [5. Sessions, Persistence and Context Management](course/module_05.md) | Conversation isolation, persistence, context budgets and summaries |
| [6. Safe Actions, Confirmations and Trust Boundaries](course/module_06.md) | Proposals, trusted approvals, revalidation and atomic execution |
| [7. Evaluations, Diagnostics and Engineering Review](course/module_07.md) | Isolated fixtures, failure analysis and controlled comparisons |

See the [course guide](course/README.md) for learning outcomes, provisional effort estimates, evidence conventions and final demonstration criteria.

## Prerequisites

- Familiarity with C#, asynchronous programming, HTTP, JSON, dependency injection and basic automated testing.
- A supported .NET SDK, an editor and Git.
- Access to one model provider; later modules also require an embedding-capable model.
- A personal usage budget for live model requests.

Previous agent-development experience is not required. Record the SDK, package versions and model configuration you actually verify rather than assuming every provider supports identical capabilities.

## Getting started

1. Read the [course guide](course/README.md), including its environment and scope section.
2. Start [Module 1](course/module_01.md) and create the learner solution as described there.
3. Implement one part at a time, running deterministic tests separately from live model experiments.
4. Append each module's evidence, decisions and limitations to the cumulative checkpoint before moving on.

As implementation progresses, application code belongs in `src/`, deterministic tests in `tests/`, scenarios and synthetic fixtures in `evals/`, and learning records in `docs/`. Introduce these folders when needed. Build and run commands will be documented when the solution exists.

### Application startup

The console app uses the .NET Generic Host and dependency injection. Configuration is loaded from `appsettings.json` beside the application output, then User Secrets, then environment variables; later sources override earlier ones. Model configuration is validated only when the chat workflow is resolved, so `--seed-only` does not require model credentials.

`IChatClient` and `ConsoleChat` are singletons for this single-session console process. The console owns the session ID across turns and creates an async service scope per question. `AgentRunner`, `ToolDispatcher`, `ExpenseTools`, `ITransactionQueries`, and `ExpenseDbContext` are scoped to that run and disposed afterward. `AddAgentRunner()` registers the agent and dispatcher using the existing assistant settings: `SystemPrompt`, `MaxToolCalls`, and `RequestTimeoutSeconds` (mapped to the run duration); the model-step limit uses the agent default of six. The console tool cap therefore defaults to four, while standalone `AgentRunOptions` defaults to ten. Database initialization uses a separate short-lived scope. The historical `ChatRunner` and `ToolChatRunner` remain available; Evals still uses them for model-only and tool-mode scenarios respectively.

Host shutdown cancellation flows to console input and active chat requests. During a run, the Esc polling delay is not itself cancelled by host shutdown: the active run must finish cancellation/cleanup before its scope is disposed. EF Core logging is limited to warnings and errors to avoid logging SQL and transaction payloads by default.

### Seed the local database

Initialize SQLite without model credentials or a model request:

```shell
dotnet run --project src/ExpenseAssistant.App -- --seed-only
```

Normal application startup also runs initialization. By default, `expense-assistant.db` is created beside the application output. Override the SQLite connection string through `ConnectionStrings:Expenses` in configuration or the `ConnectionStrings__Expenses` environment variable.

The fixed synthetic fixture `v2` contains 200 transactions across January-June 2026 and eight categories, with 83 distinct descriptions and plausible fixed prices. It includes 97 everyday expenses, 50 clear semantic candidates, 25 ambiguous purchases, 25 misleading word-overlap examples, and the three preserved groceries records. Recurring purchases reuse descriptions and prices across months. Concepts include desk/collaboration equipment, digital entertainment, leisure travel, client meetings, home maintenance, and exercise; the fixture design groups are not stored relevance labels or proof of purchase purpose. All expenses are positive EUR amounts, with no refunds or transfers. Amounts use `decimal` in application code and integer cents in SQLite; more than two decimal places are rejected. The March groceries fixture contains exactly `TX-G1` and `TX-G2`, totaling EUR 21.25; `TX-G3` is dated April 1 and is excluded from March. Semantic date-boundary examples are `TX-C001` (March 1), `TX-C002` (March 31), and `TX-C003` (April 1).

Initialization inserts missing fixture IDs atomically and preserves existing records, including edited categories. It is not a reset command: use a fresh database for baseline evaluations and tests. The initializer uses `EnsureCreatedAsync`, not migrations; it does not upgrade an existing schema.

`ExpenseDbContext` also maps `EmbeddingIndexMetadata` (key: `IndexId`) and `TransactionEmbeddings` (composite key: `IndexId`, `TransactionId`). Each vector belongs to an existing index and transaction; deleting either parent cascades to its dependent vectors, never from an index to transactions. Vectors are stored as JSON text; generation and vector-content validation are separate from these EF mappings. New databases include these tables, but existing databases require an explicit schema upgrade or a fresh database because `EnsureCreatedAsync` does not add them.

The `v2` generated IDs use `TX-E`, `TX-C`, `TX-A`, and `TX-D` prefixes rather than reusing the old `TX-001`-style IDs. Initializing an existing complete `v1` database therefore retains its 120 older generated records and adds 197 new records (320 total), without overwriting descriptions or user edits. To obtain exactly the reviewed 200-record fixture, select a new SQLite database through `ConnectionStrings:Expenses`; preserve the previous database if its edits matter. The logged dataset version identifies the seed definition, not a guarantee that an existing database matches it.

Run the database tests with:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~ExpenseDbContextTests
```

### Transaction queries

Core defines `ITransactionQueries` and read-only result records; Infrastructure implements it with `TransactionQueries`. Both operations use an inclusive start date and exclusive end date, accept an optional exact category ID, and pass cancellation through to EF Core. Unknown or blank category IDs and invalid date ranges are rejected with argument exceptions.

Search returns transaction DTOs ordered by date, then ID. The default limit is 20, with allowed limits of 1-100. `ReturnedCount` describes the returned subset; `HasMore` indicates omitted matches, determined by fetching one extra row. Merchant filtering uses a trimmed, literal substring with SQLite's default ASCII case-insensitive `LIKE` behavior. Percent and underscore characters are escaped, not treated as wildcards; blank merchant filters are rejected.

Summaries have no result limit. They load all matching amount/category/currency projections and aggregate exact `decimal` values in application code, avoiding SQLite decimal-aggregation limitations. This is appropriate for the small course fixture, not a large-data aggregation strategy. The configured currency defaults to EUR, including empty results; matching transactions in another currency cause a failure rather than a misleading combined total.

Database and cancellation failures propagate from the query service; they are not converted into successful empty results. `ExpenseTools` wraps successful data in `ToolResult<T>`, returns structured validation errors and safe `QUERY_FAILED` errors, and preserves cancellation.

Run the query tests with:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~TransactionQueriesTests
```

### Bounded tool round

This section describes the retained Module 2 `ToolChatRunner`, still used by tool-mode evaluations. The interactive app now uses the Module 3 agent loop described below.

Each question advertises the two read-only functions to the model. A direct answer needs one model request. If tools are requested, the runner validates names, required dates, argument types and supported parameters, then executes them sequentially. Every result retains its original call ID, and the original assistant messages are preserved in the final request.

`Assistant:MaxToolCalls` defaults to 4. Calls beyond that cap receive `TOOL_CALL_LIMIT` results without execution. Missing/malformed arguments and unknown names receive structured errors rather than query results. One final model request has tools disabled; any further tool request fails the run without executing another tool. No automatic invocation middleware or repeated agent loop is enabled.

`Assistant:RequestTimeoutSeconds` bounds the complete round, including tool execution and the final model request. Caller cancellation and timeouts have distinct run outcomes. Session IDs persist across console turns, but conversation history is not yet retained between turns.

Run the offline orchestration tests with:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~ToolChatRunnerTests
```

### Module 3 runner boundary

The exercises retain separate runners so the progression remains visible: `ChatRunner` is the model-only example, `ToolChatRunner` is the self-contained single tool round, and `AgentRunner` implements Module 3. The console now uses `AgentRunner`; the evaluation CLI retains its model-only and single-round baseline paths. An agent evaluation path has not yet been added.

Part 1 introduced `AgentRunner` with a separate `ToolDispatcher`. The dispatcher owns function lookup, schema-based required/property checks, argument binding, and safe invocation errors; business validation remains in `ExpenseTools`. The runner owns message ordering, execution budgets, the shared request deadline, usage aggregation, and diagnostics. Neither historical runner was refactored to use the dispatcher.

Part 2 established separate `AgentRunOptions` and `AgentRunResult` contracts. Options default to six model steps and ten tool calls; an omitted/null `MaxDuration` selects a 30-second deadline. Explicit durations must be positive and supported by the cancellation timer, and both count limits must be positive. Results include model-request attempts, tool-call attempts (including validation and whole-batch budget rejections), usage, and an optional typed stop reason. Invalid call IDs fail before tool attempts are counted. Duration expiry returns `BudgetExceeded` with `DurationLimit`; caller cancellation remains `Cancelled`.

Part 3 repeats model/tool cycles until a response contains no tool requests or a terminal condition occurs. Tools remain available on every model step. Calls execute sequentially, and complete assistant/tool exchanges are appended before the next request so later calls can use earlier results or correct validation errors. Call IDs must be nonblank and unique throughout the run. An oversized batch is rejected in full before any call in it executes; its assistant requests are not appended to history. Rejected calls count as attempts, so the reported count can exceed the configured execution cap even though actual executions never do.

No additional model request is initiated after either count budget is exhausted. Stops return `BudgetExceeded` with `ModelStepLimit` or `ToolCallLimit`, and never expose an intermediate response as a completed answer. A direct answer on the last allowed model step still completes; if both count limits are exhausted together, `ModelStepLimit` takes precedence. One deadline spans all steps and tool executions rather than restarting each cycle. Conversation history remains local to the run; persistence is not implemented.

Part 4 preserves each original assistant message and inserts its matching tool results immediately after it, including when a single model response contains several assistant messages. Result ordering follows call ordering within that message; successful, unknown-tool, validation-error and query-failure results are all retained. Results are staged until the accepted batch finishes, so cancellation or timeout after some calls complete does not send a partial exchange to another model request. A later run starts with fresh history and counters. Discarding partial conversation state does not undo operations already executed; persistent/reusable session history is still outside this implementation.

Part 5 keeps tool errors recoverable within the remaining budgets: unknown tools, invalid arguments and unavailable functions return safe structured failures, not successful empty data. Unexpected function failures use `TOOL_FAILED`; query failures retain `QUERY_FAILED`. Provider request failures terminate the run with a generic safe error after the SDK's retry policy is exhausted. Configuration validation remains at startup; authentication/authorization failures are not retried.

`AzureOpenAIChatClientFactory.MaxProviderRetries` explicitly sets the provider SDK to at most three additional HTTP attempts (four sends total per logical model request), preserving its documented default bound. HTTP 408, 429, 500, 502, 503 and 504 are retryable with SDK-managed backoff; 400, 401, 403, 404 and 422 are not retried. There is no runner/application retry wrapper, so retries do not restart completed tools or multiply through another retry layer. The same run cancellation token and deadline cover SDK retry backoff and tool execution. `ModelStepCount` counts logical model calls, not HTTP attempts. Retry counts are diagnostic metadata rather than result fields, and usage reports cannot establish the complete billed cost of failed HTTP attempts. The original factory API is retained; a transport-injection overload supports offline HTTP tests without network activity.

The new runner exposes correlated spans through `ExpenseAssistant.App.AgentRunner` and uses `ILogger<AgentRunner>` without logging prompts, response text, or tool payloads. Model requests use `model.step` spans with a one-based `model.step` tag; tool spans carry the corresponding step number. Run diagnostics include counters and stop reasons. Rejected batches log safe call IDs, allowlisted tool names and budget metadata without execution spans. The dispatcher is constructed from `new ExpenseTools(queries).CreateFunctions()` in the scoped startup registration. Console integration is covered by offline scripted-client tests and a [user-reviewed live console batch](docs/checkpoint.md#module-3-live-console-verification). Normal logs confirm execution outcomes, not answer semantics; automated agent evaluation scenarios and controlled live experiments remain pending.

Part 6 adds configured budgets to the run span and start log, step scopes to model/tool file events, and duration stop reasons to interrupted child spans. The shared provider factory adds a separate `ExpenseAssistant.Infrastructure.AzureOpenAI` activity source with `provider.attempt` spans inside each logical model request. Attempt spans record HTTP status, duration, outcome, correlation IDs, model-step number and a zero-based `retry.count`: zero is the first send, one is the first resend. Counters reset for each logical request; cancellation during backoff before another send does not increment them. The pipeline policy observes attempts without changing retry decisions or adding retries.

Subscribe an `ActivityListener` or OpenTelemetry collector to both sources for the complete agent/provider trace. The last observed resend count is also tagged as `provider.retry.count` on the model-step span and included as `RetryCount` in its completion/failure file event. A collected model-step span is needed to carry that metadata; without one, or with a client that does not use this factory, retry count stays unknown/null rather than being reported as zero. File logging still records step/run/tool correlation without an activity listener. Attempt spans do not contain endpoint URLs, credentials, headers, request/response bodies or raw exception details. Historical runner sources and evaluation selection remain unchanged; no tracing exporter is configured automatically.

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AgentRunner
```

Run the SDK transport/failure checks without credentials or network requests:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AzureOpenAIChatClientFactoryTests
```

Run the multistep file/span trace checks:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --configuration Release --filter FullyQualifiedName~AgentRunnerTelemetryTests
```

### Logging and tracing

Diagnostics are written through Serilog to `logs/expense-assistant-YYYYMMDD.jsonl` beside the application output (for example, `src/ExpenseAssistant.App/bin/Debug/net10.0/logs/`). Each line is a JSON event with a timestamp, level, message template, rendered message, and structured properties. Files roll daily and when they reach 10 MB; at most seven files are retained, so high-volume size rolls may retain less than seven days. The log directory is already excluded by `.gitignore`. The logger is disposed after the host to flush and close files during normal shutdown.

Console diagnostic providers are removed; the terminal retains chat prompts, assistant answers, usage, session/run summaries, and the seed-only confirmation. File events cover database initialization, run start/completion, each model request, and each tool's validation/execution. Log scopes carry `SessionId` and `RunId`; tool scopes add `ToolCallId` and `ToolName`. Completion records include duration, outcome/error code, and available token counts. Unknown tool names are logged as `unrecognized` rather than echoing arbitrary model-generated text.

`ActivitySource` named `ExpenseAssistant.App.ToolChatRunner` exposes this span hierarchy:

```text
assistant.run
	model.initial
	tool.execute (one per requested call, including rejected calls)
	model.final (only when the initial response requests tools)
```

Spans include correlation IDs, timing, outcome, and available model usage; tool spans also contain call IDs, allowlisted names, and error codes. Attach an `ActivityListener` or configure an OpenTelemetry listener for this source to collect spans. No tracing backend or exporter is required for file logging, and spans are not collected without a listener.

Prompts, model response text, tool arguments/results, transaction descriptions, credentials, and raw exception details are not logged by the runner. The interactive assistant answer is still printed normally. Serilog defaults to Information, with EF Core and host-lifetime events restricted to Warning and above. These levels are set explicitly in the logger configuration in `Program.cs`; existing `Logging:LogLevel` settings do not control this Serilog provider. Leave EF Core sensitive-data logging disabled.

The console's `Run usage` comes from `AgentRunResult.Usage`, summing all logical model requests without changing individual responses' usage metadata. If any request omits usage, the aggregate is unknown; a missing individual count also remains unknown rather than being treated as zero. Per-request logs retain whatever usage the provider supplies.

Run the offline telemetry tests with:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~ToolChatRunnerTelemetryTests
```

If a running Windows console process locks Debug assemblies, add `--configuration Release` to the test command instead of stopping that session.

### Evaluation scenarios

`evals/scenarios.json` retains the four Module 1 scenario IDs in `model-only` mode and adds eight Module 2 scenarios in `tools` mode. The missing-transaction-data baselines still run without tools; their original expected behavior has not been reinterpreted. Tool scenarios declare `datasetVersion: "v2"`. Independent expected totals, counts, currency and record IDs live in `evals/fixtures/v2.json`.

Validate the entire catalog and verify the fixture assertions against freshly seeded SQLite, without credentials or model calls:

```shell
dotnet run --project src/ExpenseAssistant.Evals -- --validate
```

The following commands make paid requests to the configured model. Run them deliberately after offline validation:

```shell
dotnet run --project src/ExpenseAssistant.Evals -- --mode tools
dotnet run --project src/ExpenseAssistant.Evals -- --mode model-only
```

Omitting `--mode` preserves the model-only default; `--mode all` explicitly selects both groups. An optional scenario-file path is supported; its fixture assertions are resolved from the adjacent `fixtures/v2.json` file. Validation checks the whole catalog regardless of execution mode. The previous `v1` assertion file is retained for historical evidence, not as an executable current fixture.

Each tool-enabled scenario gets a separate in-memory SQLite database, fresh synthetic data, and a distinct session/run ID. Database fingerprints include every transaction field and category before and after the run. The query-failure scenario enables an interceptor only during that scenario's model/tool round; it does not damage the fixture or touch the interactive app's database.

Reports under git-ignored `evals/results/` include selected tool names, call IDs, JSON arguments, structured results, final answers, aggregate usage and deterministic checks. This payload capture is intentional for synthetic evaluation data only and does not change normal application logs. Do not run these scenarios against personal transactions.

Expected tool calls are matched by name and the declared argument subset, without requiring one particular call order. Fixture-backed result checks independently verify full summary totals and limited-search IDs/`HasMore`. The runner returns a nonzero exit code for failed runs or failed deterministic checks. `ManualReview` stays `NotReviewed`: successful execution and correct tool results do not prove the final answer is accurate or that refusals/clarification are appropriate. Review those against each scenario's `expectedBehavior`.

Offline runner tests cover tool evidence, isolated fixtures, read-only state assertions, injected failures, invalid scenario contracts and baseline compatibility:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~EvaluationScenarioTests
```

Live behavioral batches and the controlled tool-description comparison still need to be run and recorded in the Module 2 checkpoint; these offline checks do not substitute for that evidence.

## Safety and scope

- Keep credentials outside source control. A .NET gitignore alone does not guarantee that secrets are excluded.
- Do not commit personal transactions, local databases or raw conversation logs.
- Test approval and isolation controls through application-state assertions, not only the assistant's wording.
- Treat summaries and retrieved text as contextual data, not authorization or authoritative current state.
- Keep paid live-model evaluations deliberate and separate from routine unit tests.

The initial scope is a single-user console application with multiple sessions. An API, authenticated multi-user access, MCP integration and deployment are possible later extensions, not requirements for completing this course.
