# Company PR Reviewer — Development Roadmap

**Version:** 1.3 (simplified development-first roadmap)  
**Date:** 2026-10-29  
**Status:** Phase 1 checkpoint pending — SLICE-1.01 through SLICE-1.12 implemented; SLICE-1.13 local gates passed, hosted CI/public-history evidence and publication clearance outstanding  
**Implementation:** C# / .NET 10 · GitHub Actions · DeepSeek Flash (`deepseek-flash`)  
**Repository plan:** Public repository under your company-email GitHub account for development → transfer to company organization and make private for production

## 1. Objective

Build a centrally maintained, **on-demand** AI reviewer for pull requests across repositories in one GitHub organization. A developer requests a review by creating a **new top-level PR Conversation comment** containing the exact command:

```text
@company-code-reviewer review
```

The application validates the request, fetches the current PR changes, asks DeepSeek Flash to review them, validates its findings, and posts a **non-blocking GitHub PR review** with a summary and actionable inline comments. It does **not** review PRs automatically on creation or push.

> **Command naming note:** This roadmap preserves `@company-code-reviewer review`, the command already specified in Product Requirements v1.2 and Technical Architecture v1.2. `@company-pr-reviewer` by itself is a different command and will **not** trigger the MVP unless we intentionally change the accepted command across the implementation and documentation. The command is plain text, not an actual mentionable GitHub App.

### Delivery approach

1. **Phase 0 — Repository creation:** You create the central repository yourself. Nothing else is required in this phase.
2. **Phase 1 — Development:** Implement the C# worker, DeepSeek integration, GitHub integration, and reusable workflow.
3. **Phase 2 — Hands-on testing:** Create a test PR, post the command, and verify that the review appears. Fix issues as they are discovered.
4. **Phase 3 — Production rollout:** Transfer the finished central repository into the company organization, make it private, and enable approved company repositories.

Do not gate Phase 1 behind test-repository setup, API account setup, organizational approvals, or separate governance tasks. Complete the implementation using mocks/stubs where appropriate, then configure external integrations for the live test in Phase 2. Security checks remain part of the software being built, not an extra setup phase.

---

## 2. PHASE 0 — Create the GitHub repository (you do this)

### SLICE-0.01 — Create the central project repository

**Your steps**

1. Sign in to GitHub using the account registered with your company email.
2. Select **New repository**.
3. Set the repository name to **`company-pr-reviewer`**.
4. Choose **Public** for the initial development repository.
5. Initialize it with a `README.md` and, optionally, a `.gitignore` for Visual Studio/.NET.
6. Clone the repository locally and open it in Visual Studio or your preferred editor.
7. Add the existing project documents under `docs/` if you want the implementation to follow those specifications. Check that anything committed to this public repository is safe to publish.

**Done when:** `YOUR_USERNAME/company-pr-reviewer` exists, is public, and you can push commits to it.

**Next:** Begin Phase 1 immediately. You do not need to create a caller test repository, configure the DeepSeek API key, or request GitHub organization access in Phase 0.

---

## 3. PHASE 1 — Development

**Outcome:** A complete, buildable C# reviewer and reusable GitHub workflow, ready to run against a real test PR in Phase 2.

### SLICE-1.01 — Initialize the .NET Solution Using Visual Studio

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** The existing `Ten28-PrReviewer.slnx`, `Ten28-PrReviewer/`, and `Ten28-PrReviewer.Tests/` names and root-level project folders were retained instead of renaming/moving them to the illustrative `Company.PrReviewer` / `src` / `tests` layout below. Both projects are included in the solution. Host lifecycle options are bound from `appsettings.json`, which is copied to build/publish output and loaded relative to the executable. The worker currently performs an intentional no-work success and exits; review orchestration belongs to SLICE-1.02.

**Verified:** Release solution build succeeded with zero warnings/errors; six xUnit tests passed covering DI/HTTP/logging/options registration, successful startup/shutdown, cancellation (including host shutdown requests), startup failure, and shutdown failure. Process smoke tests confirmed exit code `0` without GitHub/DeepSeek credentials and no synthetic credentials in stdout/stderr.

**Not verified:** Whether the original projects were created manually in Visual Studio. This local folder currently has no `.git` directory, so repository initialization, remote configuration, and committed-file history cannot be checked here. SLICE-0.01 remains a separate user-owned step.

**Objective**

Manually create and configure the initial .NET 10 solution using Visual Studio. Establish the project structure and application foundation needed for the Company PR Reviewer.

**Implementation**

**Step 1 — Create the solution**

1. Open Visual Studio 2026.
2. Select **Create a new project**.
3. Search for **Blank Solution**.
4. Name the solution `Company.PrReviewer`.
5. Save the solution inside the root of the `company-pr-reviewer` GitHub repository.
6. Ensure the solution file is located at the repository root, rather than inside an additional nested directory.

**Step 2 — Create the main application**

1. Right-click the solution → **Add → New Project**.
2. Select **Console App (C#)**.
3. Set the project name to `Company.PrReviewer`.
4. Set the project location to the repository's `src` directory.
5. Select **.NET 10** as the target framework.
6. Enable nullable reference types and implicit usings.

**Step 3 — Create the test project**

1. Right-click the solution → **Add → New Project**.
2. Select **xUnit Test Project**.
3. Name the project `Company.PrReviewer.Tests`.
4. Place it inside the repository's `tests` directory.
5. Target .NET 10.
6. Add a project reference to `Company.PrReviewer`.

**Step 4 — Configure application dependencies**

Install the following NuGet packages in the main application:

- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`

Configure the application to support:

- Generic Host for application lifecycle management.
- Dependency Injection for registering services.
- `IHttpClientFactory` for HTTP integrations.
- Options pattern for strongly typed configuration.
- `CancellationToken` for graceful cancellation.
- Structured logging through `ILogger<T>`.

Use `Host.CreateApplicationBuilder(args)` as the application's entry-point foundation.

**Step 5 — Configure project files**

- Add a `.gitignore` configured for Visual Studio and .NET.
- Add `appsettings.json` for non-sensitive application settings.
- Ensure secrets and API keys are never committed.
- Configure the application to exit after completing its work rather than running indefinitely.

**Step 6 — Define application exit behavior**

Establish the following exit codes:

- `0` — Successful execution or intentional skip.
- `1` — Operational failure.
- `130` — Execution cancelled.

The actual PR review execution logic will be implemented in subsequent development slices.

**Expected project structure**

```text
company-pr-reviewer/
├── Company.PrReviewer.sln
├── src/
│   └── Company.PrReviewer/
│       ├── Company.PrReviewer.csproj
│       ├── Program.cs
│       └── appsettings.json
├── tests/
│   └── Company.PrReviewer.Tests/
│       ├── Company.PrReviewer.Tests.csproj
│       └── UnitTest1.cs
└── .gitignore
```

**Done when**

- The solution and projects have been created manually using Visual Studio.
- Both projects target .NET 10.
- The test project references the main application.
- The Generic Host initializes successfully.
- Dependency Injection, HTTP client registration, and logging are configured.
- The application starts and exits successfully without requiring GitHub or DeepSeek credentials.
- The solution builds without errors.
- The initial xUnit test passes.
- Startup logs do not expose sensitive information.

**Next:** Proceed to SLICE-1.02.

### SLICE-1.02 — Create application contracts and orchestration

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added immutable review models, six provider-neutral interfaces, and a dependency-injected `ReviewOrchestrator`. The explicit `--demo` command registers synthetic event/repository adapters, a stub DeepSeek provider, demo policy/validation adapters, and an in-memory recording publisher. Ordinary startup retains its no-work behavior. No server, database, or new package dependencies were added.

**Verified:** Release solution build succeeded with zero warnings/errors; all 29 xUnit tests passed, including the six existing startup tests. Coverage includes pipeline order/context, missing requests, filtered and summary-only publication, dependency failures, cancellation, and demo execution with HTTP configured to fail immediately if attempted. Console smoke tests passed in demo and normal modes without GitHub/DeepSeek credentials. The user's console screenshot also showed demo completion with one synthetic finding, no review sent to GitHub, and normal shutdown.

**Deferred:** Real event parsing, requester authorization, policy enforcement, prompt construction, findings/diff validation, GitHub/DeepSeek API integration, publication, and duplicate detection remain assigned to later slices. The demo validator is a fixture-only passthrough.

**Implement**

- Add interfaces for GitHub event/repository access, LLM calls, review policy, findings validation, and GitHub publication.
- Define models such as `ReviewRequest`, `PullRequestSnapshot`, `ChangedFile`, `ReviewFinding`, `ReviewOutcome`, and `ReviewUsage`.
- Implement a simple `ReviewOrchestrator` using dependency injection; no separate server or database.
- Stub GitHub and DeepSeek dependencies for local development.

**Done when:** A fake valid PR event can flow through the orchestration pipeline without an external API call.

**Next:** Proceed to SLICE-1.03.

### SLICE-1.03 — Detect the manual PR comment trigger

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added `GitHubReviewEventSource` to read `GITHUB_EVENT_PATH`, accept only new top-level PR Conversation comments matching the exact trimmed command from a human author, and verify the PR is currently open through the GitHub API. Other events and invalid trigger fields are rejected before HTTP access. Normal startup detects or skips the request and exits; review execution is not yet configured. The explicit `--demo` pipeline remains offline. No new packages or public contract changes were introduced.

**Verified:** Release solution build succeeded; all 118 xUnit tests passed. Coverage includes valid and whitespace-trimmed commands, missing/extra-text commands, bots, ordinary issues, closed/merged PRs, unrelated events, API request headers, operational failures, cancellation, normal startup, and offline demo behavior. Tests passed outside the sandbox after the sandboxed test host failed to connect. NuGet reported warnings because its vulnerability feed was unreachable.

**Deferred:** Live GitHub verification, comment revalidation, requester authorization, and review execution remain for later slices and integration testing. Trigger detection alone does not authorize the requester.

**Implement**

- Read GitHub's `GITHUB_EVENT_PATH` payload.
- Accept only `issue_comment.created` on a pull request (not ordinary issues or inline review-thread comments).
- Require the trimmed comment body to exactly match `@company-code-reviewer review`; reject bots and other text.
- Verify the PR is still open through GitHub's API.
- Ensure PR creation, synchronize/push, and other GitHub events cannot start a review.

**Done when:** Unit tests pass for valid, missing, extra-text, bot, issue-only, and closed-PR scenarios.

**Next:** Proceed to SLICE-1.04.

### SLICE-1.04 — Authorize the requesting developer

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Extended `GitHubReviewEventSource` to query the commenter's current effective repository permission after verifying the PR is open and before returning a review request. Only legacy `write` or `admin` access is accepted, covering maintain and custom roles through their effective base permission. Read/none access skips the request; invalid identities, ambiguous responses, and API failures stop processing. Each request performs a fresh lookup without relying on role names, author association, ownership, or cached permissions. No public contract changes or new dependencies were introduced; normal startup remains detection-only and the demo remains offline.

**Verified:** Release solution build succeeded with zero warnings/errors; all 163 xUnit tests passed outside the sandbox. Coverage includes the permission matrix, request headers and ordering, malformed responses, mismatched identities, unsuccessful HTTP statuses, network failures, timeouts, cancellation, fresh permission lookups, sanitized host behavior, and existing startup/demo behavior. Tests run the real event source through the orchestrator and prove that denied or unverifiable permission checks produce zero downstream calls, including zero LLM provider invocations; authorized control cases reach the stub provider after authorization.

**Deferred:** Live GitHub token compatibility remains for pilot integration testing. DeepSeek usage was verified through test spies; no paid model calls were made. No further action is required to complete this slice.

**Implement**

- Query GitHub for the commenter's **current effective repository permission**.
- Require appropriate write-level permission (including equivalent maintain/admin privileges).
- Fail closed when the permission check or API call is ambiguous or unsuccessful.
- Place authorization **before** fetching extensive diffs or calling the LLM.

**Done when:** Unauthorized requests make **zero DeepSeek calls**; permission cases are covered by tests.

**Next:** Proceed to SLICE-1.05.

### SLICE-1.05 — Retrieve the current PR diff

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added a typed `HttpClient` implementation of `IGitHubRepository` and wired normal startup to retrieve a snapshot after requester authorization. Retrieval validates PR metadata, paginates changed files, preserves rename origins, deleted-file patches, and raw hunk text, and retains file metadata with a null patch when content is unavailable. It rejects duplicate paths, invalid records, incomplete pagination, and PR closure or base/head/file-count changes observed during the final metadata check. Fixed retrieval guards allow at most 3,000 files across 30 file-list pages, 10 MiB per response body, and 32 MiB total response bodies, enforced while streaming even without a declared content length. No public contract changes or new dependencies were introduced. Only read requests are used; PR content is never checked out, written locally, built, or executed. Startup logs only snapshot identity/count metadata; live LLM execution and publication remain disabled, and the demo remains offline.

**Verified:** Release solution build succeeded with zero warnings/errors; all 252 xUnit tests passed outside the sandbox. Coverage includes single/multiple/exact-boundary/empty/maximum-file pagination, supported statuses and rename origins, unavailable patches, exact preservation of multi-hunk patches and newline markers, malformed metadata and file records, duplicate paths and count mismatches, exact response and aggregate byte limits with declared and unknown lengths, HTTP/network failures, cancellation during headers/body reads, and PR changes during collection. Host tests verify authorization precedes retrieval, denied requests make no file-list calls, live LLM/publisher services remain unconfigured, and logs omit source text and secrets. Existing startup and offline-demo tests also pass.

**Deferred:** Live GitHub verification remains for pilot integration testing. Configurable analysis budgets, filtering, and skipped-file reporting remain in SLICE-1.06; hunk parsing and inline-location validation remain in SLICE-1.09. No further action is required to complete this slice.

**Implement**

- Fetch PR number, state, base and head SHA, changed-file metadata, and patches through the GitHub API (Octokit.NET or typed `HttpClient`).
- Support file-list pagination and account for renames, deletions, binary files, and missing patch fields.
- **Do not** check out, build, or execute code from the PR in the privileged review job.

**Done when:** A mocked PR produces a consistent, bounded snapshot, including valid file/hunk metadata.

**Next:** Proceed to SLICE-1.06.

### SLICE-1.06 — Add review policies and input limits

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added versioned central review rules and typed limits with initial ceilings of 30 selected files, 250,000 total UTF-8 patch bytes, 40,000 bytes per patch, and 10 findings. Central settings and optional repository configuration may only tighten these allowances. Repository configuration is loaded only from `.github/ai-review.json` at the snapshot's base SHA; HTTP 404 uses central settings, while invalid configuration or other lookup failures stop processing. Response envelopes are bounded to 128 KiB and decoded configuration to 64 KiB. A shared input selector preserves the original snapshot, selects whole patches in deterministic path order, and records one reason for every skipped file. It excludes generated paths, unsupported extensions, unavailable or binary/non-text patches, and files exceeding effective budgets. Normal startup prepares inputs and reports coverage counts. The orchestrator passes only selected patches downstream, skips generation/validation/publication when none qualify, stably orders validated findings by severity, applies the findings limit, and appends a code-generated coverage statement. The demo remains offline; live generation and publication remain disabled.

**Verified:** Release solution build succeeded with zero warnings/errors; all 359 xUnit tests passed outside the sandbox. Coverage includes central defaults and tighter overrides, base-SHA configuration requests, absent/invalid configuration, malformed and ambiguous responses, API/network failures, streamed response and decoded-size boundaries, cancellation, root/nested exclusions, rename/deletion preservation, deterministic selection, UTF-8 and multibyte patch boundaries, file-count and findings limits, complete skipped-file accounting, partial-coverage summaries, and zero downstream calls for empty selections. Existing authorization, startup, and offline-demo tests also pass.

**Deferred:** Live GitHub verification remains for pilot integration testing. Prompt construction, comprehensive hunk validation, and native publication remain in SLICE-1.07, SLICE-1.09, and SLICE-1.10 respectively. Generated-file filtering uses explicit patterns rather than exhaustive language-aware detection. No further action is required to complete this slice.

**Implement**

- Define central default review rules: correctness, security, regressions, validation, significant performance issues, and transaction safety.
- Optionally load `.github/ai-review.json` **only from the trusted base branch/SHA**.
- Filter generated files and binaries; bound file count, patch bytes, and maximum findings. Begin with the existing architecture's defaults (30 files, 250 KB total patch, 40 KB per file, 10 findings) and make them configurable within centrally enforced limits.
- Record skipped files so the summary does not imply full coverage.

**Done when:** Oversized or invalid input is safely skipped or limited, with deterministic unit tests.

**Next:** Proceed to SLICE-1.07.

### SLICE-1.07 — Build the review prompt and structured output contract

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added a deterministic, provider-neutral `ReviewPromptBuilder` returning separate system and user messages. Trusted rules, policy/version, severity definitions, evidence requirements, findings limits, JSON Schema, and an illustrative response stay in the system message. PR metadata, filenames, source/comments, complete selected patches with hunk information, and coverage counts/skipped-file reasons are serialized as untrusted JSON in the user message. Construction rejects missing or inconsistent coverage and unavailable selected patches. The output contract requires a summary and bounded findings with lowercase severities, actionable explanations/recommendations, and `RIGHT`-side locations or null lines for useful unanchored observations. No public contract changes or new dependencies were introduced; startup and offline-demo behavior remain unchanged.

**Verified:** Release solution build succeeded; all 415 xUnit tests passed outside the sandbox, including 56 new prompt/contract tests. Synthetic fixtures cover a concrete defect, no findings, partial coverage, multiple hunks, renames, deletions, exact JSON round-tripping, deterministic construction, effective limits, invalid selection metadata, and schema/domain compatibility. Adversarial cases exercise forged roles, delimiter closures, replacement schemas/severities, finding suppression, and command/secret requests in PR titles/descriptions, paths, source comments, hunk headings, and skipped paths. Tests confirm these strings remain in user JSON without changing trusted instructions or the schema. Temporary build outputs were removed.

**Deferred:** DeepSeek integration and actual model resistance evaluation remain for SLICE-1.08 and subsequent integration testing. Runtime response parsing, evidence validation, comprehensive hunk parsing, and inline-location enforcement remain in SLICE-1.09. Offline tests establish request construction and trust separation, not a guarantee of model behavior. No further action is required to complete this slice's agreed offline scope.

**Implement**

- Give the model clear review instructions, severity definitions, and a JSON output schema.
- Require evidence tied to changed code; avoid comments on trivial style preferences.
- Treat changed source, PR descriptions, and repository comments as **untrusted content**, not instructions.
- Include relevant diff hunk information and coverage limitations in the model request.

**Done when:** Prompt fixtures and tests cover normal and adversarial input without allowing PR text to override reviewer rules.

**Next:** Proceed to SLICE-1.08.

### SLICE-1.08 — Integrate DeepSeek Flash

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added a typed `HttpClient` implementation of `DeepSeekProvider : ILLMProvider` using the fixed official Chat Completions endpoint, `deepseek-flash`, high-effort thinking, JSON-object output, and the separate system/user messages from SLICE-1.07. The API key is read only from the process environment when generation is invoked. Central options default to ceilings of 16,384 generated tokens, 120 seconds per attempt, 480 seconds overall, and two retries; configuration may only tighten them. Only HTTP 429/5xx responses retry, with bounded backoff/jitter and valid `Retry-After` handling. Reads enforce a 2 MiB response limit, and cancellation covers headers, bodies, and retry waits. Redirects and HTTP client logging are disabled; operational errors expose no raw response or credential content. A provider-neutral response reader enforces the structured JSON contract and maps unvalidated findings into the existing models. Usage retains the returned model identifier and reported token counts, leaving missing counts unknown; reasoning text is discarded. The real provider is registered without enabling generation from normal startup, and the demo remains offline. No public interfaces or models changed, and no packages were added. The architecture's token-budget example was aligned with 16,384.

**Verified:** Release solution build succeeded; all 610 xUnit tests passed outside the sandbox, including 195 new provider/response-reader tests. Synthetic HTTP responses and an injectable clock cover request settings, prompt-role separation, adversarial PR content, findings/no-findings output, nullable locations, schema violations, malformed envelopes, token usage, missing process credentials, central limit binding, non-retryable failures, retry counts/backoff/`Retry-After`, cancellation, per-attempt and shared overall deadlines, transport failures, truncation, response-size boundaries, and exception/log sanitization. Orchestrator tests confirm provider failures prevent downstream validation/publication. Existing startup, authorization, input-selection, prompt, and offline-demo tests also pass. No real API key or live DeepSeek call was used; temporary build outputs were removed.

**Deferred:** Live API compatibility, model quality, and adversarial model resistance remain for explicitly requested integration testing. Independent evidence checks, finding deduplication, changed-file/line validation, and comprehensive hunk parsing remain in SLICE-1.09; publication remains in SLICE-1.10. Structural decoding here does not establish that a finding is factually correct or publishable. No further action is required to complete this slice's agreed offline scope.

**Implement**

- Add `DeepSeekProvider : ILLMProvider` using C# `HttpClient` and the official DeepSeek API.
- Configure the model as `deepseek-flash`, with reviewed reasoning/JSON settings appropriate to the current API.
- Read `DEEPSEEK_API_KEY` from the process environment; never hardcode it or print it.
- Set request timeout, cancellation, retry limits, and bounded output tokens.
- Handle non-200 responses, 429/5xx, empty output, invalid JSON, truncation, and token usage metrics.
- Keep the interface provider-neutral so Claude or Qwen can be added later without rewriting the reviewer.

**Done when:** Recorded/fake API responses exercise success and error paths; the project compiles and tests without requiring a real key.

**Next:** Proceed to SLICE-1.09.

### SLICE-1.09 — Validate AI findings and inline locations

**Status:** Implementation complete; local validation passed on 2026-10-28.

**Implementation notes:** Added a unified-diff parser and real `ReviewFindingsValidator`, reusing the strict JSON reader from SLICE-1.08. The parser checks hunk counts, ordered/non-overlapping ranges, numeric bounds, newline markers, and additions/deletions against file metadata; invalid patches yield no usable partial maps. Validation requires exact selected current paths, supported typed fields, explanatory prose, and a backtick-quoted complete changed source line with internal text preserved. Inline evidence must match the claimed added RIGHT-side line. Otherwise, an evidenced observation may retain a null line for summary-only publication; unsupported findings are omitted. Findings are deduplicated by final path/location and normalized title, keeping the highest severity. The orchestrator applies the combined severity order/findings cap, escapes model-controlled summary text for Markdown, appends labeled unanchored observations and coverage, and passes only anchored findings to the publisher. Outcomes retain both accepted categories. The real validator replaces the demo passthrough and is registered in normal startup; live generation/publication remain disabled. Public interface signatures and the JSON shape are unchanged.

**Verified:** Release solution build succeeded; all 744 xUnit tests passed outside the sandbox, including 134 new parser, validator, and pipeline tests. Coverage includes added/modified/renamed/deleted files, multiple hunks, omitted/zero counts, LF/CRLF, blank source lines, source resembling patch headers, no-final-newline markers, malformed/truncated/overlapping hunks, numeric overflow, contradictory metadata, exact paths and rejected aliases, typed severity/field checks, complete-line quotation matching, longer backtick delimiters, missing/incorrect evidence, deduplication, per-file failure isolation, cancellation, and Markdown escaping. Tests run the real JSON reader and validator through the orchestrator with a recording publisher and manually checked anchors, confirming that only grounded added-line findings reach the inline boundary and null-line observations appear only in the summary. Existing startup and offline-demo tests also pass. No live model or GitHub publication calls were made; temporary build outputs were removed.

**Deferred:** These checks establish structural validity and source grounding; model causal claims and recommendations still require human assessment. Live model quality and native GitHub publication remain unverified. Publisher implementation, final review formatting, and PR state/head revalidation remain in SLICE-1.10. No further action is required to complete this slice's agreed offline scope.

**Implement**

- Strictly parse DeepSeek's structured response into `ReviewFinding` objects.
- Reject unsupported severity values, fabricated file paths, unsupported line numbers, duplicate findings, and findings without actionable evidence.
- Parse unified diff hunks and verify every inline comment targets a valid changed `RIGHT`-side line.
- Convert unanchorable findings into a summary item only when useful; otherwise omit them.

**Done when:** Unit tests prove that invalid findings cannot become GitHub inline review comments.

**Next:** Proceed to SLICE-1.10.

### SLICE-1.10 — Publish a native GitHub PR review

**Status:** Implementation complete; local validation passed on 2026-10-29.

**Implementation notes:** Retained the existing native GitHub publisher, summary formatter, and full normal-startup pipeline without production code, public contract, or dependency changes. Summaries include the result, accepted severity counts, changed/selected/skipped file counts, skip-reason totals, and reviewed commit/policy/provider/model/worker metadata, with escaped untrusted text and labeled unanchored observations. Publication uses one commit-pinned `COMMENT` review with validated current paths, added RIGHT-side lines, and bounded comments; summary-only requests omit comments. Before each POST, the publisher rechecks PR identity, open state, and head/base SHAs. An explicit inline-location HTTP 422 rejection permits one summary-only attempt containing the validated findings after a fresh PR check; ambiguous validation failures and rate limits do not retry. Sanitized failures distinguish permission denial, rate limits with available retry timing, deadlines, and uncertain write delivery. Normal startup runs the complete pipeline; `--demo` remains offline, and completion requires a valid publication confirmation.

**Verified:** Release solution build succeeded with zero warnings/errors; all 858 xUnit tests passed outside the sandbox, including 13 additional host integration cases. The added cases exercise real host wiring and GitHub services with a stub model provider and mocked HTTP: head/base changes, closure, or unverifiable metadata after generation prevent all review POSTs; explicit inline rejection triggers a fresh metadata check and at most one summary-only attempt; PR changes before fallback prevent a second POST; fallback rejection, ambiguous 422, primary/secondary rate limits, and HTTP 429 fail visibly without further attempts. Host assertions check exit codes, request counts/order, commit-pinned COMMENT payloads, preserved findings, sanitized diagnostics, and absence of completion messages on failure. Existing publisher tests for valid anchors, renames, summary-only reviews, cancellation, deadlines, bounded responses, invalid confirmations, and uncertain network delivery also pass, along with the offline demo and prior slices. Temporary build outputs were kept outside the repository and removed.

**Deferred:** No live GitHub review or paid DeepSeek call was made; token permissions and live API compatibility remain for integration testing. The final metadata read and review POST are separate requests, so a concurrent PR change can still occur between them. Duplicate prevention, workflow concurrency, and expanded diagnostics remain in SLICE-1.11; GitHub Action packaging remains in SLICE-1.12. No further action is required to complete this slice's agreed offline scope.

**Implement**

- Format a concise summary: result, severity counts, covered/skipped files, and review metadata.
- Use GitHub's Pull Request Reviews API to submit one review with `event: COMMENT`, validated `path`, `line`, and `side`, and the reviewed commit SHA.
- Do not submit `APPROVE` or `REQUEST_CHANGES`; the reviewer must not block or merge PRs.
- Re-read PR head SHA/state before posting and abort if the PR changed during processing.

**Done when:** A mocked GitHub publisher receives a valid review request and handles 403, 422, rate limits, and network errors gracefully.

**Next:** Proceed to SLICE-1.11.

### SLICE-1.11 — Prevent duplicate reviews and improve diagnostics

**Status:** Worker implementation complete; local validation passed on 2026-10-29. Executable workflow concurrency is explicitly deferred to SLICE-1.12.

**Implementation notes:** Added deterministic policy/model/worker-aware review identities, versioned code-generated markers, and a bounded read-only GitHub review-history service. Matching submitted `COMMENTED` reviews must belong to `github-actions[bot]` with bot account type and the exact repository/PR/head/key. The orchestrator checks history before generation and again before publication, returns a skipped outcome with the existing review ID, and retains usage incurred before a late duplicate is found. Lookup failures and incomplete searches prevent generation/publication; markers survive summary-only fallback and permit a later manual request to reconcile an uncertain successful write. Added `ILLMProvider.ModelId`, `IReviewHistory`, prior-review/key outcome metadata, and explicit offline history registration for the demo. No database, cache, force-review command, new package, or workflow YAML was added.

**Diagnostics:** Structured terminal logs and optional appended `GITHUB_STEP_SUMMARY` output report safe request identity, current/failing stage, duplicate/no-input skip reasons, requested/recognized reported model identity, coverage and severity counts, total/stage timing, available input/output tokens, HTTP status/retry timing, and known write uncertainty. Usage is captured immediately after generation and remains unknown when unavailable; generation skips are explicit. Reports exclude source, findings, prompts, response bodies, arbitrary exception messages, and credential-bearing/unsafe metadata. HTTP request loggers are disabled for GitHub/DeepSeek clients. Missing summary paths are supported, and summary-write warnings preserve the existing exit code. Cancellation retains the caller token and records possible delivery when cancellation interrupts a write.

**Verified:** Release solution build succeeded with zero warnings/errors; all 959 xUnit tests passed outside the sandbox, including 101 additional cases. Tests cover stable canonical keys and changed identities; strict/forged/legacy markers; author/state/commit checks; paginated history, incomplete/malformed responses, exact response-size boundaries and aggregate/page/deadline limits; rate-limit retry metadata; zero repeated generation for an unchanged key; fresh manual reviews after identity changes; a competing review discovered after generation; failure-before-generation behavior; marker preservation through 422 fallback; uncertain-write recovery; retained usage after downstream failures; fake-clock timing; sanitized logs and appended summaries for success, skips, failures, cancellation, unknown usage, hostile metadata, missing paths, and write failures. Existing authorization, publisher, startup, and offline-demo tests pass. Build/test outputs were kept outside the repository and removed.

**Deferred:** Per-PR workflow concurrency moves to SLICE-1.12 by explicit scope decision; worker history checks alone cannot prevent every race between overlapping processes. Legacy unmarked reviews are not treated as duplicates. No real credentials or paid DeepSeek calls were used, and no live GitHub review was published; live token/API compatibility remains unverified. Cost estimation and aggregate billing across failed attempts remain out of scope.

**Implement**

- Detect successful prior bot reviews for the same repository, PR, head SHA, and reviewer/policy version; skip repeat requests by default.
- Allow a fresh manually requested review after a new commit.
- Add readable, sanitized logs/`GITHUB_STEP_SUMMARY` output. Per-PR workflow concurrency is delivered with SLICE-1.12.
- Record timing and API token usage without logging raw code, API credentials, or full prompts.

**Done when:** Repeat requests on an unchanged commit do not generate another billable LLM call; failures are diagnosable from logs.

**Next:** Proceed to SLICE-1.12, including the deferred concurrency acceptance check.

### SLICE-1.12 — Package the centralized GitHub Action

**Status:** Implementation complete; local YAML, script, build, and mocked validation passed on 2026-10-29. GitHub-hosted deployment/scheduling checks remain in Phase 2.

**Implementation notes:** Added root `action.yml`, `global.json`, reusable `.github/workflows/review.yml`, inactive `.github/workflows/ai-review.yml.example`, and the requested README. The composite pins `actions/setup-dotnet` v6.0.0 to verified commit `a98b56852c35b8e3190ac28c8c2271da59106c68`, installs stable .NET 10, and selects the latest installed stable 10.0 feature band through `global.json`. It publishes only the trusted main project from the action directory into a unique runner-temp directory, sets the worker informational version from the nonempty remote action ref, and passes credentials only to the separate DLL execution step. Publish and worker exits propagate. The reusable workflow exposes only `workflow_call`, explicitly requires `deepseek_api_key`, and grants only `contents: read` and `pull-requests: write`. Its caller template uses a credential-free PowerShell guard with ordinal trimmed-command equality, then a caller-only repository/PR concurrency group with `cancel-in-progress: false`; the called workflow has no duplicate lock. No caller/PR checkout, production dependency, persistent artifact, or worker API change was added. The YAML parser is test-only.

**Verified:** Release solution build succeeded with zero warnings/errors; all 983 xUnit tests passed, including 24 new packaging cases. Parsed YAML contracts cover inputs, triggers, explicit secrets, minimum permissions, references, trusted paths, runtime-only credential scope, and caller-only concurrency. Extracted guard scripts were executed against valid/padded/Unicode-padded, wrong-case, extra-text, missing/non-string, bot, issue-only, edited, unrelated/inline-event, and hostile payloads. Fake-command tests executed the actual action scripts from paths containing spaces, checking arguments, unchanged event/summary paths, withheld build credentials, runtime credential handoff, missing-ref rejection, and publish/worker exit propagation. An actual publish/startup smoke verified the worker release identity, copied appsettings, and credential-free no-event startup. Both reusable and temporarily renamed caller workflows passed checksum-verified `actionlint` v1.7.12; composite metadata passed parsed contract checks. Existing 959 worker tests also pass. Temporary scripts, test/publish outputs, and the downloaded validator were kept outside the repository and removed.

**Deferred:** `YOUR_USERNAME` and `v0.1.0` remain deployment placeholders; no repository/tag was published or secret configured. Local checks establish the workflow contract and trusted worker launch, not GitHub cross-repository resolution, permission compatibility, or scheduler behavior. Phase 2 must verify same-PR serialization without cancelling the running review, independent PR groups, and the real caller secret handoff. No live GitHub review or paid DeepSeek call was made. Older pending commands may be replaced; concurrency/history do not guarantee exactly-once delivery.

**Implement**

- Add a root `action.yml` composite action that prepares .NET 10 and runs the trusted C# worker from the action's own directory.
- Add `.github/workflows/review.yml` as a reusable workflow (`workflow_call`) with explicitly declared secrets and minimum permissions.
- Provide `.github/workflows/ai-review.yml.example`, copied to `.github/workflows/ai-review.yml` in caller repositories, triggered solely by `issue_comment.created` and filtering for the exact command.
- Add caller-only concurrency grouped by repository and PR number with `cancel-in-progress: false`, as deferred from SLICE-1.11; do not duplicate that group in the called workflow.
- For development, reference `YOUR_USERNAME/company-pr-reviewer` in the shared-action/workflow paths. Use a vetted tag or pinned commit; do not assume GitHub Actions references follow repository transfers.
- Add `README.md` instructions explaining how a caller passes `DEEPSEEK_API_KEY` via **the caller's GitHub Actions secret**.

**Done when:** Workflow/action YAML is validated, caller concurrency serializes same-repository/PR reviews without cancelling the running review, the central worker can be launched from the reusable workflow, and tests pass with mocked APIs.

**Next:** Proceed to SLICE-1.13; verify actual GitHub concurrency and cross-repository launch in Phase 2.

### SLICE-1.13 — Development completion checkpoint

**Status:** Pending final verification. Local checks passed on 2026-10-29; this slice is not complete without the required hosted push-CI evidence and publication clearance.

**Changes:** Added `.github/workflows/ci.yml` and one focused test, `ActionPackagingTests.CiWorkflowHasCredentialFreeVerificationBoundary`. CI triggers only on `push` and ordinary `pull_request`, uses one fifteen-minute `ubuntu-latest` job with `contents: read`, disables checkout credential persistence, and pins checkout/setup-dotnet to the reviewed commits already selected for this checkpoint. It builds Release, runs the complete suite without rebuilding, and invokes the compiled worker with `--demo`; all outputs are under `RUNNER_TEMP`. It has no DeepSeek secret handoff, write permission, publication step, or review-workflow invocation. Automatic build/test events do not request AI reviews. Application behavior, public APIs, production dependencies, local agent files, and `.gitignore` were not changed.

| Acceptance criterion | Result | Evidence and limits |
| --- | --- | --- |
| Solution builds and tests pass in CI | **UNAVAILABLE** for hosted CI; **PASS** locally | Release build: zero warnings/errors. Complete suite: **984 passed, zero failed/skipped**. No Git metadata, repository URL, final commit SHA, or hosted run was available in this checkout; local results are not a substitute. |
| Required implementations and failure gates exist | **PASS** within static/mocked scope | Source and tests are mapped below. Unauthorized, malformed, early-duplicate, and failed-history requests prevent generation where applicable; failures prevent publication. |
| Public repository contains only publishable generic material | **UNAVAILABLE** for the public tree/history and content clearance | Candidate secret scan passed, but there is no public tree/history to compare and one documentation publication question remains unresolved. No claim of completed public-repository clearance is made. |
| End-to-end invocation with stubbed dependencies | **PASS** | Normal host pipeline tests use mocked HTTP and a stub provider. A separate credential-free `--demo` invocation completed with exit `0`, one synthetic finding, and a clearly simulated review. Demo tests prohibit HTTP and confirm zero attempts. |

**Feature-to-proof map:**

| Requirement | Owning implementation | Existing proof reused in the full suite |
| --- | --- | --- |
| Exact command and current requester authorization | `GitHubReviewEventSource` | `GitHubReviewEventSourceTests`: command/event matrix, permission matrix, denied downstream work, complete mocked normal-host pipeline |
| Bounded, consistent, paginated diffs | `GitHubRepository` | `GitHubRepositoryTests`: pagination, malformed/incomplete snapshots, response budgets, changing PR metadata |
| Trusted policy and analysis limits | `GitHubReviewPolicyService`, `ReviewPolicySettings`, `ReviewInputSelector` | `GitHubReviewPolicyServiceTests`, `ReviewInputSelectorTests`: base-SHA policy loading, tightening-only overrides, budgets, coverage and zero-input skips |
| DeepSeek envelope and structured-response validation | `DeepSeekProvider`, `ReviewResponseReader` | `DeepSeekProviderTests`, `ReviewResponseReaderTests`: malformed/truncated output, schema/type checks, retry/deadline limits, downstream prevention |
| Grounded finding paths and RIGHT-side lines | `UnifiedDiffParser`, `ReviewFindingsValidator` | `UnifiedDiffParserTests`, `ReviewFindingsValidatorTests`, `ValidatedReviewPipelineTests` |
| COMMENT-only publication and stale-PR rejection | `GitHubReviewPublisher` | `GitHubReviewPublisherTests` and normal-host tests: exact payloads, head/base/closed checks, bounded 422 fallback, failures and uncertain delivery |
| Duplicate prevention before generation/publication | `ReviewIdentity`, `GitHubReviewHistory`, `ReviewOrchestrator` | `ReviewIdentityTests`, `GitHubReviewHistoryTests`, `DuplicateReviewPipelineTests`: repeat requests, changed identities, late competing reviews, failed lookup and uncertain-write reconciliation |
| Sanitized outcomes, timing and usage | `ReviewRunDiagnostics` | `ReviewRunDiagnosticsTests`: terminal outcomes, retained usage, fake-clock timing, hostile/credential metadata, missing/unwritable summaries |
| Trusted packaging and offline startup | Composite action, caller/reusable workflows, demo registrations | `ActionPackagingTests`, `DemoReviewTests`: YAML boundaries, script behavior, actual publish/startup, forbidden demo HTTP |

**Local commands and tooling:** The focused CI contract passed before the wider gates. The Release solution was built with `dotnet build ... --configuration Release --artifacts-path <temporary-path> --no-restore -p:NuGetAudit=false`, then tested with `dotnet test ... --configuration Release --artifacts-path <same-path> --no-build --no-restore`. The compiled DLL was launched directly with `--demo` in a child process with `GITHUB_TOKEN` and `DEEPSEEK_API_KEY` removed. Local NuGet vulnerability-feed auditing was disabled for offline validation; CI retains normal defaults. Checksum-verified `actionlint` v1.7.12 accepted `ci.yml`, `review.yml`, and an external renamed copy of the caller template. Gitleaks v8.30.1 was checksum-verified and run with full redaction and no added suppressions. Temporary tools, scan copies/reports, build/publish outputs, demo summary, and test scripts were outside the repository and removed.

**Publication inventory and findings:**

- The reviewed candidate contains **58 files**: the worker/test source and project files, the three existing `Docs` documents, the three workflow/template files, and root `.gitignore`, `action.yml`, `global.json`, `README.md`, and solution file. Gitleaks directory scan returned exit `0` with **zero findings**. Manual inspection found generic reviewer code/default policies and synthetic example fixtures, with no observed company application source, proprietary review instructions, or embedded live credentials. Synthetic credential sentinels are generated/assigned by fixtures and used with mock handlers; no broad scanner suppression was added.
- `.agents` contains four installed third-party skill documents; `skills-lock.json` is their local tooling metadata. These **five files** were scanned separately with zero findings and are outside the intended worker publication set. `.claude/skills` contains three Windows junctions to those skill directories rather than independent worker configuration. `.vs`, `bin`, and `obj` are local/generated material outside the candidate. No files were deleted or hidden by ignore changes. Without a real Git index/public tree, exclusion from a future commit cannot yet be verified.
- **Publication decision required:** `Docs/01-PRODUCT-REQUIREMENTS.md:36` contains internal business-rationale context. This is a potential publication-suitability issue, not a detected credential leak or a confirmed confidentiality determination. Obtain owner clearance or a separately reviewed public-safe revision before including it in the public release. The content was left unchanged and is not reproduced here.

**Required external evidence before completion:** After the user-owned repository setup/publication step, obtain the central repository URL, final reviewed commit SHA, and successful **push** CI run URL. Confirm that run built/tested the same reviewed commit and completed the offline demo; compare the actual public tree and all history intended for publication with the candidate and perform a redacted history scan. Record the real hosted test total rather than copying the local `984` result. Keep this checkpoint pending if any evidence, publication clearance, or implementation gate is unresolved. No repository initialization, remote publication, tag creation, secret configuration, history rewrite, paid model call, or live GitHub review was performed here.

**Phase 2 setup prerequisites:** Replace/verify both central owner/release references, create a private synthetic caller, configure its own DeepSeek Actions secret and allowed-action access, and then test the actual manual PR command. Live token/model compatibility, same-PR serialization, independent PR groups, and cross-repository resolution remain Phase 2 checks. Passing local gates alone does not establish readiness of the unconfigured hosted environment.

**Verify**

- Solution builds and unit tests pass in CI.
- GitHub comment parsing, authorization, diff retrieval, limits, DeepSeek response validation, review publication, and duplicate prevention are implemented.
- The public central repository contains only publishable generic code/fixtures, never company source, keys, or proprietary review policies.
- The reviewer can be invoked end-to-end with stubbed dependencies.

**Done when:** There are no known blockers to trying a **real manual PR comment** in Phase 2.

---

## 4. PHASE 2 — Test the finished reviewer using a real PR

**Outcome:** Confirm that a developer can create a PR, type the command, and receive an AI review. This is the first phase requiring live GitHub and DeepSeek integration.

### SLICE-2.01 — Prepare one private test caller repository

**Tasks**

1. Create a separate **private** test repository containing only synthetic/sample code (e.g., a simple C# project with intentional bugs).
2. Add the small caller workflow from Phase 1 to its default branch; point its reusable `uses:` path to the public `YOUR_USERNAME/company-pr-reviewer` release.
3. Create a DeepSeek Platform API key and ensure the account has a small balance for testing.
4. Save `DEEPSEEK_API_KEY` as an **Actions secret in the private caller repository**, not in the public central repository.
5. Confirm Actions settings permit the reusable public workflow and the bot has the minimum permissions it needs to comment on PRs.

**Done when:** The test caller can invoke the central workflow and pass its own secret without exposing credentials.

### SLICE-2.02 — Create a PR and request the first review

**Tasks**

1. Create a branch in the private test repo.
2. Make a small, easy-to-review change containing one or two deliberate, safe bugs (such as missing validation or an unhandled null).
3. Open a PR against the default branch.
4. Add this exact new comment in the PR **Conversation** tab:

   ```text
   @company-code-reviewer review
   ```

5. Open the repository's **Actions** tab and watch the `AI PR Review` run.
6. Confirm the run fetches the PR, calls DeepSeek Flash, and posts a **COMMENT** review with a summary and, when valid, inline findings.

**Done when:** The review is posted to the same PR, without automatically approving or rejecting it.

### SLICE-2.03 — Test expected behavior and fix issues

**Tasks**

- Open another PR **without** commenting; confirm nothing runs.
- Push a new commit **without** commenting; confirm nothing runs.
- Repeat the review command on an unchanged SHA; confirm it skips rather than calling DeepSeek again.
- Push a new commit, then request review again; confirm it reviews the new SHA.
- Test unauthorized commenters, invalid commands, oversized diffs, stale SHA, fabricated line references, and provider/network failures.
- Check GitHub Actions logs and DeepSeek API usage: no keys, raw company code, or unsafe outputs are exposed.
- Fix any failures in Phase 1 code and rerun the exact PR-comment test until the smoke tests pass.

**Done when:** The manual-only command, authorization, output, and repeat behavior are reliable in a live test repository.

### SLICE-2.04 — Verify centralization in a second test repository

**Tasks:** Create or reuse a second **private synthetic** caller repository, add only the thin workflow and its caller-owned secret, and request a review on a sample PR. Ensure no C# worker source code was copied into the second repository.

**Done when:** Two caller repositories use the **same public central reviewer** successfully, with independent secrets and no duplicated reviewer implementation.

### SLICE-2.05 — Record pilot observations

**Tasks:** Record review usefulness, false positives, missing findings, latency, actual DeepSeek token usage/spend, and failed runs. Tune prompts, bounds, and handling where the test reveals concrete issues. Tag a tested release (`v0.1.0` or similar) and document its revision and rollback procedure.

**Done when:** You have a working reviewed release and enough observations to decide whether it is worth piloting on real company code.

---

## 5. PHASE 3 — Move to the company organization and roll out

**Outcome:** Production-ready central reviewer in a private organization-owned repository, callable on demand from approved company repositories.

### SLICE-3.01 — Transfer and privatize the central repository

**Tasks:** With company authorization, transfer `YOUR_USERNAME/company-pr-reviewer` to `YOUR_ORG/company-pr-reviewer`, then change visibility to **Private**. Keep in mind that making a formerly public repo private does not undo copies or forks created while it was public. Enable private workflow/action sharing for approved same-organization repositories.

**Done when:** The company organization owns the private central reviewer and intended repositories are allowed to consume its reusable workflow.

### SLICE-3.02 — Update workflows, secrets, and release references

**Tasks:** Update **both** the callers' reusable workflow `uses:` references and the central workflow's composite `uses:` reference to `YOUR_ORG/company-pr-reviewer`; pin a vetted release/SHA. Set up organization-managed `DEEPSEEK_API_KEY` access for selected repositories (or repository-level secrets if required by the GitHub plan). Disable or update old personal-account test workflows that can no longer access the private organization-owned action.

**Done when:** No production workflow depends on the old user-owned repository path, and secrets are available only where intended.

### SLICE-3.03 — Test on approved organization repositories

**Tasks:** On two approved private company repositories, open or select safe pilot PRs and explicitly request reviews using the same command. Verify normal reviews, unauthorized request denial, duplicate detection, and no automatic triggering. Before any real proprietary diff is submitted, obtain the company's approval for the DeepSeek data-processing arrangement.

**Done when:** Two organization repositories receive successful manual-only reviews using the central private worker.

### SLICE-3.04 — Evaluate quality, costs, and release readiness

**Tasks:** Evaluate approximately 15–20 approved historical or pilot PRs against known human/Copilot findings. Measure actionable findings, false positives, relevant misses, response time, DeepSeek spend, and GitHub Actions usage. Tune the rules and document limitations before widening access.

**Done when:** Maintainers agree the review quality and spend are acceptable (or document explicit exceptions against the PRD's targets).

### SLICE-3.05 — Organization rollout

**Tasks:** Enroll approved repositories by adding the small caller workflow, grant the necessary secret access, publish user instructions for requesting a review, and nominate a maintainer for failed runs or provider outages. Keep reviews strictly manual unless there is a new explicit product decision.

**Done when:** Approved repositories can request reviews reliably; the reviewer does not run on ordinary PR events.

---

## 6. Quick acceptance checklist

### Development complete (end of Phase 1)

- [ ] C#/.NET 10 solution builds and unit tests pass.
- [ ] Manual command parser, permission checks, PR snapshot retrieval, and input bounds work with fixtures.
- [ ] DeepSeek Flash provider handles successful, invalid, and failed responses.
- [ ] Findings map to valid changed diff lines.
- [ ] Publisher creates only GitHub `COMMENT` reviews and validates head SHA before posting.
- [ ] Duplicate requests are skipped and logging is sanitized.
- [ ] Central composite action, reusable workflow, and caller template are ready.

### Live manual test complete (end of Phase 2)

- [ ] A developer opens a synthetic PR and comments `@company-code-reviewer review`.
- [ ] GitHub Actions runs the central .NET reviewer and publishes a non-blocking review.
- [ ] PR creation/push without the command triggers no review.
- [ ] Unauthorized and repeated requests don't spend unnecessary DeepSeek tokens.
- [ ] A second private test repository can call the same central reviewer.

### Production ready (end of Phase 3)

- [ ] Repository is transferred to the company organization and private workflow sharing is configured.
- [ ] Workflow/action references and approved secrets use the company organization setup.
- [ ] Company authorization to use DeepSeek for proprietary code is confirmed.
- [ ] Two company repositories pass manual PR-review tests.
- [ ] Quality, reliability, cost, and maintainer ownership are accepted.

## 7. What to do right now

**You — Phase 0:** Create the public `company-pr-reviewer` GitHub repository under your company-email GitHub account, then clone it.

**Development — Phase 1:** Start with `SLICE-1.01` and implement through `SLICE-1.13`.

**Testing — Phase 2:** Only after the reviewer is implemented, create the private test caller repository, open a PR, post the exact command, and verify the result.

**Production — Phase 3:** After testing succeeds and the company approves the move, transfer the repository to the company organization and roll it out.

## 8. Reference documents

- [Product Requirements Document](01-PRODUCT-REQUIREMENTS.md) — unchanged v1.2
- [Technical Architecture](02-TECHNICAL-ARCHITECTURE.md) — unchanged v1.2
- [GitHub: Reuse workflows](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)
- [GitHub: Transferring a repository](https://docs.github.com/en/repositories/creating-and-managing-repositories/transferring-a-repository)
- [DeepSeek API documentation](https://api-docs.deepseek.com/)

---

**Maintenance:** Track implementation progress by slice. Update the Product Requirements and Technical Architecture only when an actual functional or architectural decision changes; reorganizing this roadmap does not require changing them.
