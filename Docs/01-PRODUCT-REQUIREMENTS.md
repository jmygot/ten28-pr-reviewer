# Company PR Reviewer — Product Requirements Document (PRD)

**Version:** 1.2 (DeepSeek Flash-first pilot; two-stage repository rollout)  
**Date:** 2026-10-27  
**Status:** Approved direction; implementation pending  
**Product type:** Internal developer tooling  
**Primary stakeholders:** Development team, GitHub organization administrators, security/IT, AI provider account owner

## 1. Executive summary

Build a centrally maintained, **on-demand AI pull-request reviewer** for multiple private repositories in one GitHub organization. A developer requests a review by posting this exact standalone comment in the **Conversation** tab of an open PR:

```text
@company-code-reviewer review
```

A small GitHub Actions workflow in the requesting repository invokes a reusable workflow from `company-pr-reviewer`: **initially a public repository under the developer's company-email GitHub account**, and **later a private repository transferred to the company GitHub organization**. The centralized **C#/.NET 10 worker** validates the requester and the PR, reads the proposed changes through GitHub's APIs, asks the **DeepSeek API (`deepseek-flash`)** for actionable findings, validates locations and review quality, and posts a **non-blocking GitHub pull-request review** with a summary and inline comments.

The implementation runs only on GitHub-hosted runners; the MVP needs **no web server, database, queue, domain, or always-on hosting**. **DeepSeek V4.1 Flash is the initial testing and MVP provider**; a DeepSeek Platform API key and approved prepaid balance are required. Claude remains an optional later provider, and Claude Team/API-credit setup is **not a prerequisite** for the pilot. No employee's personal AI chat login is used by the automation.

> **Important terminology:** `@company-code-reviewer` is a **text command** in the MVP, not a registered, mentionable GitHub user or GitHub App. Reviews will ordinarily be authored by `github-actions[bot]`. A genuine bot identity is a potential later enhancement.

### Two-stage ownership and visibility plan

| Stage | Central reviewer ownership | Visibility | Caller repositories | Credentials |
|---|---|---|---|---|
| **Stage 1 — Development/validation** | `YOUR_USERNAME/company-pr-reviewer` (GitHub user account registered with company email) | **Public** | Prefer private, synthetic-data test repositories; organization-owned test repositories only with approval | Repository-level secret on each caller; no secrets in public reviewer repo |
| **Stage 2 — Production** | `YOUR_ORG/company-pr-reviewer` (transfer the same GitHub repository) | **Private** | Approved private company repositories in the same organization | Organization-selected Actions secret, or repository-level secrets if the plan requires it |

The account's registered email does not make the repository organization-owned. Public shared workflows are accessible from private caller repositories if their Actions policy allows them; after transfer, private sharing needs to be explicitly enabled inside the organization. The runtime, CLI, and event/LLM contract do **not** change between stages; GitHub `uses:` references and access/secret policies do. Personally owned Stage 1 test callers should be disabled or replaced by organization-owned test callers at production migration because they cannot normally access the newly private organization action.

**Public development constraint:** The public repository must contain only code, sample policies, and synthetic test fixtures approved for publication. Do not include company source, non-public operational details, tokens, prompts embedding proprietary data, or raw prompt/response traces. Making the repository private later **cannot retract previously published code or public forks**.

## 2. Problem and opportunity

The company plans to stop using GitHub Copilot and move developers to Claude Team, but Copilot currently handles PR reviews. Anthropic's separately priced managed PR review is not the desired replacement. The team needs a familiar GitHub-native workflow without adding another expensive subscription or operating a new service.

### Desired outcome

- Preserve an accessible, consistent AI review step when Copilot is removed.
- Allow developers to **opt in per PR and per commit**; never review automatically on open, synchronize, push, or merge events.
- Keep model consumption visible and bounded; start with low-cost DeepSeek Flash, then evaluate Claude if quality or governance requires it.
- Centralize review policy, implementation, and releases across participating repositories.
- Give humans actionable suggestions; never delegate merge/approval authority to AI.

## 3. Target users and permissions

| Actor | Responsibility | MVP capabilities |
|---|---|---|
| Developer (repository write permission or higher) | Requests review | Post exact PR command; read feedback; request again after new commits |
| PR author/reviewer | Evaluates findings | Discuss or resolve inline comments; manually decide on changes |
| Development repository owner | Maintains public prototype | Owns and versions initial public reviewer; keeps all code and fixtures publishable; coordinates transfer |
| GitHub organization admin | Configures production organization | Approve transfer, private workflow sharing, selected repository secrets, repository enrollment, action policies |
| AI provider account owner | Manages model access | Configure DeepSeek Platform billing, API key, account balance, spend monitoring and emergency key revocation |
| Reviewer worker | Executes authorized request | Read PR metadata/diff, call API, post `COMMENT` review; cannot merge/approve |

**Authorization rule:** Only a real user whose **current effective repository permission** resolves to `write` or `admin` (including the `maintain` role, which GitHub maps to `write`) may initiate a review. Deny bots, suspended/unresolvable users, and permissions `read`, `triage`, or `none`. Do not rely solely on `author_association` or a comment's apparent text. An explicit exception list is out of scope for MVP.

## 4. MVP scope

### In scope

1. On-demand trigger from a **new top-level PR conversation comment** with an exact command, ignoring surrounding whitespace.
2. Development and validation through a **public user-owned** central repository, callable by approved private test repositories (including organization repositories only when explicitly permitted).
3. Production support for several **private** repositories in **one** GitHub organization through the **transferred, private** central repository; the worker, composite action, shared workflow, tests, default policy, and release tags stay centralized.
4. Safe GitHub Actions execution using **caller repository-level secrets in Stage 1**, switching to **selected-repository organization secrets** in Stage 2 where supported. Secrets are never stored in the public central repository.
5. Authorization and validation **before** invoking the model.
6. Review of the **current PR head commit** against its base using changed-file metadata and available patches.
7. A centralized prompt focused on real defects: correctness, security, validation, transaction safety, regressions, and significant performance problems.
8. Structured model output: finding severity, title, explanation, affected file, changed line, and suggested remediation.
9. Deterministic validation of findings; attach inline comments only when locations are valid in the current diff.
10. One **non-blocking** GitHub review (`event: COMMENT`) with a concise summary, bounded inline comments, and skipped-file information.
11. Deduplication for an unchanged PR head/policy/model combination; allow a fresh review after new commits.
12. Bounded input, output, run duration, retry behavior, and diagnostic logging without secrets or raw source code.
13. Repository-specific optional review policy **only from a trusted base branch**, subject to central limits.
14. A safe synthetic-data development smoke test, then a two-private-repository company pilot and documented production transfer/rollout procedure.
15. Versioned release references and a migration checklist for transferring ownership, switching to private, refreshing caller workflow references, and revalidating security.

### Out of scope for MVP

- Automatic reviews on PR creation, update, or scheduled events.
- A real `@company-code-reviewer` GitHub App user; dedicated bot avatar/name.
- A custom website, dashboard, webhook receiver, hosting service, SQL database, or cache.
- Auto-approval, `REQUEST_CHANGES`, merge blocking, committing patches, or automatic fixes.
- Full repository semantic indexing, RAG/vector database, or executing test suites from PR code.
- Supporting other GitHub organizations or public repositories as a guaranteed target.
- Claude/Qwen additional implementations or sophisticated LLM routing (retain an interface for later).
- Replies to individual inline review threads as command triggers.

## 5. Primary user journeys

### Journey A — First review

1. Developer opens an existing PR and posts `@company-code-reviewer review` as a new Conversation comment.
2. Caller workflow confirms the event is a PR comment and invokes the central reusable workflow.
3. Worker verifies requester's live repository permission, open PR, and valid exact command.
4. Worker obtains current PR head/base metadata and bounded changed-file patches.
5. Worker asks DeepSeek Flash to identify actionable issues using trusted policy and treats all PR content as untrusted data.
6. Worker validates proposed findings and checks the head SHA again.
7. Worker submits a `COMMENT` review with a structured summary and zero to ten inline findings.
8. Developer reads and acts on the feedback. Human review remains required per company policy.

### Journey B — Review after code changes

After the PR receives a new head SHA, posting the same command produces a new review for the new commit. Older reviews are retained for auditability. The worker does not attempt to resolve old review threads automatically.

### Journey C — Repeated request for identical commit

For a PR and current `head SHA + policy version + model ID` already reviewed successfully by this bot, the worker **skips another model call** and records a visible skip in the Actions run summary. It should not spam another PR comment. Failed or incomplete reviews must not be marked as completed.

### Journey D — Rejected or unsupported request

Invalid command, closed PR, insufficient permissions, automated account, or stale commit: do not call the LLM or submit a review. Record a sanitized reason in the GitHub Actions job summary. Large or unsupported diffs should receive a **partial-review disclosure**, not falsely claim full coverage.

## 6. Functional requirements and acceptance criteria

| ID | Priority | Requirement | Acceptance criteria |
|---|---|---|---|
| FR-01 | Must | Explicit manual trigger | Creating a PR or pushing a commit never starts model review; exactly formatted PR Conversation comment does |
| FR-02 | Must | Centralized reuse across both stages | Two approved private caller repositories invoke one versioned central workflow without copying C# code; development uses a public user-owned repository and production uses a private organization-owned repository |
| FR-03 | Must | Verified requester authorization | `write`/`admin` succeeds; `read`/`triage`/unknown/bot fails before any LLM call |
| FR-04 | Must | Latest PR data | Worker fetches PR on execution and records head/base SHAs; if head changes before posting, no stale review is published |
| FR-05 | Must | Safe bounded diff retrieval | Changed files are paginated, filtered, sized and counted; binary/oversized/missing-patch files are listed as skipped |
| FR-06 | Must | Central review policy | Valid, bounded findings focus on actionable defects; policy and model version are recorded in review metadata |
| FR-07 | Must | Trusted per-repo configuration | Only config from trusted base commit is used; malformed/out-of-range config falls back to safe default or fails closed |
| FR-08 | Must | Structured and validated findings | All published inline comments target an actual added/modified diff line with valid path and side; invalid comments are dropped or summarized |
| FR-09 | Must | Native, non-blocking review | Review uses GitHub Pull Request Reviews API with `event: COMMENT`; no approval/merge/changes-requested event |
| FR-10 | Must | Idempotent-ish behavior | Duplicate successful request for same review key avoids another provider request; concurrent same-PR requests are serialized through workflow concurrency |
| FR-11 | Must | Secret safety | API key never appears in logs, review text, model prompt, or untrusted code; only necessary permissions are granted |
| FR-12 | Must | Cost and timeout limits | Worker limits changed files, diff bytes/tokens, API output tokens, total review run time, and retries; over-limit behavior is visible |
| FR-13 | Must | Useful failure signals | Logs and job summary differentiate unauthorized, skipped, insufficient balance, provider outage, and invalid-output cases |
| FR-14 | Must | Pilot coverage | Safe development smoke tests and two approved company repos complete end-to-end authorized and unauthorized tests before expansion |
| FR-15 | Should | Model abstraction | DeepSeek implementation isolated behind `ILLMProvider` so other providers can be added without changing GitHub integration |
| FR-16 | Should | Basic usage accounting | Log review key, file counts, token usage where reported, duration, model, and estimated cost (not code content) |
| FR-17 | Must | Safe public development | The public reviewer repository, CI, fixtures, and logs contain no proprietary code or secrets; API keys live only on authorized caller repositories |
| FR-18 | Must | Safe production migration | Ownership transfer, visibility change, private sharing settings, updated **both** workflow/action `uses:` paths, required secret configuration, and smoke tests are completed before org rollout |

## 7. Review output contract

Every successful review should include:

- **Header:** `Company PR Reviewer`, model identifier, reviewed head SHA (short), and review scope.
- **Summary:** 2–5 sentences describing issues found and important exclusions.
- **Findings:** At most **10** actionable comments, highest severity first (`critical`, `high`, `medium`, `low`). Avoid stylistic-only findings.
- **Inline placement:** `path`, changed `line`, `side: RIGHT` (MVP: added/modified-side lines only); if invalid, include it in a clearly labeled *unanchored observations* section or drop it, according to confidence policy.
- **Coverage statement:** number of changed/reviewed/skipped files and reason for skipped files.
- **Audit marker:** HTML comment containing normalized bot signature, `head_sha`, `policy_version`, `model_id`, and worker release ID for deduplication.

Do not claim the code is safe merely because the model found no issues. Suggested phrasing: “No actionable findings in the analyzed diff. This does not replace human review or testing.”

## 8. Non-functional requirements

| Area | Target |
|---|---|
| Execution | GitHub-hosted runner, `ubuntu-latest`, .NET 10; one job per authorized command |
| Response time | Best-effort; MVP workflow timeout **10 minutes**; no hard latency SLA |
| Reliability | Transient HTTP failures retried with bounded backoff; no silent success for incomplete publication |
| Scalability | Multiple repos; concurrency serialized **per repository and PR**, no central always-on process |
| Security | Minimum Actions permissions, no untrusted code execution, strict authorization, secret redaction, prompt-injection boundaries |
| Privacy | Treat internal code as proprietary; require company approval for transmitting diffs to DeepSeek API |
| Observability | GitHub Actions run logs and summary; include request/repo/PR/SHA/model/usage but no code bodies |
| Maintainability | One central release; thin caller files; tests around command, authorization, diff/line mapping, dedup, output handling; reviewed migration plan for repo owner change |
| Public-stage supply chain | Pin reviewed central release to a full commit SHA where practical; verify central CI workflows never receive secrets on untrusted public PRs |
| Ownership transition | Transfer only after permission/security sign-off; update `uses:` references explicitly; allow private sharing and verify secret access before relying on production runs |
| Resource caps (initial defaults) | Max **30 changed files analyzed**, **250 KB** total patch text (plus bounded context), **10 findings**, max **10-minute workflow**; values reviewed during pilot |
| Configuration | Central defaults override any dangerous per-repo instructions; changes reviewed in code review |

Resource caps are suggested starting values—not GitHub or DeepSeek platform limits. Truncation and missed coverage must be explicitly reported.

## 9. Cost and commercial constraints

- **Initial provider: DeepSeek V4.1 Flash, model ID `deepseek-flash`.** Create a DeepSeek Platform API key, fund or verify API balance, and store `DEEPSEEK_API_KEY` only as a secret in the **private calling repository** during Stage 1. No Claude Console access is required to begin the pilot.
- As of 2026-10-27, official DeepSeek Flash pricing per million tokens is **$0.15 uncached input / $0.60 output off-peak**, or **$0.30 input / $1.20 output at peak**; cache-hit input has different rates. This is a dated planning reference, not a price guarantee. Output usage can include reasoning tokens.
- Initial approximate budget example: **50,000 uncached input + 5,000 total output tokens costs $0.0105 off-peak ($0.021 peak)** for one model call. Review complexity, retries and multiple calls may raise cost. Check current pricing before testing.
- Fund the account with a **small approved test balance**, disable unapproved automatic top-ups, capture provider-reported usage, and implement conservative per-review input/output/retry caps. Do not assume an API account exposes every desired hard spend-limit control; confirm available controls and monitor balance.
- **Claude Team is a later option only**: if the company decides to add Claude, set up a separate company Claude Console API key and verify any eligible Team API credits at that time.
- The public reviewer repository does **not** carry the DeepSeek API key. GitHub Actions charges/allowances follow the **calling repository** running the reusable workflow; confirm individual/test and organization plan allowances.
- GitHub Actions on private repos can consume billable runner minutes beyond plan allowances. Validate org plan and repository access features before rollout.
- No model pricing assumptions should be frozen into code or PRD. Estimate cost at runtime/configuration using current prices and captured token usage.

## 10. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Hallucinated or irrelevant feedback | Developer fatigue | Evidence-required findings, severity filtering, bounded count, real pilot comparison |
| Public prototype exposes internal design/code | Published content cannot be made secret retroactively | Keep only publishable code, generic policy, and synthetic fixtures in public repo; review commits, CI logs and docs before publishing; obtain approval |
| Source-code disclosure | Confidentiality | Security/IT approval, no public logs of proprietary data, use an approved DeepSeek Platform account and complete provider privacy/data-processing assessment |
| Transfer breaks caller workflows | Production reviewer unavailable | Update all caller `uses:` references and central workflow's composite-action reference; GitHub Actions does not follow transfer redirects; smoke test both repos after transfer |
| Private access unavailable after migration | Workflow failures | Enable private Actions sharing in organization, verify GitHub plan/actions policy and secrets, test org-owned private caller |
| Prompt injection in PR content | Manipulated review | Separate trusted system instructions from diff data; never follow instructions found in diffs |
| Exploitation of privileged Actions job | Repo compromise | Do not checkout/build/execute PR head; restrict token; validate request permission |
| Budget exhaustion | Service interruption | Spend cap, file/token limits, one requested review per commit, clear error handling |
| Repeated duplicate reviews | Cost/spam | Per-PR concurrency, review marker lookup, pre-call and pre-publish checks |
| Incomplete large diff | Missed issues | Skip large/binary/generated files with visible partial-coverage label |
| GitHub permissions/configuration differences | Failure to roll out | Pilot on two repos; verify org plan, shared-action access, tokens, secret policies |
| Stale results after push | Misleading comments | Confirm head SHA again before GitHub `COMMENT` submission |

## 11. Success metrics (pilot)

Collect for at least **15–20 historical PRs** plus live on-demand use in **two repositories**:

- **Authorization correctness:** 100% of unauthorized/invalid test events cause zero model calls.
- **Trigger correctness:** 100% of tested PR-open/push/update events cause zero model calls.
- **Comment validity:** 100% of posted inline comments reference a valid current diff location.
- **Operational success:** >= 95% of eligible, within-limit pilot requests complete (excluding known external outages; report both raw and adjusted rates).
- **Deployment transition:** An end-to-end on-demand review succeeds from the personal/public development release and from the org-owned/private production release, with no duplicate reviewer implementation.
- **Review utility:** Human reviewers mark >= 70% of published findings as useful/actionable (pilot target, adjustable).
- **Duplicate avoidance:** Repeated unchanged-commit test incurs zero additional model requests.
- **Cost visibility:** Each processed request records token usage when reported, and pilot produces estimated spend/review.

These are project acceptance targets, **not** guaranteed results.

## 12. Decisions and pending confirmations

### Confirmed

- C#/.NET 10; lightweight worker application, not an always-on web API.
- **Stage 1:** Public reviewer repository under a GitHub user account registered with company email; synthetic-data-only development and testing.
- **Stage 2:** Transfer the same repository to the company's organization, then make it private; use same reusable workflow + composite action on GitHub-hosted runners.
- One GitHub organization, initially private repositories.
- Strict **manual PR conversation-comment** trigger, exact command.
- **DeepSeek V4.1 Flash (`deepseek-flash`) initially**, in high-effort thinking mode; reserve provider interface for later Claude/Qwen support.
- Non-blocking review; summary + validated inline findings.
- No separate app hosting, database, or UI for MVP.

### To verify during setup (not blockers for writing these documents)

- Actual GitHub account username, organization name, GitHub plan, and two pilot repository names.
- Ability to transfer a repository to the target organization (create-repository permission and no destination name collision); production org private Actions sharing policy.
- Whether public-source publication, evaluation tooling, and any real-code testing are approved under company policy.
- Organization security/IT approval **before** sending any proprietary diff content to DeepSeek; use only synthetic PRs while this is pending.
- DeepSeek Platform account, API key and small approved test balance; Claude Team/Console credits are deferred until Claude is actually integrated.
- Initial configured model ID and measured review cost/quality on representative PRs.
- Whether the organization wants a custom GitHub App identity after pilot.

## 13. Source references (verified 2026-10-27)

- GitHub — Workflow triggers, including `issue_comment`: https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows
- GitHub — Access rules for reusable workflows (public/private callers and no redirects): https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations
- GitHub — Transferring a repository: https://docs.github.com/en/repositories/creating-and-managing-repositories/transferring-a-repository
- GitHub — Changing repository visibility: https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/managing-repository-settings/setting-repository-visibility
- GitHub — Sharing private actions/workflows with an organization: https://docs.github.com/en/actions/how-tos/reuse-automations/share-with-your-organization
- GitHub — Get repository permissions for a user: https://docs.github.com/en/rest/collaborators/collaborators
- GitHub — Create a PR review: https://docs.github.com/en/rest/pulls/reviews
- GitHub — Workflow secrets, including organization-plan limitation: https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-secrets
- DeepSeek — Models & pricing: https://api-docs.deepseek.com/quick_start/pricing/
- DeepSeek — JSON Output: https://api-docs.deepseek.com/guides/json_mode/
- DeepSeek — Open Platform Terms: https://cdn.deepseek.com/policies/en-US/deepseek-open-platform-terms-of-service.html

---

**Document relationship:** Read [Technical Architecture](02-TECHNICAL-ARCHITECTURE.md) for the design and [Development Roadmap](03-DEVELOPMENT-ROADMAP.md) for implementation order. Changes to core decisions should be reflected in all three documents.
