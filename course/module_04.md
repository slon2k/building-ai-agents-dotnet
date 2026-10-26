# Module 4 — Retrieval, Embeddings and Semantic Search

[Course Index](README.md) | [Previous: The Agent Loop](module_03.md) | [Next: Sessions and Context](module_05.md)

## Module purpose

Extend the assistant beyond exact database filters so it can find transactions described indirectly.

We will build a small semantic-search capability, expose it as a tool and evaluate when it helps—and when precise queries remain the better choice.

---

## Module overview

**Estimated effort:** 3–4 hours, split across sessions if needed.

**Main question:**

> How do we retrieve the right evidence for a question without confusing semantic similarity with factual correctness?

### Working result

An expense assistant that can:

- Search transactions by meaning.
- Apply exact date and category restrictions to semantic searches.
- Combine retrieval with existing database tools.
- Distinguish exploratory searches from authoritative spending calculations.
- Produce answers grounded in retrieved records.
- Run a small retrieval evaluation suite.

Example:

```text
User:
Find expenses that might be related to working remotely.

Assistant:
Searches descriptions by meaning, optionally using date filters.
Returns relevant candidates and explains any ambiguity.
```

Semantic search returns **candidate records**, not proof of their purpose.

---

## Prerequisites

Module 3 should be complete:

- A bounded agent loop.
- Read-only transaction tools.
- Explicit run outcomes and execution budgets.
- Model and tool tracing.
- Deterministic tests and behavioral evaluations.

We will continue using synthetic data and one active conversation session.

You also need an embedding-capable model and compatible integration. A working chat model does not imply embedding support. Confirm that a sample input produces a vector, record its dimensions and budget for indexing plus repeated query embeddings before processing the dataset.

---

## Learning objectives

By the end of this module, you should be able to:

1. Explain what embeddings represent and what they do not guarantee.
2. Build a small similarity-search index.
3. Distinguish exact filtering, semantic retrieval and aggregation.
4. Expose retrieval through a controlled tool contract.
5. Evaluate retrieval separately from final-answer quality.
6. Recognize failures caused by missing evidence, irrelevant evidence and unsupported interpretation.

---

## Concepts and trade-offs

An embedding maps text to a numeric vector so related descriptions can be compared. It does not prove that an expense served a particular purpose. Vector similarity ranks candidates; database filters enforce exact constraints; application code computes authoritative totals.

### Worked retrieval judgment

For one reviewed query, suppose the relevant records are TX-R1 and TX-R2. The top three results are TX-R1, TX-R9 and TX-R2. Precision@3 is 2/3, recall@3 is 2/2 and hit rate@3 is 1. These values describe this labeled query, not the correctness of the assistant's final explanation.

If TX-R9 lies outside the requested date range, that is a filtering defect even though both relevant records were retrieved. Correct the filtering before tuning the model or similarity threshold.

Before building, explain why returning the nearest record for a query with no relevant matches is not evidence that a match exists.

---

## Retrieval design

### Exact retrieval

Use existing database tools for questions such as:

- “Show transactions from Green Market.”
- “How much did I spend on groceries in March?”
- “List purchases in the Travel category.”

Dates, amounts and assigned categories have precise meanings. Do not replace those constraints with vector similarity.

### Semantic retrieval

Use embeddings for questions such as:

- “Find purchases related to working remotely.”
- “Which transactions look like streaming subscriptions?”
- “Find expenses that might be connected to a holiday.”

These requests describe concepts rather than exact stored values.

### Combining the two

For a request such as:

> “Find possible remote-work expenses in March.”

We will:

1. Apply the March date filter.
2. Rank eligible records by similarity to the semantic query.
3. Return a limited set of candidates.
4. Let the assistant explain them with appropriate uncertainty.

For our small dataset, exact filtering before scoring is straightforward and avoids losing eligible records through global top-k truncation.

---

## Implementation plan

### Part 1 — Prepare searchable transaction text

Build a consistent text representation for each transaction:

```text
Merchant: CloudDesk
Description: Monthly online collaboration subscription
Category: Software
```

Keep dates and amounts as structured metadata for exact filtering.

Document which fields are embedded. Including an assigned category can help retrieval, but may also bias results toward existing labels.

Add realistic synthetic examples:

- Clear matches.
- Ambiguous descriptions.
- Similar wording with a different meaning.
- Relevant concepts expressed without the query’s exact words.

Do not make every relevant record conveniently contain the search phrase.

### Part 2 — Generate embeddings

Introduce an embedding-generation component using the selected provider.

Where supported by the chosen integration, use:

```text
IEmbeddingGenerator<string, Embedding<float>>
```

Keep embedding generation separate from chat completion.

Record index metadata:

- Embedding model identifier.
- Vector dimensions.
- Text-construction version.
- Dataset version or content hashes.

Stored and query embeddings must come from compatible model configurations. Equal vector dimensions alone do not guarantee compatibility.

If the embedding model or text representation changes, rebuild the index.

### Part 3 — Build a small local index

For approximately 100–200 transactions, a full scan is sufficient.

Persist:

- Transaction identifier.
- Searchable-text hash.
- Embedding vector.
- Index-version metadata.

Implement cosine similarity over eligible records:

```text
Load eligible transaction vectors
    ↓
Embed the user’s semantic query
    ↓
Calculate similarity scores
    ↓
Sort descending
    ↓
Return top-k candidates
```

There is no need for a dedicated vector database yet.

Handle:

- Dimension mismatches.
- Invalid or zero-length vectors.
- Missing embeddings.
- Deterministic ordering for tied scores.

### Part 4 — Expose a semantic-search tool

Add:

#### `SearchTransactionsByMeaning`

Arguments:

- Semantic query.
- Optional start date, inclusive.
- Optional end date, exclusive.
- Optional category.
- Result limit.

Return:

- Candidate transactions with stable identifiers.
- Similarity scores.
- Applied filters.
- Number of eligible records searched.
- Whether eligible records exceeded the result limit.

Example tool description:

```text
Find candidate transactions whose descriptions are semantically
similar to a concept or activity.

Use this for loosely described purchases, not authoritative
spending totals or exact merchant matching.

Similarity indicates ranking relevance, not certainty that a
transaction belongs to the requested concept.
```

Validate query length, filters and result limits in application code.

### Part 5 — Define answer boundaries

The assistant should:

- Refer to transaction identifiers when presenting candidates.
- Distinguish stored facts from inferred purpose.
- Mention relevant uncertainty.
- Avoid claiming that top-k results are exhaustive.
- Avoid interpreting similarity scores as probabilities.

For example:

```text
This collaboration-software subscription may be related to
remote work, but the transaction description does not establish
whether it was used for business or personal purposes.
```

Do not calculate an authoritative “remote-work spending total” merely by summing semantic candidates. The category is inferred, and relevant records may be missing.

### Part 6 — Integrate with the agent loop

Make the semantic-search tool available alongside existing exact tools.

Test requests that require:

- Exact retrieval only.
- Semantic retrieval only.
- Semantic retrieval with exact filters.
- Clarification because the concept is ambiguous.

The model chooses among tools, but the application still controls validation, query execution and budgets.

### Part 7 — Expand diagnostics

Record retrieval metadata such as:

- Embedding model and index version.
- Number of eligible records.
- Requested and returned result counts.
- Search and query-embedding durations.
- Available embedding usage.
- Failure codes.

Do not log vectors, full transaction text or user query text by default.

---

## Retrieval evaluation

### Create a labeled query set

Build approximately 10–15 queries with manually reviewed relevant transaction IDs.

Include:

- Clear semantic matches.
- Exact-match requests.
- Date-restricted searches.
- Ambiguous concepts.
- Queries with no relevant records.

Separate tuning queries from a small held-out set where practical. Avoid repeatedly optimizing against every evaluation example.

### Measure retrieval independently

Start with:

| Metric | Purpose |
| --- | --- |
| Precision@k | How many returned candidates are relevant? |
| Recall@k | How many labeled relevant records were retrieved? |
| Hit rate@k | Did the results contain at least one relevant record? |

Only compute recall where the relevant set has been reviewed sufficiently. Handle no-relevant-record queries separately rather than assigning a misleading score.

Compare:

1. A simple keyword-search baseline.
2. Semantic search.
3. Semantic search with exact filters.

For this dataset, manual judgments are sufficient. Document ambiguous labels.

### Evaluate final answers separately

Check whether the assistant:

- Uses the retrieved records accurately.
- Preserves uncertainty.
- Respects date restrictions.
- Avoids unsupported totals.
- Does not invent transactions.

Good retrieval does not automatically produce a good answer.

---

## Tests and experiments

### Deterministic tests

Use fixed vectors and fake embedding responses to verify:

- Cosine-similarity calculations.
- Exact filtering before ranking.
- Top-k limits and tie ordering.
- Dimension and index-version validation.
- Stable transaction identifiers.
- Empty eligible datasets.
- Embedding failures.
- Cancellation propagation.
- No database mutations.

### Troubleshooting

| Symptom | First check |
| --- | --- |
| All results look unrelated | Verify query/index model compatibility and inspect the embedded text |
| Valid date matches disappear | Check that exact filters run before top-k truncation |
| Index works after one rebuild but not another | Compare model, dimensions, text hashes and index metadata |
| Good retrieval produces a wrong total | Inspect answer grounding; candidates are not an exhaustive aggregation set |

### Reasoning check

- Why are matching vector dimensions insufficient to prove model compatibility?
- When should exact retrieval be preferred over semantic search?
- What can retrieval metrics tell you that final-answer review cannot, and vice versa?

### Experiments

Use the [evidence format](README.md#evidence-for-each-experiment) and record one row per query/configuration, with query ID, labeled-set version, model/index version, `k`, returned IDs, precision, recall where valid, hit rate and a short interpretation. Mark metrics undefined where their denominator is zero. Keep no-relevant-record queries separate.

Save final-answer reviews separately and link both artifacts from the checkpoint. Keep labeled query IDs stable and connect end-to-end retrieval scenarios to the cumulative scenario collection.

#### Experiment A — Change `k`

Compare small and larger result sets.

Observe the trade-off between missing relevant records and adding irrelevant ones.

#### Experiment B — Change embedded fields

Compare descriptions alone with merchant, description and category.

Record how retrieval changes rather than assuming that more text is always better.

#### Experiment C — Search for something absent

Ask for a concept with no relevant transactions.

Observe that nearest-neighbor search can still return candidates. A high rank does not establish relevance.

Do not introduce a universal similarity threshold without evaluation; useful thresholds vary by model and dataset.

---

## Completion criteria

Module 4 is complete when:

- [ ] Transaction text is embedded reproducibly.
- [ ] Index metadata supports compatibility checks.
- [ ] A local similarity-search implementation works.
- [ ] Exact filters are applied correctly.
- [ ] Semantic search is exposed as a validated read-only tool.
- [ ] The agent chooses appropriately between exact and semantic retrieval.
- [ ] Answers distinguish candidates from established facts.
- [ ] Retrieval metrics are recorded against labeled queries.
- [ ] Deterministic tests pass.
- [ ] Traces include retrieval timing and index metadata.
- [ ] Semantic results are not treated as exhaustive financial evidence.

---

## Checkpoint and handoff

Append this section to `docs/checkpoint.md`; preserve previous modules. Follow the [cumulative checkpoint convention](README.md#keep-a-cumulative-checkpoint).

```markdown
## Module 4 Checkpoint

### Implemented
- Embedding generation
- Versioned local index
- Similarity search with exact filters
- Semantic-search tool
- Retrieval evaluation set

### Index configuration
- Embedding model:
- Vector dimensions:
- Embedded fields:
- Text-construction version:
- Dataset version:

### Verification
- Commit and verification date:
- Build/test commands and results:
- Scenario/query IDs, fixture version and assertion/review results:
- Experiment evidence and run identifiers:
- Reasoning-check answers:
- Time spent (setup/build/debug/review):
- Deterministic test results:
- Retrieval metrics:
- Keyword baseline comparison:
- Final-answer evaluation observations:

### Decisions and limitations
- ...

### Next step
Module 5: persist, switch and resume independent conversation
sessions, then introduce context budgets and summaries.
```

[Course Index](README.md) | [Previous: The Agent Loop](module_03.md) | [Next: Sessions and Context](module_05.md)
