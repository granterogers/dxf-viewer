# DXF Viewer

A fast, lightweight Windows DXF file viewer built with .NET 8, WPF, and SkiaSharp.

[![Build](https://github.com/granterogers/dxf-viewer/actions/workflows/build.yml/badge.svg)](https://github.com/granterogers/dxf-viewer/actions/workflows/build.yml)

## Features

- **Dark theme** — dark navy UI designed for reading engineering drawings
- **Tabbed viewing** — open multiple DXF files side by side with close buttons
- **Drag and drop** — drop any `.dxf` file onto the window to open it
- **Smooth pan & zoom** — mouse-wheel zoom centred on cursor, click-drag to pan
- **Fit to window** — press `F` to fit the drawing to the viewport instantly
- **Directory navigation** — step through all DXF files in a folder with `◀` / `▶` or arrow keys
- **Always on top** — pin the window above other apps for reference while working
- **System tray icon** — lives in the tray while running; right-click to open files or exit
- **Remembers last folder** — file picker reopens where you left off
- **Read-only** — viewer only; files are never modified

## Screenshots

<!-- Add screenshots here -->

## Requirements

- Windows 10 or Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

## Installation

### Option 1 — Download a release

Download the latest `DxfViewer-vX.Y.zip` from the [Releases](../../releases) page, extract, and run `DxfViewer.exe`.

### Option 2 — Build from source

```bash
git clone https://github.com/granterogers/dxf-viewer.git
cd dxf-viewer
dotnet restore
dotnet build -c Release
```

The executable is at `bin/Release/net8.0-windows/DxfViewer.exe`.

## Usage

| Action | How |
|---|---|
| Open file | `Ctrl+O` or drag & drop onto window |
| Close tab | Click `×` on tab, or `Ctrl+W` |
| Fit drawing | `F` key |
| Zoom | Scroll wheel (centred on cursor) |
| Pan | Click and drag |
| Next/prev file in folder | `→` / `←` arrow keys, or `◀` / `▶` buttons |
| Cycle tabs | `Ctrl+Tab` / `Ctrl+Shift+Tab` |
| Always on top | Click **Pin** button |
| Open from tray | Right-click tray icon → Open File… |

## Supported DXF Entity Types

| Entity | Notes |
|---|---|
| `LINE` | Single segment |
| `CIRCLE` | Rendered as smooth curve |
| `ARC` | CCW angle convention; Y-flip applied |
| `POLYLINE` | 2D with vertex list + SEQEND |
| `LWPOLYLINE` | Lightweight polyline; bulge arcs discretised |
| `TEXT` | Position, height, rotation; `%%c`/`%%d`/`%%p` decoded |
| `MTEXT` | Plain text extracted |
| `ELLIPSE` | Discretised to polyline |
| `SPLINE` | Approximated via control polygon |
| `INSERT` | Block references flattened with scale/rotate |

Files without a `$ACADVER` header (pre-R12 / legacy format) are parsed with a built-in fallback reader.

## Roadmap

- [ ] DWG support via ODA File Converter
- [ ] Print / export to PDF or PNG
- [ ] Layer visibility toggle
- [ ] Measurement tool

## License

MIT — see [LICENSE](LICENSE)
