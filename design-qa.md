# KiCad download landing page — design QA

final result: passed

Scope: the selected second landing-page concept, extended by the user's explicit
request for light and dark themes. This is not qualification of the full KiCad
engineering workflow or Windows two-version updating.

## Visual truth and captures

Selected reference:
`/home/holyglory/.codex/generated_images/01a06e57-5f4c-7b23-8476-b42467c8c6c7/exec-b17bfa13-9076-485d-8b16-f2c92566bfb8.png`
(1058×1487 pixels). Implementation: https://kicad.vr.ae/;
public generation 35, source `d08b9fbbfa`.

Full-view comparison paired that reference and the rendered dark capture in one
image-review input. Compare proportions at a common width, not raw pixel sizes:
the implementation uses a 1440×1700 CSS viewport, density 1, with a 1440×1945
full-page capture. The source normalizes to about 1440×2024. The slightly shorter
page and the added install/archive disclosures are intentional, requested
download functionality rather than a different visual direction.

Public captures: `/tmp/kicad-public-dark.png`, `/tmp/kicad-public-light.png`.
Retained eight-cell initial/full-page evidence, including both themes at
390×844 and 1440×1700, and both expanded archives:
`/mnt/build-storage/codex/kicad/evidence/landing-public-v1/`.
The reviewed manifest is `manual-review.json` in that directory. All sixteen
images were inspected. Expanded mobile initial captures supplied the focused
control/text review; full-page captures supplied whole-archive topology.
Native selector values are masked by the verifier, not absent from the page.

## Required fidelity surfaces

- Typography: self-hosted Inter variable, readable body and metadata, centered
  heading with intentional mobile wrapping; no clipped text. Wide/narrow
  toolbar icons keep accessible names when labels collapse.
- Spacing: selected centered hero and four-column download dock retained;
  two-column mobile dock, compact header, one normal page scroll. No nested
  scroll regions or unintended horizontal overflow at sampled widths.
- Colors: graphite/mint dark theme and cool-white/deep-green light theme;
  both have measured passing text contrast. System preference follows changes
  until a stored manual override is selected.
- Images: dedicated photographic hero variants, compiled into the host with
  unmodified licensed icons. No rasterized UI, handmade substitute PCB, missing
  artwork, stretched board or clipped mounting holes.
- Copy: preview status is explicit; no fabricated benchmarks, customers or
  completed-autonomous-design claims. Current downloads come from the signed
  feed and actual catalogue, not mock dates or the last staged baseline.

## Comparison and repair history

1. First packaged preview omitted PNGs because of the repository's broad ignore
   rule (P1). Explicitly tracked both intended assets; post-fix public images
   decode and a browser-downloaded Windows ZIP matches its published SHA-256.
2. The archive heading lacked heading semantics (P2). Converted it to a real
   heading without visual drift. The final public audit has zero critical
   findings and passing continuation coverage.
3. Layout-complete local runs exposed cold response overhead: 25.7ms, then
   13.2ms and 10.8ms during staged improvements. Prepared finite UTF-8 variants
   and made deployment readiness require a real HTML response. The ready public
   generation's local browser navigation measured 1ms; public LCP samples were
   108–132ms. Earlier failed runs remain retained and are not relabelled passes.

Public governed run `t20260911T092305Z-4f2613` passed page layout, all 56 archive
GET/hash/HEAD/range checks, signed platform feeds, and the existing native
caption Update/Cancel/Save/restart journey. Its eight visual decisions passed.
Warnings were reviewed: the keyboard skip link is intentionally hidden before
focus; the optional expanded archive starts in its existing document position
and remains scroll-accessible. These are not missing primary downloads.

## Interactions and limits

Actual browser checks exercised light/dark button and keyboard activation,
reload persistence, live system-theme changes, Mac-chip uncertainty, mobile
fallback, release expansion/collapse, platform filtering/restoration, installation
disclosure, source/instructions navigation and a real primary package download.
No page console errors were observed. Additional responsive samples at 320 and
900px and 200% text enlargement showed no horizontal overflow. Widths between
samples and native Safari/Firefox engines were not exhaustively tested.

The compiled repeatable public-browser journey also passed in governed run
`t20260911T093445Z-97cac6`: keyboard theme activation, both persisted themes,
release and installation expansion/collapse, filtering/restoration, live
download-route selection, loaded artwork and absence of browser errors.

No actionable P0/P1/P2 visual findings remain. Minor P3: photographic lighting
and the board angle differ slightly from the generated reference. Both retain
its chosen composition and visual quality; no further redesign is proposed.

Implementation checklist: selected layout complete; both themes complete;
downloads and archive complete; original update/catalogue routes preserved;
public visual and compatibility verification complete.
