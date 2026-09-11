# KAICad release maintenance

KAICad is a public development fork of the GitHub KiCad mirror. The canonical
upstream is <https://gitlab.com/kicad/code/kicad>. `main` contains KAICad development;
the existing development snapshot is a preview, not a stable KiCad-based release.
Application executable names, native file formats, and update trust do not change
as a consequence of the repository rename.

## Detect and prepare

`Prepare upstream KiCad release` checks canonical Git tags daily at **04:17 UTC**.
Stable three- and four-component numeric versions are recognized. `-rc`, nightly,
and `*.99.*` development tags are excluded. The initial 76 stable tags, through
10.0.6, are recorded with their exact peeled commit IDs in
`automation/distribution/upstream-tracking.json`; initialization does not port them.
Existing candidate branches deduplicate future checks. Changed or missing observed
tags fail inspection instead of silently retargeting a candidate.

Manual inspection and catch-up use the same workflow:

```sh
gh workflow run upstream-release.yml --repo holyglory/KAICad --ref main -f detect_only=true
gh workflow run upstream-release.yml --repo holyglory/KAICad --ref main -f detect_only=false
```

GitHub schedules run on the default branch and may be delayed. Public repositories
can have schedules disabled after 60 days without activity. Check the workflow's
state, re-enable it with `gh workflow enable upstream-release.yml --repo holyglory/KAICad`,
and run catch-up after inactivity. Do not manufacture commits to keep the schedule alive.

For each new tag, the workflow creates an isolated `codex/upstream/<version>`
candidate and a draft PR against `release/<major>.<minor>`. The first port starts at
the official stable tag and applies only the difference between KAICad's recorded
upstream base and its exact feature commit. Later ports merge upstream into the
maintenance line and carry subsequent KAICad feature changes forward. This avoids
merely merging a stable tag into a newer development snapshot.

The candidate contains `automation/distribution/upstream.json` (upstream source,
tag, exact commit, channel, feature source and KAICad revision) and
`automation/distribution/integration.json` (the concrete integration result).
The exact resulting candidate commit is in the retained `candidate.json` receipt;
it is not embedded in itself. Native and Linux package receipts include upstream
provenance as well as their existing source identity.

## Conflicts, checks and repair

A clean application of the changes is only an **integration candidate**. Conflicts
produce a blocked draft with its original source references and retained diagnostic
patch/error. Existing source, branches, downloads and earlier evidence are preserved.
The native build admission rejects a blocked candidate.

The successful preparation path runs managed release contracts, then explicitly
dispatches the existing native workflow for the candidate SHA on both Mac
architectures and Windows x64. Native dispatch is explicit because a PR opened with
`GITHUB_TOKEN` does not automatically trigger another PR workflow. Results and build
artifacts belong to their actual GitHub run IDs; a successful dispatch is not a
successful build. CI never receives the VPS publisher key or private engineering data.

Repair a candidate through ordinary commits on its existing branch. Inspect the
retained original patch and exact feature/upstream commits, resolve every conflict,
and set the integration result to `integrated` only after completing the port. Do
not replace the candidate branch or resolve conflicts by dropping features. When
local preparation itself fails, retry with a new output directory so the first
attempt remains inspectable. For a published draft, repair in place and rerun:

```sh
gh workflow run release-candidate-checks.yml --repo holyglory/KAICad --ref main -f source_commit=EXACT_CANDIDATE_SHA
gh workflow run native-delivery.yml --repo holyglory/KAICad --ref codex/upstream/VERSION -f source_commit=EXACT_CANDIDATE_SHA -f target=all
```

The native workflow's existing checks-only fixtures remain available for focused
repairs. A failed check must remain visible and must not be relabelled as passed.
Download and retain candidate packages/evidence in the project-owned build-storage
area before GitHub artifact retention expires. If evidence or package bytes are
unavailable, regenerate and requalify the candidate before review.

## Qualification and owner review

The release maintainer uses the existing VPS/Coordinator workflow to qualify the
**same frozen commit**, including Linux amd64, both macOS architectures and Windows
x64. Inspect `devcoordinator2 plan overview` and the declared schema-2 checks before
running the relevant full release graph. Existing native application/MCP journeys,
save/reopen, independent dirty designs, and signed two-version update/rollback
journeys remain release requirements. A build-only receipt or managed test pass
does not establish those behaviors.

Assemble the review with the upstream version/commit, exact KAICad commit and
revision, platform package/source checksums, actual native/Coordinator run refs,
compatibility changes and release notes. Use the draft PR and retained packages
for inspection. The maintainer must verify every required proof, then request the
owner's review of this exact candidate. A later source or package change invalidates
that approval. Existing unrelated engineering outcomes retain their own ledger state.

**Nothing in the scheduled or candidate-check workflows publishes a stable release,
signs an update, advances an update feed, or automatically merges the PR.**

## Publish an approved candidate

After the owner approves the verified candidate:

1. Retain the approved source SHA and artifact hashes in the Coordinator decision
   and release evidence. Use `kaicad-<upstream-version>-r<N>`; start at revision 1
   and increment for subsequent KAICad releases on the same upstream version.
2. Merge the reviewed candidate into `release/<major>.<minor>` using a fast-forward
   or a merge that preserves the exact candidate. Tag the **verified candidate SHA**,
   never a different merge commit that was not qualified. Preserve upstream tags.
3. Use the existing `kicad-validate` `stage-hosted`, `package-linux`, and signed-feed
   staging commands with that exact commit and version. Keep the matching source
   archive, notices and existing downloads. Their command help defines the required
   native receipts, previous catalogue and new immutable output directories.
4. Run the existing `release-sign` command on the VPS with the established publisher
   and previous signed envelope. Advance each platform sequence normally; do not
   introduce a key into GitHub or substitute a new trust identity. Stage the signed
   platform feeds together with their verified packages.
5. Apply the declared downloads deployment through Coordinator, verify public
   package hashes and the signed update paths, and record the actual delivery.
   Create the GitHub release for the same tag with notes and the public package,
   source and checksum links. Preserve older downloads and the last working feed
   if staging, deployment or verification fails.

These are owner-reviewed stable releases. The separately agreed **four-day
intermediate-preview cadence** remains in force and does not grant approval to
publish an unreviewed stable candidate.

## Repository checks

`devcoordinator2 test start CHECKOUT --test upstream-release --tier release` runs
the focused, complete workflow graph: restore, build, realistic small Git ports,
conflict/retry preservation, version/tag admission, hosted compatibility and the
download repository link. It does not build KiCad or claim a future stable port.
`KAICad release workflow checks` provides the hosted counterpart and manual retries.

Historical source snapshots without the upstream manifest continue to verify
against the original development base `f638a860a05b3e48d1074314a656ad9b8f597466`.
Malformed manifests never fall back to that legacy rule. Stable candidates must
match their canonical tag and must have a completed integration result.

The download replacement gets up to five minutes to verify the entire retained
archive catalogue before its real health check can pass. The previous generation
continues serving during that check. Startup logs retain the archive-verification
duration; this timeout is a failure ceiling, not a readiness shortcut.
