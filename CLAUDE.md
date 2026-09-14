# CLAUDE.md — DXF Viewer

Project-specific guidance for working on this repo. See [HANDOFF.md](HANDOFF.md) for the
current state of in-progress work, open questions, and what to pick up next.

## What this app is

A WPF/.NET 8 Windows desktop app ("Microvellum DXF Viewer") that natively renders DXF and DWG
files with no external converter (ODA File Converter was explicitly evaluated and rejected in
favor of native parsing). It exists specifically to let the user cross-check CNC cutlist/cabinetry
exports against Microvellum's own DXF viewer, so **pixel/behavior parity with Microvellum is the
standing bar for "correct" rendering** — not just "renders without crashing." When in doubt about
how something should look (colors, text placement, line weight), the answer is "however
Microvellum renders the same file," not a guess.

## Standing rules

- **Bump `AppVersion.cs`'s minor version every time a new feature is introduced** (e.g. `v1.4` →
  `v1.5`), not for bug fixes. This is a manual step distinct from the separate, automatic git-tag
  version bump a global hook applies on every push — the two numbers are unrelated and both are
  expected to march forward independently.
- Default to writing **no comments**; when one is needed, it explains *why* (a non-obvious
  constraint, a bug workaround, a surprising invariant), never *what* the code does.
- Never use the ODA File Converter or any other external conversion step — native parsing only.
- Keep the DXF path (`netDxf`) and DWG path (`ACadSharp`) independent. `DwgParser.cs` mirrors
  `DxfParser.cs`'s entity handling but is a separate file specifically so the mature, well-tested
  DXF path is never put at risk by DWG-specific changes.

## Verification workflow (there is no automated test suite)

This app is verified by rendering real sample files headlessly and looking at the output — there
are no unit tests. The loop that's proven reliable across this project's history:

1. Build Debug: `dotnet build -c Debug`
2. Render a specific file to a PNG and inspect it with the Read tool:
   ```
   DxfViewer.exe --render-test <file> <out.png> [pageName] [azimuthDeg] [elevationDeg]
   ```
   `pageName` defaults to `"Model"`; azimuth/elevation only matter for 3D (DWG Solid3D) scenes.
   This harness deliberately mirrors the real renderer's behavior (hairline stroke width, layer
   visibility filtering, text collision avoidance, depth-cueing) — if you change how the real
   renderer draws something, update `Program.cs`'s `DrawScene3DForTest`/`DrawSceneForTest` to
   match, or the harness will lie about what a user actually sees.
3. **Before considering any rendering change done, sweep all 331 sample files** (`samples/DXF/`
   and `samples/DWG/`, recursively) through `--render-test`, confirm zero failures, and — this is
   the step that has caught real, widespread bugs twice in this project's history — **diff the
   `bounds=[...]` output against the pre-change sweep** to catch any file whose fit-view changed
   unexpectedly. A one-file repro fixed in isolation is not proof of a safe fix; only the full
   sweep is. See HANDOFF.md's "Hard-won lessons" for two concrete cases where skipping this step
   would have shipped a regression.
4. Build Release: `dotnet build -c Release`.

A bash one-liner for the full sweep (adjust the output dir per attempt so you can diff sweeps):
```bash
find samples -type f \( -iname "*.dxf" -o -iname "*.dwg" \) > /tmp/filelist.txt
mkdir -p /tmp/sweepN
while IFS= read -r f; do
  safe=$(echo "$f" | tr -c 'A-Za-z0-9._-' '_')
  ./bin/Debug/net8.0-windows/DxfViewer.exe --render-test "$f" "/tmp/sweepN/${safe}.png"
done < /tmp/filelist.txt
```
(Run this from the Bash tool, not PowerShell — the sanitization pattern matters for later diffing.)

Files under `samples/DXF/bad render/` are named for known-tricky real-world exports (corrupt
vertices, oversized dimension text, bowtie arcs, stray far-away annotations) and are the first
place to check when touching rendering/bounds logic.

## Hard-won lessons (don't re-litigate these)

- **Statistical outlier rejection for fit-view bounds is a trap.** Two different general
  approaches (median/MAD, then gap-detection with IQR) were tried to exclude a stray far-away
  DIMENSION entity from blowing out the fit view, and *both* caused real regressions — silently
  clipping legitimate geometry in dozens of other files (multi-part DWG nesting sheets especially)
  — before a full-sweep bounds diff caught it. The fix that actually shipped safely was
  **identifying the specific entity by its own semantic marker** (a DIMENSION's dimstyle name
  starting with `"ROUTE"`, meaning it measures a CNC toolpath distance rather than a part edge)
  and exempting just that entity from the bounds calculation via `SceneLine`/`SceneText`'s
  `BoundsExempt` field — not guessing from statistics. If you're tempted to add another
  distance/percentile heuristic to `DxfScene.RobustRange`, don't — find the semantic signal in the
  source format instead, and always verify with a full-sweep bounds diff before considering it safe.
- **DWG Solid3D ACIS/SAB body data is a real, currently-unsolved limitation**, not an unexamined
  assumption — see the open GitHub issue for the full investigation trail (ACadSharp version
  needed for `AcisData` to even be exposed, confirmation the binary payload is genuinely present,
  and which libraries were checked and ruled out for decoding it). Don't re-investigate from
  scratch; read that issue first.
- Microvellum recolors known cabinetry-cutlist layer name conventions (`BORDER_*` → blue,
  `ROUTE_*` → green, `2D_DIM*` → red) **regardless of the color the source file's entities actually
  carry** — confirmed directly against real files. `2D_DIM` is further split by entity kind: the
  dimension/extension lines and ticks render blue like `BORDER`, only the value *text* renders red.
  This lives in `CadGeometry.LayerColorOverride`.
