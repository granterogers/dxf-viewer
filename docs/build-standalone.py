"""Bundles docs/index.html into one self-contained .html file.

The artifact version relies on two things a downloaded file does not get: supporting
files served alongside the page, and a <head> skeleton supplied at publish time. This
inlines every image as a data: URI and wraps the fragment in a real HTML document, so
the result opens correctly from any folder, with no images/ directory next to it.
"""
import base64
import io
import mimetypes
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "index.html")
OUT = os.path.join(HERE, "microvellum-dxf-viewer.html")

MIME = {".png": "image/png", ".svg": "image/svg+xml", ".jpg": "image/jpeg"}


def data_uri(rel_path: str) -> str:
    path = os.path.join(HERE, rel_path)
    ext = os.path.splitext(path)[1].lower()
    mime = MIME.get(ext) or mimetypes.guess_type(path)[0] or "application/octet-stream"
    with open(path, "rb") as fh:
        return f"data:{mime};base64,{base64.b64encode(fh.read()).decode('ascii')}"


def main() -> None:
    html = io.open(SRC, encoding="utf-8").read()

    # Pull the artifact-only viewport meta; a downloaded page needs the normal one.
    html = re.sub(r'<meta name="claude:viewport-fit-sides"[^>]*>\s*', "", html)

    inlined = []

    def swap(match: "re.Match[str]") -> str:
        rel = match.group(1)
        inlined.append(rel)
        return f'src="{data_uri(rel)}"'

    html = re.sub(r'src="(images/[^"]+)"', swap, html)

    title = re.search(r"<title>(.*?)</title>", html).group(1)

    # <title> and the font <link> belong in <head>; lift them out of the fragment.
    html = re.sub(r"<title>.*?</title>\s*", "", html, count=1)
    fonts = re.search(r'<link rel="stylesheet" href="https://fonts\.googleapis[^>]*>', html)
    font_link = fonts.group(0) if fonts else ""
    if fonts:
        html = html.replace(font_link, "", 1)

    doc = f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="description" content="What the Microvellum DXF Viewer is for and how it is used.">
<title>{title}</title>
{font_link}
<style>
  *, *::before, *::after {{ box-sizing: border-box; }}
  html {{ -webkit-text-size-adjust: 100%; }}
  body {{ margin: 0; }}
  img {{ max-width: 100%; }}
  [hidden] {{ display: none !important; }}
</style>
{html.strip()}
</body>
</html>
"""
    io.open(OUT, "w", encoding="utf-8", newline="\n").write(doc)

    for rel in sorted(set(inlined)):
        print(f"  inlined {rel}")
    print(f"\n{os.path.basename(OUT)}  {os.path.getsize(OUT) / 1024:.0f} KB  (title: {title})")


if __name__ == "__main__":
    main()
