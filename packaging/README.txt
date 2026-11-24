Microvellum DXF Viewer  v2.2
============================

Runs as-is. No installation, no .NET runtime to install, no converter.

  1. Unzip anywhere (a desktop folder, a network share, a USB stick).
  2. Double-click DxfViewer.exe.

Requirements: 64-bit Windows 10 or 11. Nothing else.


What it's for
-------------
A Microvellum work order drops dozens of DXF files into one folder. Open any one
of them, then step through the rest of the folder with the arrow keys instead of
going back to the file dialog each time.


Keyboard
--------
  Left / Right    previous / next drawing in the same folder (wraps at the ends)
  F               fit the drawing to the window
  Ctrl+O          open a file
  Ctrl+W          close the current tab
  Ctrl+Tab        next tab        (Ctrl+Shift+Tab for previous)

You can also drag a .dxf or .dwg file onto the window, or pass one on the
command line.


Toolbar
-------
  Previous / Next         step through the folder
  Open File               open one or more drawings, each in its own tab
  Fit to Window           re-centre the drawing
  Export PNG              write an image of the current drawing
  Measure                 click two points for a distance, with dX and dY
  Light Mode              white drawing background instead of dark
  View Source             open the raw file in Notepad
  Open with Microvellum   launch the current file in Microvellum
  Always on Top           keep the viewer above other windows

"Open with Microvellum" asks for the Microvellum executable the first time and
remembers it afterwards.


Known limits
------------
  - DWG solid bodies draw as wireframe, not shaded faces.
  - 3D wireframes are not visible in Light Mode; use the dark background.
  - Paper Space layout tabs are read from DWG only, not DXF.
  - Hatches draw their boundary, not the fill pattern.
  - It is a viewer. Nothing is ever written back to your files.


Settings
--------
Window preferences are kept in:
  %LOCALAPPDATA%\DxfViewer\settings.json
Delete that file to reset to defaults. Nothing else is written outside the
folder you unzip to.


Built in-house at INNERGY.
https://github.com/granterogers/dxf-viewer
