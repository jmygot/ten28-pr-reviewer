# Company PR Reviewer

An on-demand C#/.NET 10 reviewer for GitHub pull requests. A developer requests a review by adding a new top-level PR **Conversation** comment:

```text
@company-code-reviewer review
```

The trimmed command is case-sensitive and must be the entire comment. PR creation, pushes, edited comments, ordinary issues, and inline review-thread comments do not trigger a review. The worker verifies the requester currently has write/admin-equivalent repository permission before generating a review. It submits only a non-blocking `COMMENT` review; it never approves, requests changes, or merges.

## Configure the central release

The checked-in `YOUR_USERNAME/company-pr-reviewer@v0.1.0` references are development placeholders. **`v0.1.0` is a proposed release, not a verified published tag.** Replace `YOUR_USERNAME` with the central repository owner. Review and publish the central source before installing a caller workflow, then verify the intended protected release tag or replace it with a reviewed full commit SHA.

Keep these two references aligned with the intended worker release:

1. The central action reference in [the reusable workflow](.github/workflows/review.yml).
2. The reusable-workflow reference in [the caller template](.github/workflows/ai-review.yml.example).

Publish a reviewed release containing both `action.yml` and the reusable workflow before installing callers. A workflow pin does not implicitly pin a separately referenced action: verify both locations. Do not point either location at contributor-controlled branches.

## Install in a caller repository

1. In the **caller repository**, create an Actions repository secret named **`DEEPSEEK_API_KEY`** under **Settings → Secrets and variables → Actions**. Keep the key in each private test caller during development; do not store it in the public central repository, YAML, or source files. No personal access token is required.
2. Copy `.github/workflows/ai-review.yml.example` from this repository to **`.github/workflows/ai-review.yml`** on the caller's **default branch**, removing the `.example` suffix. Replace the owner and release reference as described above. GitHub ignores the inactive template in this central repository.
3. Allow the selected central reusable workflow/action and the pinned `actions/setup-dotnet` dependency in the caller's Actions settings. The caller review job grants only `contents: read` and `pull-requests: write`; the called job cannot elevate those permissions. The command guard grants no token permissions and receives no DeepSeek secret.
4. Create a small synthetic test PR and post the exact command above. Inspect the caller repository's Actions run and job summary.

The caller passes its key explicitly:

```yaml
secrets:
  deepseek_api_key: ${{ secrets.DEEPSEEK_API_KEY }}
```

The central workflow passes that named secret and the caller-scoped `${{ github.token }}` to the composite action. Only the worker execution step receives them as `DEEPSEEK_API_KEY` and `GITHUB_TOKEN`. Event metadata and `GITHUB_STEP_SUMMARY` remain the runner's original values. No caller or PR source is checked out or executed.

## Execution, concurrency, and diagnostics

The composite action installs .NET 10 and publishes the trusted worker from its own directory into a unique `RUNNER_TEMP` directory. The root `global.json` keeps SDK selection on stable .NET 10. The action ref becomes the worker informational version used in duplicate detection; a vetted new tag/SHA therefore identifies a new worker release. Invoke the packaged action remotely with a tag or SHA: local `uses: ./` has no remote action ref and is intentionally rejected.

The caller's review job groups concurrency by repository and PR with `cancel-in-progress: false`. Accepted requests for the same PR serialize while different PRs remain independent; the called workflow has no duplicate lock. Older pending requests can be replaced by newer ones. History checks skip successful reviews with the same head, effective policy, model, and worker version before another billable model call. A new commit permits a fresh manual request. This is best-effort duplicate prevention, not transactional exactly-once delivery.

Logs and `GITHUB_STEP_SUMMARY` report outcomes, skip reasons, the failing stage, review identity, coverage, severity counts, elapsed time, available token usage, and publication uncertainty. Unknown usage remains unknown. Reports exclude prompts, source, credentials, and finding text. Exit codes are `0` for completion/intentional skip, `1` for failure, and `130` for cancellation. A failed step-summary write warns without changing the review exit code.

Local build, mocked tests, and an offline demonstration:

```powershell
dotnet build Ten28-PrReviewer.slnx --configuration Release
dotnet test Ten28-PrReviewer.slnx --configuration Release
dotnet run --project Ten28-PrReviewer/Ten28-PrReviewer.csproj --configuration Release -- --demo
```

The demo makes no GitHub or DeepSeek calls. Real cross-repository access, token permissions, scheduler behavior, and model compatibility require the Phase 2 pilot; local YAML/script validation does not establish those live behaviors.

## Ownership transfer and rollback

After transferring the central repository to `YOUR_ORG/company-pr-reviewer`, update **both** action/workflow references and publish a new reviewed release under that owner. GitHub does not redirect action or reusable-workflow references after transfers. Once private, configure approved organization caller access and secret sharing; personally owned callers may no longer be able to consume it. Roll back using a previously verified workflow/action pair under the current owner.

See [the architecture](Docs/02-TECHNICAL-ARCHITECTURE.md) and [the roadmap](Docs/03-DEVELOPMENT-ROADMAP.md) for implementation details and pilot steps.
