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