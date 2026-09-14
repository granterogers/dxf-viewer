# HANDOFF.md

Snapshot of where this repo stands as of **2026-09-14**, written so a fresh Claude session (or a
human) can pick up with zero prior context. See [CLAUDE.md](CLAUDE.md) for standing project rules
and the verification workflow — read that first, this file is the "what happened and what's left."

If you're a Claude session reading this: the conversation that produced this handoff is long and
detailed; this file is the distilled result, not a substitute for it, but it should be enough on
its own.

## What this app does

A WPF/.NET 8 Windows desktop DXF/DWG viewer ("Microvellum DXF Viewer"), built for a
cabinetry/woodworking CNC cutlist workflow. It exists so the user can view exported DXF/DWG files
without opening full CAD software, and specifically to cross-check rendering against Microvellum's
own built-in DXF viewer — Microvellum is the ground truth for "does this look right."

## Current state: what works

- **Native DXF parsing** via `netDxf` (modern, `$ACADVER`-header files) with a fallback custom
  group-code parser (`DxfParser.ParseLegacy`) for headerless pre-R12 legacy DXF, which is what most
  of the real cutlist export samples actually are.
- **Native DWG parsing** via `ACadSharp` (`DwgParser.cs`) — no ODA File Converter, no external
  process. Handles Arc/Circle/Line/LwPolyline/Polyline2D/Polyline3D/Ellipse/Spline/Text/MText/
  Insert/Solid3D.
- **Multi-page support for DWG**: Paper Space layout tabs are composited (viewport-clipped,
  scaled crops of Model Space, respecting `FrozenLayers`) into their own page, selectable via a
  dropdown above the canvas (`DxfPage`, `DxfTabViewModel.Pages`/`PageNames`/`CurrentPageIndex`).
  DXF files always have exactly one page ("Model") — no DXF layout support was added, out of scope.
- **True 3D orbit-camera viewer** for DWG files containing `Solid3D` bodies (`DxfTabControl`'s
  `DrawScene3D`, orbit math in `CadGeometry.OrbitCameraBasis`). Renders the legacy `Wires`/
  `Silhouettes` edge data (only `Wire.Type == 1` — types 2/3 are garbage/degenerate, confirmed by
  direct inspection). Left-drag orbits, middle-drag pans, wheel zooms. Depth-cued: near edges
  render at full color, far edges fade toward the canvas background, so the wireframe reads as 3D
  at a glance (`DxfTabControl.DrawScene3D`, mirrored in `Program.cs`'s `DrawScene3DForTest`).
- **Microvellum color/rendering parity** for the cabinetry-cutlist layer-name conventions:
  `BORDER_*` → blue, `ROUTE_*` → green, `2D_DIM*` → red text on blue dimension lines/ticks
  (`CadGeometry.LayerColorOverride`, applied in `DxfScene.ApplyLayerColorConventions`) — regardless
  of whatever ACI color code the source file's entities actually carry.
- **Robust fit-view bounds** against real-world file quirks:
  - Percentile-based outlier rejection for isolated corrupt vertices (`DxfScene.RobustRange`).
  - `ROUTE_*`-layer entities excluded from bounds/outlier calc (they're real toolpath data that
    can legitimately range far outside the part's own footprint) but **visible by default** —
    these are two independent decisions, don't conflate them (see `LayerInfo.DefaultVisible` vs.
    `DxfScene.IsDefaultHiddenLayer`).
  - `ROUTEDIM`-dimstyle DIMENSION entities (CNC routing-distance annotations, not part edges)
    excluded from bounds via a per-entity `BoundsExempt` flag — see CLAUDE.md's "hard-won lessons"
    for why this is a per-entity semantic flag and not a statistical heuristic.
- **Dimension-text collision avoidance** (`CadGeometry.PlaceText`) that stops dense, short
  consecutive dimension values from rendering as fused/overlapping digits — staggers overflow
  labels onto a new line using a proper anisotropic (along-reading-direction vs. across it)
  bounding-box overlap test, iterated a few times to resolve cascading collisions.
  - Note: this staggers to a new *row*, which is legible but not always pixel-identical to how
    Microvellum lays out the same cramped cluster (MV sometimes keeps more values on one row via
    a font-size or spacing trick that wasn't reverse-engineered). Good enough for legibility;
    revisit only if the user specifically flags a remaining case.
- **Correct bulge-arc geometry** — a real bug (signed radius instead of `Math.Abs(radius)` in
  `CadGeometry.BulgePoints`) caused a 180°-reflected "bowtie"/starburst artifact on clockwise arcs.
  This was earlier *misdiagnosed* as "legitimate multi-region CNC toolpath data" for some files —
  that diagnosis was wrong and was corrected once the actual bug was found; if you see old
  commentary suggesting otherwise, trust this file instead.
- **Hairline stroke width** (`StrokeWidth = 0` in SkiaSharp, always exactly 1 device pixel
  regardless of zoom) instead of a fixed drawing-unit width, which used to blow up to dozens of
  pixels when fit-to-window zoomed into a small part.
- **File-locking tolerance**: all three file-open call sites (`DxfParser.cs` ×2, `DwgParser.cs`)
  request `FileShare.ReadWrite | FileShare.Delete` instead of `FileShare.Read`, since the app never
  writes to opened files. This was verified against a real repro (another process holding
  `FileAccess.ReadWrite` with only `FileShare.Read` granted — a common pattern for editors/viewers)
  where the old code failed with "used by another process" and the new code succeeds. A file locked
  fully exclusively (`FileShare.None`) by the other app still can't be opened — that's enforced by
  the OS from the other process's side and no flag on our end can override it.
- **Microvellum integration**: `AppSettings.MicrovellumExePath` + a "Open with Microvellum" action
  in `MainViewModel` to launch the same file in Microvellum directly for comparison, plus app icon
  and file-association groundwork from the `v1.3` commit.

Verified: full sweep of all 331 sample files (`samples/DXF/` + `samples/DWG/`, recursively) via
`--render-test` renders with **zero failures**, and a bounds diff against the pre-fix sweep
confirmed the outlier-bounds fix alone corrected **50+ previously-broken files** in
`samples/DXF/bad render/` with no regressions elsewhere.

## Known limitation (not a bug — a real technology wall)

**DWG `Solid3D` bodies with proprietary binary ACIS/SAB data cannot be rendered as solid faces.**
Investigation trail (don't redo this from scratch):
1. User pushed back hard on an initial "this data doesn't exist" claim — correctly, as it turned
   out. Re-investigation found ACadSharp 3.6.35 (the version installed at the time) simply didn't
   *expose* the `Solid3D.AcisData` property that a newer release does.
2. Upgraded `ACadSharp` 3.6.35 → 3.6.51 (already done, already in `DxfViewer.csproj`). Confirmed via
   direct reflection probing that a real ~65KB binary ACIS payload **is** present on affected
   entities (`s.AcisData != null`, `s.IsBinaryAcisData == true`).
3. Decoding proprietary binary ACIS/SAB into actual face geometry requires a real B-rep geometry
   kernel. Checked and ruled out: ACadSharp (exposes raw bytes only), netDxf (no ACIS support),
   ezdxf (explicitly documented as refusing to parse arbitrary ACIS bodies). No open-source .NET
   (or otherwise readily embeddable) library was found that does this decode.
4. Current fallback — the legacy `Wires`/`Silhouettes` edge data on the same `Solid3D` entity — is
   real, usable wireframe data (see "what works" above) and is the practical ceiling for a
   native-parsing approach. The only way to get actual shaded solid faces is either (a) a B-rep
   kernel becoming available for .NET, or (b) reintroducing an external converter step (ODA File
   Converter), which the user has **explicitly and repeatedly rejected** — don't propose it again
   without the user raising it first.

This has a tracking GitHub issue — search for "ACIS" in the repo's issues before re-investigating.

## Things intentionally left undone (ask before doing, don't assume)

- **Origin/UCS crosshair gizmo** seen in a Microvellum 3D reference screenshot was noticed but
  *never requested* — the user's actual ask that turn was about text clarity/coloration, not the
  gizmo. Don't add it speculatively; confirm first if it comes up again.
- **No DXF Paper Space/layout support** — only DWG got multi-page support. Not asked for, and the
  DXF path is deliberately kept minimal/stable.
- **README screenshots section is still a placeholder** (`<!-- Add screenshots here -->`).

## Things that still need a human's eyes (can't verify from a headless render)

- **Interactive multi-page DWG navigation** — the page dropdown, page switching re-fit/re-draw
  behavior — was only ever verified by reading the plan's logic, never clicked through in the
  actual running app. Open `samples/DWG/complex dwg/DML 25014 CAMERON HOUSE 014 MASTER BEDROOM.dwg`
  (21 layout tabs) and confirm the dropdown lists all of them and switching pages works correctly.
- **3D orbit/pan/zoom mouse interaction feel** — the math was verified via the `--render-test`
  harness at fixed angles, but the actual drag-to-orbit / middle-drag-to-pan / wheel-zoom feel in
  the running app hasn't been hands-on tested.
- **"Open with Microvellum" button** — `AppSettings.MicrovellumExePath`-driven launch was added but
  its end-to-end behavior (does it launch, does it pass the right file) hasn't been confirmed
  working in this session.

## Repo hygiene notes

- `samples/*.dxf` (repo root, ~19 files) were reorganized into `samples/DXF/` (confirmed
  byte-for-byte equivalents exist there for every one before deleting the root copies) — this
  handoff's commit finalizes that move. `samples/DWG/` and `samples/DXF/bad render/` are new
  sample sets added this session (~23.5MB total) specifically to cover DWG parsing and the
  "corrupt/tricky real export" cases the outlier-bounds work fixed; kept in the repo for
  regression-sweep reproducibility per CLAUDE.md's verification workflow.
- `AppVersion.cs` bumped `v1.4` → `v1.5` as part of this handoff, for the 3D depth-cueing feature
  added this session (per the standing "bump minor version per feature" rule — bug fixes in this
  same session did not each get their own bump).
- A separate, automatic **git-tag** version bump happens on every `git push` via a machine-wide
  Claude Code hook (`~/.claude/hooks/version-on-push.ps1`) — unrelated to the `AppVersion.cs`
  number above, don't confuse the two, and don't manually create/push version tags yourself.

## Suggested next steps, roughly in priority order

1. Do the human-verification pass listed above (multi-page DWG nav, 3D mouse feel, Microvellum
   launch button) and fix whatever doesn't hold up.
2. Revisit dimension-text stacking to more closely match Microvellum's exact layout for cramped
   clusters, if it comes up again as a specific complaint (see "what works" note above).
3. Periodically re-check whether any .NET-embeddable ACIS/SAB decoder has appeared before writing
   off solid-face DWG rendering permanently.
4. Fill in README's screenshots section.
