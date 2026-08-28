#!/usr/bin/env python3
"""Turns MANUAL.md into the pdf beside it.

The manual is written once, in markdown, so that the copy on github and the copy people print
cannot say different things. This only dresses it for paper.

Needs the `markdown` package, and Google Chrome, which does the printing. Chrome is used rather
than a dedicated html to pdf engine because it is already on every machine this is likely to be
built on, and it needs no system libraries of its own.
"""

import os
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "MANUAL.md")
PDF = os.path.join(HERE, "TwinCatAdsTool-Manual.pdf")

CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"

STYLE = """
@page { size: A4; margin: 18mm 16mm 20mm 16mm; }

body {
  font-family: -apple-system, "Segoe UI", Helvetica, Arial, sans-serif;
  font-size: 10.5pt; line-height: 1.5; color: #1b1b1b; margin: 0;
}

h1 { font-size: 24pt; margin: 0 0 4pt 0; }
h2 { font-size: 15pt; margin: 22pt 0 6pt 0; padding-top: 6pt;
     border-top: 1px solid #d8d8d8; break-after: avoid; }
h3 { font-size: 12pt; margin: 14pt 0 4pt 0; break-after: avoid; }
h1 + p, h2 + p, h3 + p { margin-top: 0; }

p, li { orphans: 2; widows: 2; }
ul, ol { padding-left: 18pt; }

code { font-family: "SF Mono", Consolas, monospace; font-size: 9pt;
       background: #f2f2f2; padding: 1pt 3pt; border-radius: 3px; }

pre { background: #f7f7f7; border: 1px solid #e2e2e2; border-radius: 4px;
      padding: 8pt 10pt; font-size: 8.5pt; line-height: 1.35; overflow: visible;
      white-space: pre-wrap; word-wrap: break-word; break-inside: avoid; }
pre code { background: none; padding: 0; font-size: inherit; }

table { border-collapse: collapse; width: 100%; margin: 8pt 0; font-size: 9.5pt;
        break-inside: avoid; }
th, td { border: 1px solid #dcdcdc; padding: 4pt 7pt; text-align: left;
         vertical-align: top; }
th { background: #f2f2f2; }

blockquote { margin: 8pt 0; padding-left: 10pt; border-left: 3px solid #d0d0d0; color: #444; }

a { color: #10559a; text-decoration: none; }

hr { border: none; border-top: 1px solid #e0e0e0; margin: 16pt 0; }

.subtitle { color: #666; font-size: 10pt; margin: 0 0 14pt 0; }
"""


def render():
    try:
        import markdown
    except ImportError:
        sys.exit("This needs the markdown package: pip install markdown")

    with open(SOURCE, encoding="utf-8") as handle:
        text = handle.read()

    body = markdown.markdown(text, extensions=["tables", "fenced_code", "toc"])

    return (
        "<!doctype html><html><head><meta charset='utf-8'>"
        f"<style>{STYLE}</style></head><body>{body}</body></html>"
    )


def print_to_pdf(html):
    # Outside the repository on purpose: chrome keeps writing to its profile after it has been
    # asked to stop, so a scratch directory next to the source ends up staged for commit.
    scratch = tempfile.mkdtemp(prefix="twincatadstool-manual-")
    page = os.path.join(scratch, "manual.html")

    with open(page, "w", encoding="utf-8") as handle:
        handle.write(html)

    if os.path.exists(PDF):
        os.remove(PDF)

    # Chrome prints and then does not always exit, so it is left to run and stopped once the file
    # it was asked for has appeared.
    chrome = subprocess.Popen([
        CHROME, "--headless=new", "--disable-gpu", "--no-sandbox",
        f"--user-data-dir={scratch}/profile",
        "--no-pdf-header-footer",
        f"--print-to-pdf={PDF}",
        f"file://{page}",
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    for _ in range(120):
        if os.path.exists(PDF) and os.path.getsize(PDF) > 0:
            time.sleep(1)
            break
        time.sleep(0.5)

    chrome.terminate()
    shutil.rmtree(scratch, ignore_errors=True)

    if not os.path.exists(PDF):
        sys.exit("Chrome produced no pdf.")

    print(f"{PDF}  {os.path.getsize(PDF) // 1024} KB")


if __name__ == "__main__":
    print_to_pdf(render())
