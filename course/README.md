# Building an AI Agent

Build one practical expense assistant in C#, extending the same application through seven modules. Start with a model-backed console application and finish with a bounded agent whose retrieval, state changes and evaluation results can be inspected.

## Who this course is for

Developers comfortable with C#, asynchronous programming, HTTP, JSON, dependency injection and basic automated testing. Previous agent-development experience is not required.

Before starting, check that you can explain an asynchronous API call, propagate a cancellation token and write a test using a fake dependency. Review those topics first if any are unfamiliar.

## Course map

Complete the modules in order. Each extends the previous module's working application; there are no separate projects to restart.

| Module | Working result | Initial effort estimate |
| --- | --- | --- |
| [1. Model interaction](module_01.md) | An inspectable model call, execution identifiers and baseline scenarios | 2-3 hours |
| [2. Tools and transaction data](module_02.md) | Validated read-only tools and deterministic financial calculations | 3-4 hours |
| [3. The agent loop](module_03.md) | Bounded multi-step execution with explicit outcomes | 3-4 hours |
| [4. Retrieval and embeddings](module_04.md) | Semantic search compared with an exact-search baseline | 3-4 hours |
| [5. Sessions and context](module_05.md) | Persistent conversations, isolation and controlled history compaction | 3-5 hours |
| [6. Safe actions](module_06.md) | Approval-gated changes with revalidation and an audit trail | 3-5 hours |
| [7. Evaluation and review](module_07.md) | Repeatable evaluations and a baseline-versus-candidate comparison | 3-5 hours |

The estimates total 20-30 hours and have not been validated through a learner pilot. Allow additional time for setup, unfamiliar concepts, debugging and review. Modules 5 and 6 have staged labs that can be completed in separate sessions. Record actual time before revising these estimates.

## Environment and scope

You need a supported .NET SDK, an editor, Git and access to one model provider. Module 1 describes the initial setup. Later modules add local SQLite storage and an embedding-capable model.

Use synthetic transactions only. Keep credentials outside source control, set a personal usage budget and run live experiments deliberately rather than as part of every unit-test run.

These documents are a learning guide, not a tested starter repository. No SDK, package or provider combination is certified here yet. Before building, record the following in your project documentation and pin the package versions you actually use:

| Item | Record |
| --- | --- |
| Runtime and packages | Exact .NET SDK, target framework, model adapter, SQLite and test package versions |
| Model capabilities | Provider, model/deployment, tool-calling support and relevant settings |
| Retrieval configuration, from Module 4 | Embedding model, vector dimensions and index version |
| Verification | Date, build/test commands and result of a minimal live request |

Use the installed packages' documentation when adapting examples. Do not assume every provider supports identical settings or tool behavior.

The course remains a local, single-user console application. Session isolation is not authenticated user authorization. An API, multi-user access, MCP integration and deployment are later extensions, not requirements for completion.

## How to work through a module

1. Read the working result, prerequisites and learning objectives.
2. Explain the relevant concepts before implementing them.
3. Build one part at a time and verify it before continuing.
4. Run deterministic tests separately from live behavioral evaluations.
5. Perform the experiments, keeping the fixture and unchanged settings fixed.
6. Record evidence, decisions and limitations; proceed only when the completion criteria are met.

Implementation tasks, tests, experiments and completion criteria are required unless explicitly marked optional. A successful model answer alone is not sufficient evidence that application controls work.

### Evidence for each experiment

Use this format in your learner project's checkpoint. Link larger outputs rather than copying full traces or sensitive payloads.

| Field | What to record |
| --- | --- |
| Objective | The question or hypothesis being tested |
| Task and configuration | Scenario IDs, fixture version and the one variable changed |
| Expected evidence | A test result, application-state assertion, metric or manual review rubric |
| Observation | Actual result, repetitions and relevant run/trace identifiers |
| Interpretation | What the evidence supports, what failed and what remains uncertain |

For deterministic checks, compare with an independently established expected value. For live model behavior, use observable criteria rather than exact response wording or one mandatory tool sequence.

### Keep a cumulative checkpoint

In the learner project, create `docs/checkpoint.md` with one top-level heading, `# Course Checkpoints`. Append each module's checkpoint beneath it using a second-level heading; retain earlier modules and date any later corrections. The checkpoint templates in the modules follow this convention.

Include the source-control commit, verification commands and results, scenario IDs, experiment evidence, time spent and unresolved limitations. Mark verification as not run when it has not been performed. Never record credentials.

Keep one cumulative `evals/scenarios.json` collection. Preserve scenario IDs across modules; retire or revise a scenario explicitly when the application's capabilities change. Module 7 extends these scenarios into isolated, repeatable evaluations rather than starting a new collection.

## Final demonstration

The course is complete when you can demonstrate and explain:

- An exact spending query checked against a known synthetic fixture total.
- A semantic search whose answer distinguishes candidates from established facts.
- A bounded multi-step run and a safely handled tool failure.
- Conversation continuity after restart without leakage between sessions.
- Context compaction that preserves valid tool exchanges and relevant continuity.
- Rejection of unapproved, cross-session and stale changes.
- One explicitly approved change applied at most once, with an audit record.
- An isolated evaluation report comparing one controlled change with a baseline.

Support the demonstration with passing deterministic tests, reviewed behavioral evidence and documented limitations. This is a reference learning application, not a claim of production readiness or universal model reliability.

[Start Module 1: Model Interaction and Application Foundations](module_01.md)
