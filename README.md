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

### Seed the local database

Initialize SQLite without model credentials or a model request:

```shell
dotnet run --project src/ExpenseAssistant.App -- --seed-only
```

Normal application startup also runs initialization. By default, `expense-assistant.db` is created beside the application output. Override the SQLite connection string through `ConnectionStrings:Expenses` in configuration or the `ConnectionStrings__Expenses` environment variable.

The fixed synthetic fixture `v1` contains 123 transactions across January-June 2026 and eight categories. All expenses are positive EUR amounts, with no refunds or transfers. Amounts use `decimal` in application code and integer cents in SQLite; more than two decimal places are rejected. The March groceries fixture contains exactly `TX-G1` and `TX-G2`, totaling EUR 21.25; `TX-G3` is dated April 1 and is excluded from March.

Initialization inserts missing fixture IDs atomically and preserves existing records, including edited categories. It is not a reset command: use a fresh database for baseline evaluations and tests. The initializer uses `EnsureCreatedAsync`, not migrations; it does not upgrade an existing schema.

Run the database tests with:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~ExpenseDbContextTests
```

### Transaction queries

Core defines `ITransactionQueries` and read-only result records; Infrastructure implements it with `TransactionQueries`. Both operations use an inclusive start date and exclusive end date, accept an optional exact category ID, and pass cancellation through to EF Core. Unknown or blank category IDs and invalid date ranges are rejected with argument exceptions.

Search returns transaction DTOs ordered by date, then ID. The default limit is 20, with allowed limits of 1-100. `ReturnedCount` describes the returned subset; `HasMore` indicates omitted matches, determined by fetching one extra row. Merchant filtering uses a trimmed, literal substring with SQLite's default ASCII case-insensitive `LIKE` behavior. Percent and underscore characters are escaped, not treated as wildcards; blank merchant filters are rejected.

Summaries have no result limit. They load all matching amount/category/currency projections and aggregate exact `decimal` values in application code, avoiding SQLite decimal-aggregation limitations. This is appropriate for the small course fixture, not a large-data aggregation strategy. The configured currency defaults to EUR, including empty results; matching transactions in another currency cause a failure rather than a misleading combined total.

Database and cancellation failures propagate to the caller; they are not converted into successful empty results. Mapping failures into safe model-facing tool responses is a later step.

Run the query tests with:

```shell
dotnet test tests/ExpenseAssistant.Tests/ExpenseAssistant.Tests.csproj --filter FullyQualifiedName~TransactionQueriesTests
```

## Safety and scope

- Keep credentials outside source control. A .NET gitignore alone does not guarantee that secrets are excluded.
- Do not commit personal transactions, local databases or raw conversation logs.
- Test approval and isolation controls through application-state assertions, not only the assistant's wording.
- Treat summaries and retrieved text as contextual data, not authorization or authoritative current state.
- Keep paid live-model evaluations deliberate and separate from routine unit tests.

The initial scope is a single-user console application with multiple sessions. An API, authenticated multi-user access, MCP integration and deployment are possible later extensions, not requirements for completing this course.
