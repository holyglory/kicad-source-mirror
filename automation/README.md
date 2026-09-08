# Codex-operated KiCad automation

Implementation branch: `feature/codex-kicad-automation`, based on
`f638a860a05b3e48d1074314a656ad9b8f597466`. This is an incomplete implementation
of the approved six-milestone program, not a release.

## Current source boundary

The compiled C# service uses the official MCP SDK over STDIO and generated KiCad
protobuf messages over a native NNG binding. It provides explicit session start,
attach, reattach, inspection, root-schematic opening and open-document queries.
The read-only component guidance tool resolves version-pinned class and instance
guidance with explicit conflict results. `kicad_engineering_design_validate`
validates the combined electrical, architectural and instance-guidance model
against explicitly supplied, version-pinned knowledge libraries. It returns
inherited/local guidance, named-property conflicts and abstract connections that
have no explicit net realization. The strict `engineering-design:1` XML section
preserves these owners together; it is not a native schematic snapshot and does
not generate or synchronize native files. Missing libraries and unknown fields
are errors, not silently discarded inputs.

`kicad_design_bindings_inspect` accepts the composed `design:1` document: the
engineering section, typed native schematic hierarchy and explicit sheet/symbol
bindings. It resolves by UUID and sheet path, including repeated screens and
multi-unit symbols. It reports unresolved or ambiguous links separately from
reference/value/unit differences and preserves native coverage gaps. The codec
retains unresolved bindings for repair; serialization does not authorize applying
them. This is not live synchronization, pin-connectivity comparison or proof of
complete native reconstruction.

`kicad_design_reconcile_properties` prepares a three-way reverse projection of
native reference/value/unit/placement changes into the engineering model. It
preserves concurrent instructions, reports competing edits and inconsistent
multi-unit/shared owners, and does not add coordinates to unchanged coordinate-free
models. Its candidate is only a property-level proposal: other native snapshot
changes and coverage gaps remain explicit. It performs no file writes, connectivity
comparison, live revision admission or synchronization checkpoint advancement.

`kicad_design_plan_placement` prepares forward connected-symbol translations from
the same baseline, desired engineering model, observed hierarchy and declared
libraries. It combines equal-displacement symbols on one sheet into a native
selection and resolves repeated-screen geometry once by exact identity. Existing
locks, conflicting shared placements, rotation/mirroring changes and unsupported
electrical changes prevent partial movement proposals. Missing coordinates do not
request movement. Results retain coverage gaps and unprojected native changes;
they require live revision admission and connectivity verification before use.

The internal `DesignRecoveryStore` preserves the full composed baseline, observed
hierarchy, declared library snapshots, exact desired-file bytes (including invalid
XML), and any unconfirmed native operation. Atomic replacement and revision-token
checks prevent overwriting a competing recovery record. Reopening a record does
not prove that its pending operation committed or that synchronization completed.

`kicad_design_recovery_observe` reads one absolute recovery-store path for an
explicit attached instance. An optional `expectedRevisionToken` rejects a changed
record. It verifies the exact pending native operation, then returns its retained
receipt separately from a fresh typed hierarchy snapshot. The current snapshot
may contain later edits or undo; it is not the operation's historical result.
Wrong instances, roots, document epochs and regressing revisions are rejected.
The saved baseline, desired bytes (including invalid XML) and pending command
remain untouched, including on cancellation or concurrent recovery changes.
This is a read-only recovery step, not automatic reconciliation or write-back.
The compiled MCP/native Linux journey exercises failures followed by successful
observation without file churn. Actual Codex Desktop and Mac qualification remain
separate required evidence.

`kicad_schematic_hierarchy_reconcile` accepts baseline, desired and observed
native typed hierarchy XML. It combines independent changes by exact instance
paths and object identities, including supported branch insertion, removal and
reparenting. Project-wide settings retain one owner across sheet copies. Repeated
instances must agree on physical contents; only reference/unit/variant projections
validated against their instance records are excluded from that comparison.
Conflicts retain all three sheet versions and return no partial native operations.
The response includes a `snapshotToken`. To choose an entire conflicted sheet,
pass `choices` keyed by its returned `instancePath`, with `xml`, `native` or
`baseline`, and the same token as `expectedSnapshotToken`. Whole-sheet selection
intentionally replaces that complete version, including independent edits within
it. A changed snapshot, unknown conflict path, unresolved sibling or incompatible
choice for a shared physical sheet cannot produce a partial batch. Choices are
not persisted and do not authorize live edits.
The internal `DesignRecoveryStore.ResolveHierarchy` API can retain reviewed
choices separately in a version-2 recovery record, using the recovery revision
token to prevent competing writes. Reopening revalidates their hierarchy token
and native epoch/sequence. A changed hierarchy or native revision requires
clearing the old resolution before saving refreshed state. Invalid desired XML
cannot be resolved, but remains recoverable. Records without choices retain the
version-1 format. `kicad_design_recovery_plan` exposes the saved reconciliation
state for an explicit instance and absolute recovery-record path; it does not
contact the editor or claim the observation is current. It returns the recovery
revision and hierarchy snapshot tokens. `kicad_design_recovery_resolve` requires
both tokens and explicit whole-sheet choices and writes only that recovery
record. Neither tool advances the baseline or writes either design
representation. Keep recovery records in their own directory, separate from the
instance registry's session JSON records. The compiled STDIO journey verifies
inspection, persistent choices and reopening, wrong-instance/stale-write/locked-
file failures followed by success; direct handler checks cover cancellation
before the write. Native application and automatic synchronization remain separate.

`kicad_design_recovery_refresh` persists a freshly captured native hierarchy into
an existing recovery record. It requires an attached instance and the exact
recovery revision token. Baseline, libraries and desired-file bytes (including
invalid XML) remain unchanged. Competing recovery writes reject the refresh;
changed native content or revision invalidates prior hierarchy choices. An
unchanged snapshot makes no additional write. Pending mutations are deliberately
rejected: inspect and reconcile their exact receipts before replacing the saved
revision needed for retry. This operation only refreshes the recovery observation;
it does not write the design XML or edit KiCad. Incomplete native tracking remains
explicit and is not authorization for synchronized mutation.

The internal `DesignRecoveryFileObserver` subscribes to an explicit design file
before its initial read. Each notification captures the current saved bytes into
recovery, including invalid XML, without changing the baseline or editor. Atomic
save replacements are observed; failed persistence retains a notification for a
later retry. Unchanged bytes retain choices without rewriting the record, while
changed bytes invalidate prior choices. Cancellation cancels the receive call,
not the editor. `kicad_design_intake_start` owns a continuous intake task for an
explicit instance, recovery record and design path. `kicad_design_intake_list`
recovers process-local intake IDs after a lost start reply. `kicad_design_intake_wait`
inspects the latest status or waits for a newer sequence without polling. Invalid
XML stays preserved and watched; persistence failures pause until
`kicad_design_intake_resume` receives the current paused sequence. Cancelling a
wait leaves intake running. `kicad_design_intake_stop` and service shutdown await
owned tasks, so they cannot write after returning. The service rejects duplicate
normalized design or recovery paths within its registry; this is not cross-process
file ownership. Session IDs do not survive service restart; start a new intake
against the retained recovery record. These tools never launch or close editors,
apply native changes, or advance the baseline. Continuous file intake is not yet
automatic bidirectional synchronization.
Successful plans include merged XML, ordered native operations and explicit
coverage gaps. This tool does not access files, apply a batch, resolve electrical
intent or advance the synchronized baseline. Concurrent edits inside a reparented
branch can still require explicit conflict resolution. Linux native tests verify
independent text/placement reconciliation, repeated-sheet operation deduplication,
reparenting and undo/redo; compiled STDIO tests verify tool results and failures.

Symbol definitions explicitly retain native De Morgan body-style semantics,
separately from ordinary custom styles named `Standard` and `Alternate`.
`demorganBodyStyles=true` requires that exact two-style pair; contradictory names
are rejected instead of silently discarded. The distinction survives typed XML,
native cache replacement, undo/redo and native save/reopen. Existing XML without
the flag retains ordinary named-style semantics; names alone are not evidence
of De Morgan intent. This does not imply complete library inheritance coverage.
The native schematic-cache parser rejects explicit `extends` declarations rather
than loading and later saving them without their parent relationship. This rule
does not change external symbol-library parsing. Already flattened root definitions
may retain a diagnostic parent name in memory; that is not persisted inheritance
and does not prevent their capture.

Registry records describe
the last verified connection, not live process health. KiCad owns its process and
project files; disposing the service's process handle does not kill an editor.

Startup persists an **unverified** launch receipt before creating KiCad. If startup
is cancelled or interrupted, `kicad_instance_pending_launches` exposes the receipt
for recovery without claiming the process is running. `kicad_instance_reattach`
checks the exact native identity and requested project before promoting it to a
verified connection record. `kicad_instance_saved_sessions` discovers historical
records after MCP restarts; listing does not attach or probe them. Previously
verified records also pin the process epoch. The Linux native journey cancels
before the first handshake, confirms the process survives and recovers it from
a fresh registry. Linux startup defaults to native software rendering; it still
requires an available graphical display. Native Mac validation remains pending.

`kicad_schematic_save_state` reads native schematic save flags and the modified
sheet-instance paths without changing the visible sheet. It checks loaded
schematic screens only, not project-settings persistence or external disk changes;
it is not a discard/close authorization. The compiled MCP/native journey verifies
dirty-to-saved transitions and recovery of unchanged unsaved objects and revision
after shutting down and restarting MCP against the same saved connection record.

Typed schematic metadata also retains the project's dimensionless dash/gap,
text-offset, label-size and overbar-height ratios. The planner emits one explicit
project-wide operation across repeated sheets, rejects inconsistent projections,
and preserves conflicting ratio edits for resolution. Native batches apply this
group with scoped undo, cache invalidation, rollback and retry receipts. Linux
native checks cover actual dash-pattern changes, saved project values and keyboard
undo/redo. Other project settings remain an explicit reconstruction gap.

Native graphical line segments use `SchematicLine` with `SLT_GRAPHIC`. A generic
`SchematicGraphicShape` segment is rejected before mutation because the pinned
schematic writer cannot save that representation. This preserves the existing
native format instead of allowing an edit that later blocks saving.

The native extension adds explicit automation startup and session discovery.
Its capability response currently advertises only `session.info` and
`version.read`. A Linux native check has opened schematic windows in two isolated
instances and captured their actual software-rendered canvases as PNGs. This
preview is exposed through a preliminary MCP tool with viewport metadata and an
explicitly incomplete revision-tracking flag. Current-sheet presentation checks,
sheet activation and operation-receipt inspection are also exposed. Independent
region/layer images of any explicitly targeted loaded sheet are available through
the separate render tool below. PCB/3D views and complete synchronized
observation remain unfinished.

`kicad_schematic_render_views` accepts an explicit instance and
`RenderSchematicViews` protobuf JSON: the discovered document descriptor plus
one to four keyed views. Each view declares a nanometre `region` (`position` and
`size`), `widthPixels`, `heightPixels`, and optional `nativeLayers`. Dimensions
are 64–2048 pixels, with at most 8,388,608 pixels across the batch. Empty layers
select supported artwork independently of the GUI layer visibility, including
the native drawing-sheet frame/title block and page boundary. The response
provides the exact layer ID/name catalogue for subsequent layer-specific calls;
`drawing_sheet` and `page_boundary` can be requested separately or omitted.

These are native offscreen Cairo images, not crops of the human canvas. Regions
are aspect-fitted, and each image includes the exact pixel-to-sheet transform.
Private object copies keep painter cache/flag changes out of the user's objects.
Images share one supported native snapshot, and the MCP image blocks follow the
keyed view order. The target can be the displayed sheet, another loaded sheet, or
a particular instance of a repeated sheet, without navigating the editor. The
private painter resolves instance references, text and text geometry using that
explicit path, rather than the displayed sheet's cached text bounds. Rendering
uses printing-style artwork. Transient overlays and PCB/3D views remain unfinished. Cancellation
releases the caller's IPC wait; an already-dispatched bounded native render may
finish without delivering its images. It does not modify or undo the design.

Page frames use a private copy of the native drawing-sheet template with its own
coordinate environment and cached items; the global/alternate template is never
swapped. Title, sheet name/path, page numbering, variant text and first-page rules
use native drawing-sheet resolution. Native tests distinguish the built-in empty
layout's compatibility item from a truly void allowed layout, preserving both.
Whole-page test views derive their regions from the actual native page bounds,
not an assumed standard paper size; arbitrary requested regions still clip normally.

Linux native checks exercise overview/detail regions, notes-only and wires-only
views, exact transforms, deterministic repeated results, invalid requests and
recovery, while preserving the human image, viewport, design snapshot and unsaved
state. The compiled MCP journey delivers multiple exact native PNGs plus matching
metadata on displayed and non-visible root/repeated sheets. The repeated-sheet
journey compares full artwork and reference-only images with the root and either
instance displayed, including sheet-variable text and global labels. Explicit
text-measurement tests preserve the normal cache and cover rotated/mirrored
reference fields. This is not actual Codex Desktop
or native Mac qualification, complete render-feature coverage or complete native
revision tracking.

Presentation findings identify the sheet instance, objects, measured values and
policy thresholds. Excessive-crossing findings also include each distinct
crossing region and its involved wire IDs for local close-ups and repairs,
rather than only a bounding box spanning the signal. The Linux native journey
checks three crossings, repair to two, and same-net false-positive protection.
Text-size limits are explicit millimetre policy values, not electrical rules.
For ordinary stroke-font and outline-font text, page-fit facts use native glyph geometry,
including the project overbar metric, schematic drawing offset, rotation and
renderer-default stroke width, rather than the approximate editor hit box.
Focused native tests compare shifted glyph extents at both orientations, verify
stroke-width changes and exclude empty/device text from this measurement path.
Outline-font checks also compare the bounds with pixels painted by the actual
native Cairo schematic painter using the repository's Noto Sans font fixture,
at both orientations, for plain text, overbars and mixed markup. Cairo switches
fill/stroke state by glyph type so overbar strokes between outline letters remain
visible. A two-pixel raster tolerance covers edge quantization, not missing marks.
The native journey positions text from its measured extent just inside the page,
changes the metric to cross the edge, verifies the localized warning and clears
it through undo. Text-box containers, fields and additional font/markup combinations retain their existing
explicit coverage limits; this is not exhaustive glyph-clipping qualification.
Native facts include table cells, sheet pins and label fields with their owning
object identities. Covered merged-table cells are excluded from visible-content
findings, matching the native painter. Linux journeys check tiny text and repair
in these nested objects, including merged-cell false-positive protection.
Text boxes and table cells also expose separate native font-metric text bounds:
their rectangular containers are not clipping regions. The verifier reports
container overflow and detects text extending beyond the page even when the
container fits. Native journeys exercise horizontal and rotated multiline
overflow, restoration to a short note, and hidden merged-cell exclusion, with
retained canvas captures. These are geometry checks, not exhaustive glyph or
occlusion analysis.
Full glyph clipping/occlusion and complete multi-page coverage remain open;
an incomplete report cannot declare the diagram clear.

Each native journey retains only `automation/artifacts/native-session-current`.
Before the next journey, that directory is moved intact into
`automation/artifacts/native-session-history`; older `native-session-results`
outputs are preserved too. This keeps per-run evidence bounded without deleting
historical local artifacts. Coordinator retains the hash-bound run archive.

The read-only `kicad_schematic_metadata` tool returns typed screen identity,
page/title data, project text variables, bus aliases and embedded-file records.
It also preserves net-chain terminal references, netclass/color and persistable
member-net names. Pending declarations with missing terminals remain present;
the `committed` flag identifies native collection membership, not electrical
verification. Live committed state takes precedence over older restore-map values.
Native import tests exercise resolved, unresolved, partial and name-only declarations
on repeated sheets, typed XML equality, save/reload and restoring the original fixture file.
The XML planner supports schematic-wide declaration replacement/removal in one native
undoable batch. Membership is recomputed: callers cannot force the observed `committed`
flag. Native journeys cover rollback, retries, no-ops and keyboard undo/redo.
The net-chain coverage gap remains until complete identity and electrical-change
restoration are qualified; this is not proof of full schematic reconstruction.
Its structured result retains incomplete revision tracking and lists
unrepresented state. Native and compiled MCP journeys compare actual page/title
data and nonempty project variables/aliases, verify repeated-sheet screen identity,
and reject wrong views and staged transactions. A populated fixture passes a
real embedded PNG through typed XML into an isolated native schematic, then
checks payload preservation, native legacy-checksum migration, repeated-sheet
metadata and the compiled MCP round trip.
Metadata also distinguishes `loaded_native_format_version` (the version read
from disk; zero means unknown) from `writer_native_format_version` (this build's
save format). Native tests pin the latter to fork version `20260907` and preserve both through
typed XML and compiled MCP. These are provenance fields, not editable settings
or proof of complete serializer compatibility. Item planning rejects a loaded
or writer version above the supported `20260907` ceiling, and a loaded version
above a declared nonzero writer version. The native journey checks that ceiling
against the actual writer. Read-only typed XML still preserves newer provenance
for inspection. Zero-version legacy DTO plans remain inspectable but are not
compatibility proof or live-edit authorization. Full import/restore admission
and complete feature coverage remain unfinished.

The read-only `kicad_schematic_data` tool returns supported objects and metadata
from one native dispatch, with typed XML of that same data. Targets include the
exact sheet-instance path; unsupported state and incomplete revision tracking
remain explicit. Compiled STDIO MCP tests compare the result to native reads
and parse the returned XML back into equivalent data on root and repeated
sheets. Wrong instances, missing or inactive sheet targets, and reads during
an open commit are rejected; reads resume after cancellation of the commit.
This tool does not write files, restore documents or capture the whole hierarchy.

The read-only `kicad_schematic_hierarchy_data` tool captures every loaded sheet
instance in one native dispatch without navigating the editor. The typed XML
retains separate instance paths, shared screen identities, supported objects and
settings, plus every unsupported-state marker. Its result uses a canonical root
target and deterministic path ordering and does not depend on which sheet is
visible. Native/compiled MCP tests compare it with individual sheet reads and
exercise wrong targets, open-transaction rejection and recovery. This is loaded
hierarchy capture, not full-fidelity archive/restore or complete revision tracking.

`kicad_schematic_hierarchy_validate` checks supplied hierarchy XML for missing
child contents, orphan/duplicate instances, mismatched child identities, wrong
parent paths, recursive screens and inconsistent child references between
instances of a shared screen. It preserves the input and reports topology
validity separately from native serializer coverage gaps. It does not resolve
filenames, compare shared electrical/layout content, establish connectivity or
authorize restoration/mutation. Unit tests include valid repeated sheets and
must-catch invalid graphs; compiled MCP tests cover malformed XML/recovery and
real native hierarchy captures.

`kicad_schematic_hierarchy_plan` previews changes between supplied current and
desired hierarchy XML. It returns sheet-targeted operations for one native batch
across loaded sheets and supported new nested sheets, deduplicates identical shared-screen plans, and
rejects conflicting shared edits or inconsistent schematic-wide assets. It also
returns serializer coverage gaps and explicitly does not authorize live edits or
claim complete schematic recreation. Shared-symbol references, units and variant
projections are checked against complete placement records before comparing
physical edits, so moving a symbol preserves differing assembly options.
New nested sheets are created before their contents and supplied page/title
settings are explicitly initialized. Project-wide bus aliases must agree across
every sheet, including new sheets, and their replacement is emitted only once.
Stored root-page
records, unrepresented state and loaded-file provenance are rejected rather than
silently discarded. Subtrees can be detached through their parent reference,
preserving child contents for native undo and leaving files untouched.
Additional instances of existing screens require consistent complete shared
contents and explicit symbol/sheet placement records; references are not invented.
Subtrees can be reparented while retaining physical screen identities; the model
must supply the resulting instance paths and placement records. Changing
project-wide variant descriptions uses an explicit registry operation rather
than an implicit write through a symbol's projected description. Registry
entries must agree across every sheet and are emitted once per project. Changing
a registry name does not rename symbol/sheet variant declarations: those have
their own explicit placement records.

Native project registry entries (including empty descriptions) are retained in
typed XML metadata. Inferred names remain with their symbol/sheet declarations;
refreshing the editor's variant list does not invent saved project entries.
Registry replacement is undoable and participates in atomic rollback, revision
admission and retry receipts. Native dialog Add/Delete/Rename/Copy and description
edits also use commits, including variants with no component overrides.
The Linux rendered journey checks cancellation, invalid names, saving, undo/redo
and XML registry changes combined with symbol placement. Per-symbol description
projections are validated but never sent as a second registry write; independent
registry-description and placement changes merge without a false object conflict.
This is not complete change-stream coverage or automatic bidirectional sync.
Compiled STDIO tests cover discovery, target preservation, conflict/malformed-input
rejection and recovery. The underlying planner is exercised by the real Linux
editor journey; this is not Codex Desktop or native Mac qualification.

`kicad_schematic_observe` combines the current-canvas PNG and supported sheet
data in one native dispatch. It reads data, forces the native rendering
checkpoint, then rereads and compares supported content and revisions before
returning. The MCP result includes image content, viewport, object metadata and
typed XML; a detected change rejects the observation.
Capture also compares the complete published pixel-to-design transform and
visible-layer list before and after repainting, alongside sheet, center, scale
and revision. The Linux rendered journey exercises real keyboard zoom-in and
zoom-out in both instances: image/scale changes preserve supported design content,
revision and unsaved state. Zoom follows native preset steps, not an exact inverse
of an arbitrary starting fit scale. Mid-capture layer-change race injection and
independent render contexts remain unqualified.
Compiled MCP journeys
compare native image bytes and data on root/repeated sheets and exercise target
and open-commit rejection with recovery. Tracking and serializer coverage remain
incomplete. This is not yet an independent offscreen view, a full rapid-edit
stress qualification or a revision-safe mutation precondition.
The native edit journey also retains paired PNG/XML observations before a note
move/addition, after the edit, after graphical undo and after redo. It checks
exact current data/revisions, unchanged viewport, changed pixels after editing,
and exact restoration of earlier PNG bytes on undo/redo. These fixtures test
observation consistency, not aesthetic layout. Notes are separated so exact PNG
comparison does not depend on compositing order between overlapping glyphs;
the selected viewport does not show the entire page.

Native atomic batches now accept `replace_embedded_files` for schematic-owned
assets. The native decoder checks payloads and checksums before replacement;
the same commit owns asset and graphical changes, including rollback and one
undo entry. Linux editor journeys restore a real asset from typed XML, exercise
mixed and assets-only undo, redo, invalid payload/type/name rejection, stale
admission, receipt replay and unchanged-state no-ops. A newly named asset also
survives native save and reload. Public MCP mutation readiness, font/worksheet
consumer rendering and full-document reconstruction remain open; this is not
yet a complete asset-driven schematic generation workflow.

Metadata also preserves the native root-page record separately from page size,
title block and placement in a parent sheet. Native `set_root_instance` batches
support explicit presence/page number, no-ops, retry receipts, rollback and
page-settings undo. Linux journeys verify undo/redo, invalid page-number rejection,
XML preservation, absence/restoration and changed values surviving native reload.
Sheet-object XML accepts valid empty-parent-path root records alongside ordinary
placement records. Shared-screen reads and saves resolve one file-level value;
explicit updates cover every placement of that screen. Contradictory copies
produce an error instead of an arbitrary choice, and save preflight leaves the
schematic/project files unchanged. Linux journeys exercise explicit resolution,
undo back into conflict, redo and reload from both displayed instances. Broader
flat-hierarchy qualification and complete document reconstruction remain open.

No revision-safe editing, synchronized rendering, XML reconstruction, automatic
synchronization, routing, document extraction or simulation tools are advertised.
Their absence is unfinished work, not a reduction of scope. The authoritative
DevCoordinator completion ledger retains these outcomes.

## Source ownership

* `src/KiCad.Automation.Model`: engineering meaning, identities, units, XML and layout.
* `src/KiCad.Automation.Native`: protobuf/NNG client and instance registry.
* `src/KiCad.Automation.Mcp`: compiled STDIO executable and typed tool adapters.
* `tests/KiCad.Automation.Tests`: MSTest unit and compiled transport acceptance tests.
* `tools/KiCad.Automation.Validation`: manually invoked Mac build/evidence command; not a service.
* `../api/proto/common/commands/automation_commands.proto`: shared extension messages.
* Native behavior stays in `common`, `kicad`, `eeschema`, and `pcbnew` as appropriate.

There is no custom chat application or application-server client. Each execution
stack is co-located with its engineering repository. Project-specific confirmed
security assumptions are recorded in `../security-assumptions.md`.

## Development checks

### Symbol definition fidelity

Native snapshots distinguish normal, global-power and local-power symbols and
preserve the library's simulation, bill-of-materials, board and position-file
defaults separately from the placed component's explicit attributes. An untyped
legacy update cannot silently convert an existing power symbol to an ordinary
symbol. Linux native checks cover every combination of these four library
defaults, native library save/reload, and removal/restoration of connected global
and local power symbols from typed XML in two real editor instances.

The decoder rejects unknown or malformed symbol children and child/shape types
that the native library writer cannot persist. It constructs the replacement
definition before changing the destination; rejection does not leave a moved
symbol with missing artwork. Native tests cover direct rejection and editor-level
preservation of properties, connectivity and journal sequence. This is scoped
symbol fidelity, not complete library-cache reconstruction or automatic sync.

The definition also carries its own embedded files, separately from document
attachments. Asset names, kinds, compressed payloads and checksums are validated
before replacement. An explicitly empty file collection clears the library's
assets; omitting the collection cannot silently discard existing assets. Native
checks cover library save/reload and two-editor XML restoration, corruption
rejection and preservation of document attachments. Complete embedded-font
consumer coverage and unused library-cache reconstruction remain open.

### Connected symbol placement (preliminary)

`kicad_schematic_move_connected_symbols` sends a finite native drag through the
compiled MCP server. It accepts explicit instance/document and symbol identities,
an exact displacement in nanometers, and document epoch/revision plus a stable
operation ID. Native undo includes affected wires, labels and junctions. The
operation preserves the user's selection and rejects locked targets, stale
revisions and displacements that cannot be represented exactly.

After cancellation or a lost reply, do not submit the edit under a new operation
ID. Inspect the retained receipt or retry identical arguments with the same ID.
The MCP result reports `NotConfirmed` for errors after submission rather than
asserting that the native edit was rolled back. Use an explicit observation after
the result to obtain image and supported state together.

The native `move_connected_symbols` batch operation currently requires the
targeted sheet to be displayed and does not consume creations staged earlier in
the same batch. It is not full electrical correctness verification, unrestricted
offscreen editing, complete revision tracking or automatic XML synchronization.
The compiled editor journey covers actual MCP movement, connection preservation,
retry/stale behavior, observation and native undo; it is Linux evidence, not a
Codex Desktop or native-Mac qualification.

### Running checks

From the repository root, use:

```sh
devcoordinator2 test start . --test automation --tier development --client codex
```

This runs locked restore, compilation, MSTest, native configuration and compilation
of the changed native translation units and shared protobuf library. It does not
establish a fully linked native application or release readiness. Test
results and stdout/stderr are retained by DevCoordinator; inspect the log catalogue
before retrieving bounded diagnostics. Dependencies are pinned in
`Directory.Packages.props` and package lockfiles.

On the current Linux development host, large working files use the mounted
`/mnt/build-storage` volume. The existing `automation/artifacts/native` build
path links to `/mnt/build-storage/codex/kicad/builds/main-native`; the governed
Linux check groups use `/mnt/build-storage/codex/kicad/t` for `TMPDIR`. Keep the
volume mounted before running those checks. New large worktrees and materialized
evidence copies belong under its `codex/kicad/worktrees` and `codex/kicad/evidence`
directories. This does not relocate Coordinator's private retained evidence or
change the Mac validation workflow. Short IPC socket paths remain separate from
bulk temporary storage.

The compiled transport tests exercise a real MCP subprocess and the installed NNG
library against an isolated echo peer. The peer is a test fixture, not KiCad.
The real transport preserves a 2 MiB binary reply and recovers after cancellation
after the peer has received the request; the subsequent exchange receives its own
reply. Cancellation releases the client connection, not a committed native edit.
This does not establish unlimited message sizes or complete multi-image delivery.
The required actual Codex Desktop image/result round trip remains unverified.
The separate `native-foundation` check
builds the manager, both editor libraries and runtime assets, then tests two
isolated native manager sessions under Xvfb, opens their root schematics and
captures PNGs, including wrong-target rejection and recovery. It also exercises
native editing and a compiled MCP STDIO subprocess; that subprocess is not the
Codex Desktop application.

The native journal check now records completed schematic commits and keyboard
undo/redo, with bounded history and explicit recovery for expired cursors or
replaced documents. The Linux journey moves a text object, drives undo/redo
through its rendered editor window, checks the resulting native positions and
captures the reapplied result. Cancelling a staged native edit is checked for
restoration without a committed-change entry. Connected symbol/wire moves now
compare native connectivity, with keyboard undo/redo, transformed and multi-unit
symbols, hierarchy edits, and atomic rollback checks. Page Settings is exercised
through the actual dialog, including export to repeated sheets, save, cancellation
and preservation of redo. Schematic Setup also compares live serialized project
settings and records accepted changes after native refresh, without counting
cancel or unchanged OK as edits. The rendered Linux journey changes a formatting
ratio, cancels and accepts it, rejects a stale batch without overwriting the user
change, and restores the fixture with a fresh revision. Setup itself does not gain
undo support from this change. Read-only settings capture preserves stores and
dirty flags, follows nested/parent parameter ownership, and compares persisted
values rather than transient preset flags; genuine serialization failures remain
conservative invalidations. Ordinary best-effort saving is unchanged.
The journal still reports
`tracking_complete=false`: other direct settings/property paths,
complete change payloads and exhaustive revision tracking remain open. Native
batches reject stale tracked revisions and support operation-ID retries, but
that does not make their partial revision cursor safe for all persisted edits.
Clients must not use this partial journal cursor as a safe document revision.

`kicad_schematic_create` explicitly initializes an unsaved empty root schematic
for an attached project's missing `.kicad_sch`. An existing file or open document
is preserved, and retrying creation returns that document without replacing it.
Normal `kicad_schematic_open` still requires an existing file. Native project/file
ownership, dirty-session and modal-operation checks remain in force; save is a
separate explicit action. The headless CLI rejects the graphical creation flag.
Creation preserves a concrete identity/name in the project's root declaration;
a nil identity receives a new native ID. Mismatched root filenames and multi-root
creation are rejected without rewriting project declarations. The Linux journey
creates the missing root through compiled MCP, closes that MCP process, retries
from fresh MCP processes and verifies the same unsaved native document remains.
During automation reload, native automatic-repair notices are non-modal warnings
and remain in native diagnostics; repairs stay dirty until saved. Ordinary
interactive notices are unchanged. Creation is a construction primitive, not a
complete model-to-schematic generator or full reconstruction guarantee.

Typed schematic formatting includes operating-point display preferences:
voltage/current significant-digit precision (1–10) and the native automatic or
fixed unit ranges. These are display settings, not simulation results. Complete
formatting replacements require the explicit `operatingPoint` group; an omitted
group is rejected rather than filled from defaults. Native capture, XML planning,
undo/redo and save/reopen preserve these preferences. Changing them refreshes
the native operating-point display. Other project-owned settings and complete
simulation control remain unfinished.

Multi-unit reference formatting also round-trips through an explicit
`unitReference` group: the native separator character and first-unit character
code are preserved, including alphabetic and numeric conventions. Missing groups
and values outside native persisted ranges are rejected before mutation. The
Linux journey verifies two placed units changing from `U1A`/`U1B` to
`U1.1`/`U1.2`, then restoring alphabetic labels through keyboard undo. Native
display-text queries and independently rendered close-ups identify the same
supported revision; obtaining those close-ups leaves the human canvas unchanged.
Separate formatting checks exercise rollback, undo/redo and native save/reopen.
This is formatting fidelity evidence, not complete schematic reconstruction or
native-Mac qualification.

`kicad_events_wait` observes tracked schematic commits, including undo/redo,
through a separate per-instance native notification channel. Call it with an
attached `instanceId`; resume with the returned notification's `eventEpoch` and
`sequence`. `InitialStateRequired` and `RecoveryRequired` require a fresh native
snapshot rather than replaying guessed changes. `Change` contains the exact
document, journal revision, origin/operation attribution and incomplete-tracking
flag. A small head heartbeat detects a lost final notification without loading
designs repeatedly. `Deadline` means the bounded wait expired, not that the
design is certainly unchanged. `Failed` includes structured `errorCode` and
`errorMessage` fields, distinguishing invalid cursors/arguments, native API
rejection and transport failure. Cancellation remains cancellation rather than
being converted into a tool failure. It closes only the observer; it does
not close KiCad or discard unsaved work. Separate subscribers can observe the
same instance independently. This is partial committed-change notification,
not complete native edit coverage or automatic XML synchronization. The stream
does not yet report every document lifecycle/settings path, and snapshots still
carry their existing supported-format and incomplete-tracking limitations.

`HardwareRepositoryXml` reads and writes the root `hardware.xml` composition
model using the embedded `hardware-v1.xsd`: child project/model paths, stable
port identities, source documents and revisions, shared libraries and revisions,
and explicit inter-board interfaces. Descriptions and source-purpose prose are
preserved; ordering is deterministic. Validation rejects unknown XML fields,
unsupported versions, broken ownership references, competing interface bindings
and duplicate writable paths. Paths use portable repository-relative spelling;
this does not resolve symlinks, establish filesystem ownership, launch projects,
or verify electrical compatibility. Empty initial repositories are supported.

The model also includes electrical XML and version-pinned component guidance XML;
neither constitutes the complete schematic reconstruction format. Structural
diagram entities and revision-bound layout requests are model foundations, not
yet native UI instruments or an automatic synchronization service. A layout
candidate preserves established placement outside its explicit affected set,
cannot change locked placements, and cannot change electrical connectivity.
It still needs native validation and visual review before acceptance.

`ReadSchematicScreenData` collects current-sheet settings and supported top-level
objects in one native dispatch. Fields, pins and cells stay inside their owners;
objects are ordered by native identity. Unsupported objects are listed explicitly
with identity, native type and reason. Computed ERC markers are excluded. Its
`SchematicScreenData` record round-trips through typed XML, including coverage
gaps. Native journeys verify root/repeated-sheet reads, deterministic repeated
reads, XML equality, wrong-target rejection and open-commit rejection. This is
not yet a full multi-file design archive or a complete revision-safe observation;
the metadata gaps and incomplete tracking flag remain authoritative.

`SchematicItemDelta.Plan` compares two such records by exact native identity and
returns only changed create/update/delete operations. Reordering unchanged items
produces no edits. It rejects unsupported metadata changes, unresolved screen objects,
duplicate identities and type changes under one identity. Group dependencies
are ordered within a native batch. Sheet-symbol edits require an explicit parent
instance and child-screen identity; new repeated instances may reference a child
already represented in the current screen. New child contents and relinking to a
different child still require document-level dependency handling. It does not execute edits or
establish safe revision admission. The native journey applies XML-derived note
creation/movement in one commit, checks all unrelated supported objects, verifies
graphical undo/redo, applies the reverse deletion/restoration, and checks that
unchanged state produces no further operations. Automatic synchronization,
conflict handling and native guidance-authoring UI remain unfinished.

Bus-alias replacement participates in the same native undoable batch. Empty lists
remove aliases; unchanged lists create no edit. Names and members must be trimmed
and nonempty, and names must be unique because the project writer cannot retain
ambiguous definitions. Native checks cover rejected-batch rollback, retry/no-op
behavior, keyboard undo/redo, project-file persistence after replacement/removal,
and schematic reload. A separate native journey joins sheet-local probe nets through
bus entries and a global bus when an alias is added; rejection and undo leave them
separate, and redo reconnects them. Automatic synchronization remains unfinished.

Project text variables can also be replaced in the native batch. All sheets must
agree on the shared dictionary; the hierarchy planner emits one replacement,
including when new sheets are added. Empty values and Unicode/multiline text are
preserved, and an empty dictionary removes the variables. Native checks cover
expansion, rejected-batch rollback, retries/no-ops, keyboard undo/redo and saved
project contents. These are native project variables, not the complete engineering
instruction/class system. Settings refreshes preserve an explicitly launched
automation IPC endpoint without changing the user's ordinary API preference.

Group snapshots include the native design-block library link as typed data.
The model planner can update existing group metadata when membership is unchanged;
native repeated-sheet tests verify a planned name/link edit and native undo/redo.
Both current and desired group graphs are checked before planning: members must
exist on the same screen, have canonical identities, and cannot be duplicated,
multiply parented, self-referential or cyclic. Valid unchanged groups can coexist
with independent member edits; removing a referenced member without updating its
group is rejected. Native repeated-sheet fixtures verify nested membership,
library-link read/write/restoration and invalid-link rejection. New nonempty groups
are planned after their members, with nested groups before parents. Native batches
resolve members created earlier in the batch and record membership with the new
group in one commit. Tests create nested groups containing both new and existing
members, reject a later failed operation without leaving dangling ownership,
replay retries, undo/redo and recreate after undo. The planner also removes groups
parent-first while preserving members unless their deletion is explicitly requested.
Native removal records surviving member ownership for undo and avoids resurrecting
removed parents during commit finalization. Tests cover failed nested removal,
ungroup undo/redo, saved/reopened groups, and saved group/member removal.
Membership changes between surviving groups detach old memberships before
attaching new ones in the same native transaction. Tests cover reparenting,
rollback, retry, undo/redo and persistence. Native ownership restoration never
points surviving members at disposable undo images. Removing a group releases
live ownership while retaining its original membership for undo, so the same
batch can transfer survivors into an existing or newly created group. Tests cover
rollback, retries, undo/redo, persistence and reversing that transfer to recreate
the original groups. Complete group/hierarchy qualification remains open.
The fork's `20260907` schematic and symbol-library format persists UUIDs for
symbol-definition graphics, text, and library-owned pins. This is a local format extension, not an
upstream upgrade. Older files without those IDs remain readable; older KiCad
readers may reject newly saved files. Group persistence checks compare complete
screen snapshots, including symbol graphics, without dropping identities.
This targeted comparison is not complete schematic feature-coverage proof.
Native group deserialization also rejects unresolved, duplicate, cross-screen,
self-referential and conflicting-owner membership before changing state, instead
of silently omitting unresolved IDs. Real native rejection checks compare the
entire observed screen and revision, not just the response status.

Explicit embedded-file/font collections, root-page records, title blocks and page settings can
produce undoable native batch operations alongside object edits. Missing
records are not interpreted as deletion: removal requires an explicit empty
collection, root-page record or title block. Managed checks cover combined planning, no-ops,
presence rejection and merging an XML root-page change with an independent
native note. A native journey applies the XML-planned root-page operation with
undo/redo and persistence checks. The asset journey now also plans changes from
actual screen data and typed XML: mixed image/note removal and restoration,
undo/redo, unchanged no-op, and a newly named embedded PNG surviving save/reload.
Independent page, root-page, title-block and asset changes merge; embedded files and
their font setting remain one unit so competing edits within that unit preserve
conflicts. Title fields remain one conflict unit. Native tests combine title
replacement and note movement in one undo entry, verify failed mixed-batch
rollback, duplicate retries, stale rejection, no-ops, undo/redo, all title fields
after save/reopen, explicit clearing and rejection of NUL-containing text.
Page operations share geometry validation with the standalone command, load
drawing-sheet assets before mutation, and restore the project drawing-sheet
setting on rollback. Model planning orders embedded-asset replacement before
page changes that may use those assets. Native checks cover custom/standard
pages, malformed/missing/future layouts, combined page/title/note undo and
persistence, and pixel equality between standalone and batch page commands.
Page/title commit and rollback invalidate cached drawing content. Native symbol
round-trip checks also render before replacement and verify pin connectivity:
replaced pin allocations are removed from the connection graph before destruction,
and explicitly dirty connectivity is recalculated even for identical geometry.

`SchematicItemMerge.Plan` performs a three-way comparison against the last
synchronized item state. Independent object edits combine; native-only and
identical edits require no feedback mutation. Competing edits, delete/modify
conflicts and competing creations retain baseline/XML/native versions and return
no partially applicable candidate. Existing IDs cannot change object type.
Notes additionally merge independent wording and placement edits on the same
identity. Wording plus its hyperlink remain one unit; position plus typography
remain another, so competing coordinates or formatting are not guessed. Other
object types retain whole-object conflict granularity. Metadata
conflicts pause with preserved versions; unsupported item deltas are reported.
Two-instance native tests preserve native wording alongside model placement on
the same note, as well as an independent native note through an XML
update and merged XML serialization, reject a competing note without native
changes, and exercise undo/redo and reverse restoration. This planner does not
watch files/events or establish complete revision admission; those integrations
remain open. `Resolve` accepts explicit XML/native/baseline choices for existing
object conflicts, preserves unrelated edits and leaves unselected conflicts
paused. It only plans changes against the supplied snapshot; applying a choice
still requires revalidation of the live revision. Metadata and object-type
conflicts cannot be resolved through this object-choice interface.

`SchematicSyncStore` preserves the last baseline and both competing versions in
a local recovery record. Reopening retains conflict data and session identity;
stale writers, corrupt records and unsupported versions are rejected without
overwriting the saved record. Same-directory replacement and exclusive local
writer ownership protect accepted records from failed writes. This is recovery
data, not the engineering source or completion ledger, and is not a guarantee
against whole-system power loss. Native tests save and reopen a conflicting
record before merging independent edits and checking reverse changes after
undo. Automatic lifecycle integration and actual service-restart recovery are
still unfinished.

Conflict choices are persisted separately from the original three versions.
`SchematicSyncStore.Resolve` requires the current checkpoint token; its choices
are additionally bound to the exact content and native session. `Plan` reuses
those choices after reopening without replacing the conflicting source data.
Changed content or session identity rejects an old choice until it is explicitly
cleared and reconsidered. Acceptance of a new synchronized baseline clears the
old choices. The native journey reopens a saved choice before applying its
result; this does not yet establish live-service restart or automatic admission.

`kicad_schematic_xml_plan` exposes supported item comparison through read-only
MCP. Supply baseline, desired and observed typed screen XML with the same exact
sheet target. Results contain merged XML and proposed operations, or conflicts
with all available versions. Optional object-ID choices select `xml`, `native`
or `baseline` only for those supplied snapshots. The tool never reads or writes
files, persists choices or edits a native document; `liveMutationAuthorized`
is always false. Compiled STDIO tests exercise conflict details, explicit choice,
unchanged input, malformed XML and subsequent recovery. Full hierarchy/settings
changes and automatic synchronization remain unfinished.
Target identity is not mergeable metadata. Changed screen IDs or full document
descriptors (including repeated-sheet instance paths) return `target_changed`,
even for native-only or convergent changes, with no candidate or operations.
Managed tests cover all three input combinations and the compiled MCP boundary.

`SchematicDataXml` adds typed native-object XML and a generated XSD. It preserves
message presence, exact integer coordinates, embedded typed messages and asset
bytes, and rejects unknown or unrepresentable data. Native tests export a placed
symbol, remove it, reconstruct it from XML, and compare its native properties,
identities and connectivity. Variant symbol substitutions and pin-to-pad
overrides are included. Shared symbols now include complete placement records:
exact UUID paths, project names, references, units and local variants, including
other projects sharing a schematic file. Tests reconstruct a shared symbol from
one XML snapshot, compare both displayed instances and the other-project record,
reject malformed replacements without edits, and exercise native undo/redo and
saving. Pin-map delegation is retained as an instruction, not a resolved value.
Shared sheet symbols also carry complete parent-path records, including page
numbers, project ownership and local variants. A nested-sheet fixture reconstructs
one shared sheet symbol under two parent instances, preserves different page
numbers/variants and an other-project record, and checks rejection, undo/redo and
saving. That fixture deliberately retains the referenced child document through
another sheet: it does not establish generation of child documents from XML.
This proves object-level reconstruction, not a complete schematic snapshot:
whole-hierarchy generation, full save/load coverage, project settings, embedded
assets and automatic synchronization still need implementation and qualification.
Native files must not be deleted in a real project on the basis
of these isolated fixture tests.

## Runtime shape

After a validated build, the intended STDIO launch command is `kicad-mcp`.
Diagnostics use stderr exclusively. Set `KICAD_AUTOMATION_STATE_DIRECTORY` to
choose the registry directory. Start requires an explicit matching fork executable
and an existing `.kicad_pro` path. Native endpoints use absolute local IPC paths.
Do not point a production workflow at this development build yet.

## Manually invoked Mac validation

The command below is implemented but has **not been executed on a Mac**. Prepare
matching dependencies with the official KiCad Mac Builder workflow first. Reuse
its generated `toolchain/kicad-mac-builder.cmake`, including its customized
wxWidgets. The command does not run bootstrap scripts, clean dependencies, install
a worker, or change an existing worktree. The .NET SDK, CMake, Ninja and a shared
NNG library discoverable by the .NET native loader must already be available.

Run from a checkout containing this helper, substituting actual absolute paths
and a full commit SHA available from that checkout's `origin`:

```sh
dotnet run --project automation/tools/KiCad.Automation.Validation -- mac \
  --repository /path/to/kicad \
  --commit FULL_40_CHARACTER_COMMIT_SHA \
  --architecture arm64 \
  --builder /path/to/kicad-mac-builder \
  --toolchain /path/to/kicad-mac-builder/toolchain/kicad-mac-builder.cmake \
  --output /path/to/new-validation-directory \
  --native-tests 'YOUR_CTEST_SELECTION'
```

Use `--architecture arm64` for Apple Silicon and `--architecture x64` for Intel
target validation, with separate output directories and matching Mac/.NET
execution architectures. The command rejects a process/target mismatch; merely
cross-compiling does not establish execution of the other target. Existing
builder dependencies must support the selected architecture.

Choose a dedicated output path without spaces or CMake metacharacters. The
pinned native installer embeds that path into generated CMake commands; the
helper rejects unsupported destinations before creating the output or invoking
installation. This restriction is on the Mac validation build destination,
not on engineering-project paths or ordinary application use.

The output directory must not already exist. The helper fetches the exact commit,
creates a detached source worktree there, builds with the supplied toolchain,
runs the pinned native bundle install rules into its own `install` directory,
checks the installed manager/CLI Mach-O architecture slices and native commit,
runs the selected CTest checks and non-Linux-only MSTest checks, and retains the
source/build for inspection. It writes `result.json` and `evidence.tar.gz` on
success or a handled failure. The archive includes a versioned receipt, actual
step results, logs, CMake configuration and hashes of produced evidence. Images
are included only if checks actually produce them under
`KICAD_AUTOMATION_EVIDENCE_DIRECTORY`; the helper does not synthesize screenshots.

Transfer the result and archive through the existing Git/SSH workflow. On Linux
or Mac, the following verifies integrity and commit identity only:

```sh
dotnet run --project automation/tools/KiCad.Automation.Validation -- verify \
  --result /path/to/result.json --archive /path/to/evidence.tar.gz \
  --commit FULL_40_CHARACTER_COMMIT_SHA --architecture arm64
```

Receipt validation is not a Mac execution or an attestation. Even a successful
selected-check receipt has `CrossPlatformReady: false`; full rendered journeys,
Codex Desktop integration and every remaining milestone still need evidence.
Architecture-aware native receipts use schema 2 and identify the selected target
and native executable hashes. Legacy schema-1 receipts remain readable for
integrity checking but cannot satisfy an explicit `--architecture` request.
Native configuration and cache paths are isolated beneath the new output
directory. This does not configure signing, notarization or an unattended worker.
Self-contained distribution packaging remains unfinished.

### Linux installation staging (preliminary)

The governed `linux-package-stage` graph builds the normal native application
targets, publishes the MCP executable with its .NET runtime, and stages both in
a new private directory on the designated bulk volume. It uses `DESTDIR` with
KiCad's existing install rules; it does not install into the host's
`/usr/local` or modify a user installation.

The dependent installed-package journey verifies the staged file hashes, starts
the installed native manager under a test-owned virtual display, and uses the
self-contained MCP binary to create, render, retry, save and reopen a schematic.
MCP EOF must leave the native editor alive. It does not use
`KICAD_RUN_FROM_BUILD_DIR` or set `APPDIR` for this normal prefix layout.

Run from this Linux checkout:

```sh
devcoordinator2 test start /home/holyglory/kicad --test linux-package-stage --tier development --client codex
```

Each candidate retains its `staging.json` inventory; the generated
`automation/artifacts/distribution/current-staging.json` pointer identifies the
latest attempt for the dependent check. Neither is a public release catalogue.
Passing run `t20260908T083609Z-5ce6bd` proved this installed journey on Debian 13
x64. It does not prove independent Linux distributions, dependency closure,
public downloads, updating, Codex Desktop or either Mac architecture.
Those delivery requirements remain open; staging reports
`QualifyingDelivery: false`.

### Debian package preparation (preliminary)

The package-debian command builds a Debian 13 amd64 package from a hash-verified
frozen application catalogue. It uses Debian's shared-library analysis and
explicitly includes .NET dynamic runtime dependencies, Poppler utilities and
KiCad's ngspice library. It does not globally ignore missing-library errors.
Package names and installation prefixes are artifact-specific, preserving older
installations used by live editors; no system-wide KiCad alias is replaced.

The optional .NET LTTng 2.12 tracepoint provider is omitted only from the Debian
copy because Debian 13 uses the incompatible newer LTTng ABI. The receipt records
that exact omission. The original frozen tar archive remains unchanged.

The Debian package was built in governed run t20260908T105916Z-53e9c8. Its
construction receipt predates qualification and is retained unchanged.
Subsequent clean-Debian execution passed in deployment df9549b90e32b3da3 and
evidence check t20260908T141913Z-7e0621: no SDK or development libraries, a non-root
operator, and real packaged MCP create/render/reconnect, dirty-session survival
and native save. The verified render/evidence tree is
d2edcced4f3c692d03d8b4f92b6269c93219b079487d560f9b059e7a52dc1959.

Coordinator's explicit finite-container lifecycle now owns this check without
weakening NoNewPrivileges or adding a dummy service. The disposable qualification
deployment was removed after evidence retention. This proves the stated Debian
journey only: public HTTPS downloads, automatic updates, both native-Mac targets,
simulation and the complete engineering workflow remain unqualified.

### Authenticated update preparation (internal, preliminary)

The compiled distribution module verifies signed release metadata with the
publisher public key supplied by the installed application. It selects an exact
platform/package pair, downloads over HTTPS with signed byte-count and SHA-256
checks, and retains authenticated metadata across restarts. Repeated unchanged
checks do not rewrite the saved metadata. Invalid metadata, concurrent checks,
cancellation and persistence failures do not authorize installation.

The first installed-version envelope is an installer receipt, written after
the package has been verified. It is not the latest online feed or a manifest
embedded inside the archive whose own final hash it describes. Packaging and
first-install receipt provisioning still need integration.

The update feed (`updates/preview.json` or `updates/stable.json`) is separate
from the unsigned read-only public download catalogue. Payloads are fetched
only from its publisher's `artifacts/` path. There is no production signing key,
enabled public update feed, native Update button, periodic background worker,
archive installation, restart or rollback yet. A `download_verified` receipt
explicitly has `installationReady: false`; it does not prove native signing,
Mac notarization or a working automatic updater.

Linux tar-archive staging now verifies the downloaded bytes again, rejects
escaping/duplicate paths and special files, bounds expansion, preserves
ordinary executable permissions and internal library-file links, and checks
package identity before returning a new isolated directory. It never switches
the active installation. Unsupported archive layouts fail instead of being
partially installed; Mac archive preparation remains separate work.

Run focused signature, HTTPS transfer and persistence checks with:

```sh
devcoordinator2 test start /home/holyglory/kicad --test update-contracts --tier development --client codex
```

The focused contracts use synthetic release payloads and ephemeral test keys.
The `update-archive` graph additionally exercises the real frozen a5666e707777
Linux package through a test-only signed HTTPS feed, staged extraction and
rendered native/MCP create/render/reconnect/save. It passed in governed run
`t20260908T152803Z-450fb4`; retained native evidence tree:
`1a159879861fef8f91bae2e732436d9b8e5d9c47e93b99e9ea5eebe8e1fb38d5`.
This is not a public feed, installation activation, qualifying desktop delivery
or native-Mac execution.

The same `kicad-mcp` executable now has finite native-helper modes:

```sh
kicad-mcp --check-update --configuration /absolute/path/to/installed-updater.json
kicad-mcp --prepare-update --configuration /absolute/path/to/installed-updater.json
```

The trusted installed configuration names the HTTPS publisher, pinned public
key, installed signed receipt, exact channel/platform/package format, and
existing dedicated state and staging directories. It is not read from an
engineering repository. The helper emits JSON progress and a terminal result,
has a 15-minute ceiling, and accepts cancellation; normal invocation remains
the STDIO MCP server. Check-only mode fetches metadata but never the package.
Linux tar candidates can reach `archive_staged`, never
`installationReady: true`. Unsupported preparation targets are explicit.

Governed run `t20260908T154945Z-79bd1a` exercised the actual helper subprocess:
check without an artifact request, cancel during a real HTTPS transfer,
verify no partial/verified payload remained,
retry with the retained signed checkpoint, stage the frozen package, then
complete the native rendered journey. The disposable child used an ephemeral
test CA file without changing host trust or disabling production TLS checks.
This mode is not yet wired to a native startup/hourly worker or Update button.

Before returning a prepared Linux candidate, the helper also runs its native
CLI with isolated configuration/cache paths and checks the compiled commit
against the signed release. Startup failure, wrong commit, excessive output and
cancellation cannot produce a native-identity success. This check does not
replace rendering or full release qualification. Governed run
`t20260908T160325Z-3d07a9` covered those failures and the real package journey.

The low-level Linux activation module can switch a dedicated installation's
current-version link and switch back without replacing version files. Each
activation has a distinct pointer identity, including rollback, so stale
retries cannot silently reapply an earlier update. Contract tests cover open
file preservation, cancellation at the transition boundary, Unicode paths,
competing operations and conflicting pointer contents. Launchers generated for
future packages resolve physical version paths for libraries and resources.
The already published a5666e707777 artifacts remain unchanged.

This is an internal filesystem primitive, not an enabled installer: it does
not authenticate arbitrary version directories, handle native editor shutdown,
restart the application, qualify power-loss recovery, provision the first
installed receipt or implement Mac activation. No native Update button uses it
yet; the complete automatic-update workflow remains open.

### Verified initial Linux installation (preliminary)

The compiled bootstrap mode is available from the updated source build:

```sh
kicad-mcp --install-package --configuration /absolute/install-request.json \
  --publisher-key /absolute/trusted/publisher.spki
```

The request contains `schemaVersion: 1`, `installationRoot`, `archivePath`,
`envelopePath`, `origin` and `channel`. All three paths must be absolute; the
installation parent must exist, and the new installation root must not exist.
The publisher key is a DER SubjectPublicKeyInfo public key provided separately
by the operator's trusted bootstrap. The request cannot supply its own key.
The unsigned public download catalogue is not an installation envelope.

The installer authenticates the envelope and archive, extracts into new sibling
staging, verifies the native compiled commit, writes the installed signed
receipt and version-specific updater configuration, and creates the relative
manager/current selection. Only then does it publish the new installation root.
Cancellation or a conflicting destination before publication leaves existing
work untouched. No system package, desktop registration or running editor is
changed. Installation paths support spaces and Unicode.

Governed run `t20260908T173618Z-2e1fe7` used the real frozen Linux archive with an
ephemeral test publisher to exercise rejected inputs, cancellation at the
publication boundary, a competing destination, the actual bootstrap subprocess,
and native/MCP create/render/reconnect/save from the installed selection. It is
not native-Mac evidence, a production signing setup, power-loss qualification,
or the complete automatic updater. The public a5666e707777 preview predates
this bootstrap command; do not assume its embedded MCP executable provides it.

### Installation-bound update candidates (preliminary)

New bootstrap installations keep their original publisher policy at the root
and record a fingerprint for each verified version. A provisioned update
configuration includes `installationRoot`; `--prepare-update` then verifies and
registers a candidate beneath that installation instead of returning loose
staging. Its terminal status is `candidate_registered`, with the expected
current-selection identity and `installationReady: false`.

Registration inherits the original publisher/channel and rejects stale
selections, wrong publishers, older accepted metadata, changed configuration
and changed payload files. Repeating an unchanged candidate reuses it without
rewriting its files or selecting it. Ordinary timestamp changes alone do not
change the payload fingerprint. These receipts detect accidental/local drift;
they are not attestations against an operator who can rewrite all local trust
and receipt files. Roots predating the publisher policy/version-registration
metadata are not silently adopted by this path.

Governed run `t20260908T180714Z-ced0e8` exercised real bootstrap, helper-process
registration and unchanged-candidate reuse alongside the rendered installed
KiCad/MCP journey. The registration fixture deliberately uses a new signed
metadata sequence for the same real frozen application bytes; it does not prove
an application-version upgrade. Native close/restart, verified activation,
power-loss recovery, production signing and both Mac targets remain required.

Verified Linux activation now rechecks registered version bytes, configuration,
publisher identity and accepted metadata before changing the current selection.
Rollback is a separate operation bound to one update receipt and its exact
retained predecessor. It re-verifies that predecessor, rejects superseded
requests, and does not lower the accepted-update checkpoint. A damaged selected
candidate does not prevent recovery when its predecessor remains verified.

Governed run `t20260908T182001Z-81dcc7` covered rejected changed candidates,
stale/arbitrary targets, cancellation, metadata replay, activation retry,
receipt-bound rollback and retry, checkpoint preservation, and real native/MCP
rendering after restoration. It still uses different signed metadata sequences
for the same frozen application bytes. The selection APIs are not yet exposed
as a native Update button or an editor-closing/restarting command; they must not
be treated as a completed desktop update journey or power-loss qualification.

### Native updater lifecycle (preliminary)

The native project manager can own the compiled helper when its installed
launcher supplies absolute `KICAD_AUTOMATION_UPDATE_HELPER` and
`KICAD_AUTOMATION_UPDATE_CONFIG` paths. This context is separate from engineering
project fields. It checks on startup and schedules checks hourly, starts
preparation for an available candidate, and keeps an unchanged registered
candidate without preparing it again. Ordinary nonconfigured KiCad update
behavior is preserved. Helper output is bounded, parsed and checked; a timeout,
invalid response or exit failure never produces a ready-to-install state.

The native client owns a separate helper process group, supports cancellation,
and releases it on owner destruction. A still-running cancelled helper receives
a bounded termination fallback while the native event loop remains available.
There is no native Update button, visible readiness claim or editor restart yet.
The published a5666e707777 packages do not include this integration or set these
launcher variables. Automatic checking in a new packaged installation still
requires that launcher wiring and qualification.

Native Boost/CTest cases exercise the GUI event loop under a virtual display,
accelerated scheduling, repeated checks, malformed/contradictory responses,
failure recovery, cancellation and owner destruction, including a synthetic
helper that ignores the first cancellation signal. The rendered journey uses
the real manager and self-contained .NET helper against a test-only HTTPS
publisher: an up-to-date release, invalid configuration, and normal rendered
manager close during a pending check. It verifies HTTP cancellation and exit of
the exact owned helper process. Governed run `t20260908T190558Z-766a64` passed
these checks; this is Linux evidence, not a completed different-build update,
dirty-editor restart, production feed or native-Mac execution.

```sh
devcoordinator2 test start /home/holyglory/kicad --test native-updater-journey --tier development --client codex
```

### Public preliminary downloads

The download-only catalogue is at [kicad.vr.ae](https://kicad.vr.ae/downloads.json).
It serves the frozen a5666e707777 Debian 13 x64 application archive, matching
source, and the Debian installer. Public GET/hash, HEAD, range, private/control
path rejection and upload rejection passed in `t20260908T162709Z-a0e4b9`.
No engineering repository, native control, credentials or private evidence is
served there. Native-Mac packages are not yet available.

On Debian 13, after downloading the `.deb` to the current directory:

```sh
sudo apt install ./kicad-codex-preview-20260908T093000Z-a5666e707777-debian13-amd64.deb
/opt/kicad-codex/preview-20260908T093000Z-a5666e707777/kicad-codex
```

The matching STDIO service is
`/opt/kicad-codex/preview-20260908T093000Z-a5666e707777/kicad-mcp`.
The package preserves other KiCad installations. These downloads are preliminary:
automatic updating, both native-Mac targets and the full engineering workflow
remain unfinished. Public availability is not a qualifying-delivery reset.

### Source PDF inspection (preliminary)

The `kicad_source_pdf_page` tool reads a PDF declared under `documents` in the
engineering repository's `hardware.xml`. Supply the absolute manifest path,
the document's exact ID, and a one-based page number. The default image's
longest side is 1800 pixels; `maximumImageDimension` can request 1–8192 pixels.

The result contains page text and a PNG, plus the declared source revision,
document ID, page number and SHA-256 of the exact PDF bytes used by both native
tools. Supply that hash as `expectedSha256` on later requests when changes to
the source must be rejected. The declared revision comes from the manifest;
it is not a verified publisher revision.

This requires the local `pdftotext` and `pdftoppm` Poppler executables on PATH.
It uses no Python or JavaScript runtime and does not open a KiCad editor.
Native PDF permission failures are returned, not bypassed. Each call owns its
temporary source snapshot and renderer outputs; original documents are never
rewritten. Cancellation terminates only that call's native subprocesses.

When a page has no extractable text, its image is still returned for visual
inspection. The reader does not perform OCR or infer missing values. It does
not classify operating limits versus absolute maxima, interpret tables,
reconcile contradictions, create library components, or validate pin-to-pad
maps. Those engineering outcomes remain separate unfinished work.

Run the focused native-document check through DevCoordinator:

```sh
devcoordinator2 test start /absolute/path/to/kicad --test automation --check restore --check build --check source-documents --tier development --client codex
```

Compiled handler and STDIO evidence is not evidence of operation in Codex
Desktop or on macOS. Those acceptance checks remain required separately.
