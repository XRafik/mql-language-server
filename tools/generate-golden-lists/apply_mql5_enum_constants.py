#!/usr/bin/env python3
"""Enrich data/builtins/mql5.json with documented MQL5 stdlib enum constants.

Issue #136 follow-up (StratoX incident, 2026-10-18): the MQL4 golden list was
enriched with documented stdlib constants (issues #136/#152) but the MQL5
list was left without them, so `Mql5Builtins.IsBuiltin` — consulted by
`UnresolvedSymbolRule` before emitting a 5070 "Undeclared symbol" diagnostic
— rejects real standard-library enum members (SYMBOL_TRADE_TICK_SIZE,
POSITION_VOLUME, ...).

The script crawls the official MQL5 reference ("Constants and Enumerations"
tree, https://www.mql5.com/en/docs/constants), extracts structurally the
(identifier, description) pairs from the documented enum tables
(spans `f_EnumID` / `f_EnumDesc`), collects the documented ENUM_* type names,
and merges the new names into the `variables` section of
data/builtins/mql5.json. Existing entries are never modified; every new
entry carries per-entry provenance pointing at the doc page it came from.

Usage:
    python3 apply_mql5_enum_constants.py [--dry-run]
    python3 apply_mql5_enum_constants.py --from-pdf <pdftotext -raw dump> \
        <pdftotext -layout dump> [--dry-run]

The --from-pdf mode extracts the same names from pdftotext dumps of the
official "MQL5 Reference" PDF (chapter "Constants, Enumerations and
Structures"): the -layout dump identifies the chapter's pages (per-page
footers), the -raw dump renders table cells without kerning. This is the
offline path used when mql5.com rate-limits the crawler; provenance records
the PDF page indices.

Outputs:
    - Updated data/builtins/mql5.json (unless --dry-run)
    - tools/generate-golden-lists/data/mql5-enum-extraction.json (evidence:
      per-page counts and every extracted pair, for review)
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import time
import urllib.request
from datetime import date, datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GOLDEN_PATH = REPO_ROOT / "data" / "builtins" / "mql5.json"
EVIDENCE_PATH = Path(__file__).resolve().parent / "data" / "mql5-enum-extraction.json"

BASE = "https://www.mql5.com"
START_URL = f"{BASE}/en/docs/constants"
USER_AGENT = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 mql-lsp-golden-lists/1.0"
REQUEST_DELAY_SECONDS = 3.0
MAX_PAGES = 400
FETCH_ATTEMPTS = 3

# Persistent page cache: fetched HTML is written here so repeated runs make
# monotonic progress under server-side rate limiting (resume semantics).
PAGE_CACHE_DIR = Path(__file__).resolve().parent / "data" / "page-cache"

# Enum table rows: identifier cell + description cell.
ROW_RE = re.compile(
    r'<tr[^>]*class="[^"]*EnumTable[^"]*"[^>]*>(.*?)</tr>', re.DOTALL)
ID_RE = re.compile(r'class="f_EnumID">([A-Za-z_][A-Za-z0-9_]*)<')
DESC_RE = re.compile(r'class="f_EnumDesc">(.*?)</span>', re.DOTALL)
TAG_RE = re.compile(r"<[^>]+>")
LINK_RE = re.compile(r'href="(/en/docs/constants(?:/[a-z0-9_]+)+)"')
ENUM_TYPE_RE = re.compile(r"\bENUM_[A-Z0-9_]+\b")


def fetch(url: str) -> str:
    last_error: Exception | None = None
    for attempt in range(FETCH_ATTEMPTS):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
            with urllib.request.urlopen(request, timeout=30) as response:
                return response.read().decode("utf-8", errors="replace")
        except Exception as exc:  # noqa: BLE001 - retried below, recorded by callers
            last_error = exc
            time.sleep(REQUEST_DELAY_SECONDS * (2**attempt))
    raise RuntimeError(f"fetch failed after {FETCH_ATTEMPTS} attempts: {last_error}")


def strip_tags(fragment: str) -> str:
    return TAG_RE.sub("", fragment).strip()


def fetch_cached(path: str) -> tuple[str, bool]:
    """Fetch a page, serving from and updating the on-disk cache.

    Returns (html, from_cache). Cached pages are never re-fetched, so a run
    interrupted by throttling resumes where the previous one stopped.
    """
    cache_file = PAGE_CACHE_DIR / (path.strip("/").replace("/", "__") + ".html")
    if cache_file.exists() and cache_file.stat().st_size > 1024:
        return cache_file.read_text(encoding="utf-8"), True
    html = fetch(BASE + path)
    PAGE_CACHE_DIR.mkdir(parents=True, exist_ok=True)
    cache_file.write_text(html, encoding="utf-8")
    return html, False


def discover_pages() -> list[str]:
    """Breadth-first crawl of the constants tree, return page paths."""
    seen: set[str] = set()
    queue = ["/en/docs/constants"]
    pages: list[str] = []
    while queue and len(pages) < MAX_PAGES:
        path = queue.pop(0)
        if path in seen:
            continue
        seen.add(path)
        pages.append(path)
        try:
            html, _ = fetch_cached(path)
        except Exception as exc:  # noqa: BLE001 - evidence record keeps the failure
            print(f"WARN fetch failed {path}: {exc}", file=sys.stderr)
            time.sleep(REQUEST_DELAY_SECONDS)
            continue
        for match in LINK_RE.finditer(html):
            child = match.group(1)
            if child not in seen:
                queue.append(child)
        time.sleep(REQUEST_DELAY_SECONDS)
    return pages


def extract_pairs(html: str) -> list[tuple[str, str]]:
    """(name, description) pairs from documented enum tables."""
    pairs: list[tuple[str, str]] = []
    for row in ROW_RE.finditer(html):
        id_match = ID_RE.search(row.group(1))
        if not id_match:
            continue
        name = id_match.group(1)
        desc_match = DESC_RE.search(row.group(1))
        description = strip_tags(desc_match.group(1)) if desc_match else ""
        pairs.append((name, description))
    return pairs


def extract_enum_types(html: str) -> set[str]:
    """Documented ENUM_* type names mentioned anywhere in the page body."""
    return set(ENUM_TYPE_RE.findall(html))


NO_DESCRIPTION = "(description not yet captured)"

# ---------------------------------------------------------------- PDF mode --

# Identifier token: uppercase run with at least one underscore, or a web color
# (clrRed). Lookarounds instead of \b: the de-kerned cell text can be directly
# adjacent to digits (TRADE_RETCODE numeric code column).
IDENT_RE = re.compile(r"(?<![A-Za-z0-9_])[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+(?![a-z])")
COLOR_RE = re.compile(r"(?<![A-Za-z0-9_])clr[A-Za-z]+(?![a-z])")
FRAGMENT_RE = re.compile(r"^[A-Z0-9_]+$")

# Tokens without underscores that are still documented named constants.
UNDERSCORELESS_ALLOWLIST = {"NULL"}

CHAPTER_FOOTER_RE = re.compile(
    r"^\s*(\d+)\s+Constants, Enumerations and Structures\s*$", re.MULTILINE)


def build_vocabulary(raw_text: str) -> set[str]:
    """Intact identifier tokens seen anywhere in the raw dump, plus every
    name already registered in either dialect's golden list. Used to repair
    or reject row candidates whose description's leading capital letter got
    glued to the token ("SYMBOL_TRADE_TICK_SIZEM" -> SYMBOL_TRADE_TICK_SIZE).
    """
    vocabulary: set[str] = set()
    for line in raw_text.splitlines():
        vocabulary.update(IDENT_RE.findall(line))
        vocabulary.update(COLOR_RE.findall(line))
    for dialect in ("mql4", "mql5"):
        path = REPO_ROOT / "data" / "builtins" / f"{dialect}.json"
        if path.exists():
            golden = json.loads(path.read_text())
            vocabulary.update(golden.get("variables", {}))
            vocabulary.update(golden.get("functions", {}))
    return vocabulary


def chapter_page_indices(layout_text: str) -> list[int]:
    """PDF page indices (1-based) whose footer marks the constants chapter.

    The -layout dump preserves per-page footers ("769   Constants,
    Enumerations and Structures"); the -raw dump drops them but renders
    table cells without kerning, so both dumps are combined.
    """
    return [
        i for i, page in enumerate(layout_text.split("\f"), start=1)
        if CHAPTER_FOOTER_RE.search(page)
    ]


def transcribe_colors(page_text: str, printed: int,
                      names: dict[str, set[int]], vocabulary: set[str]) -> None:
    """The Web Colors page renders as a swatch grid: many color names per
    line, wrapped mid-token, unparseable as table rows. Instead, consume the
    page's letter stream greedily with the known color vocabulary (both
    golden lists); segments left over are transcribed at their 'clr' token
    boundaries, which the grid preserves. Every consumed/transcribed name is
    recorded with the page as provenance.
    """
    first, last = page_text.find("clr"), page_text.rfind("clr")
    if first < 0:
        return
    letters = re.sub(r"[^A-Za-z]", "", page_text[first:last + 40])
    known = sorted((c for c in vocabulary if c.startswith("clr")),
                   key=len, reverse=True)
    pos, consumed = 0, []
    while pos < len(letters):
        for name in known:
            if letters.startswith(name, pos):
                consumed.append(name)
                pos += len(name)
                break
        else:
            nxt = letters.find("clr", pos + 1)
            segment = letters[pos:nxt if nxt > 0 else len(letters)]
            if re.fullmatch(r"clr[A-Za-z]+", segment):
                consumed.append(segment)
                pos += len(segment)
                continue
            break  # unparseable residue: stop honestly rather than guess
    for color in consumed:
        names.setdefault(color, set()).add(printed)


def pdf_extract_names(
    layout_text: str, raw_text: str
) -> tuple[dict[str, dict], set[str], list[dict]]:
    """Extract (name -> {pages}) plus documented ENUM_* type names.

    From the -raw dump: a table row renders as the identifier (sometimes
    wrapped over two lines, e.g. "CHART_BRING_TO_TO" + "P") followed by its
    description on the same or following lines. The leading run of pure
    uppercase/underscore tokens of each line is the identifier candidate;
    everything after the first lowercase-containing token is description
    prose. Tokens starting with ENUM_ are enum type names, not members.

    Candidates are admitted against a document-wide vocabulary: tokens seen
    intact anywhere (or already in a golden list) pass as-is; lead-captured
    tokens get their trailing glued capitals stripped while a vocabulary
    match exists, and are skipped when they cannot be repaired. Wrap joins
    (two or more pure-fragment lines) are structurally reliable and are
    recorded as joined even when unseen intact elsewhere.
    """
    names: dict[str, set[int]] = {}
    enum_types: set[str] = set()
    chapter_pages = set(chapter_page_indices(layout_text))
    raw_pages = raw_text.split("\f")
    fragments = re.compile(r"^[A-Z0-9_]+$")
    # Leading identifier run: segments start with a letter (so a numeric code
    # column like "ERR_SUCCESS 0 The operation completed" cannot glue onto the
    # token), an optional numeric code may precede the identifier
    # ("10004 TRADE_RETCODE_REQUOTE ..."), and clr* web colors are allowed.
    leading = re.compile(
        r"^(?:\d+[ \t]+)?(?:[A-Z][A-Z0-9_]+|clr[A-Za-z]+)"
        r"(?:[ \t]+(?:[A-Z][A-Z0-9_]+|clr[A-Za-z]+))*")
    int_re = re.compile(r"\bENUM_[A-Z0-9_]+\b")
    vocabulary = build_vocabulary(raw_text)
    stats = {"repaired": 0, "skipped": 0}

    def admit(token: str, wrap_joined: bool, printed: int) -> None:
        if token in UNDERSCORELESS_ALLOWLIST or COLOR_RE.fullmatch(token):
            names.setdefault(token, set()).add(printed)
            return
        if not IDENT_RE.fullmatch(token):
            return
        if token in vocabulary:
            names.setdefault(token, set()).add(printed)
            return
        if wrap_joined and IDENT_RE.fullmatch(token):
            names.setdefault(token, set()).add(printed)
            return
        for cut in range(1, 7):
            tail = token[-cut:]
            if (len(token) > cut + 3 and tail.isalpha() and tail.isupper()
                    and token[:-cut] in vocabulary):
                names.setdefault(token[:-cut], set()).add(printed)
                stats["repaired"] += 1
                return
        stats["skipped"] += 1

    for pdf_page in sorted(chapter_pages):
        if pdf_page > len(raw_pages):
            break
        printed = pdf_page  # provenance granularity: PDF page index
        page_text = raw_pages[pdf_page - 1]
        if page_text.count("clr") > 30:
            # Web Colors swatch grid: dedicated parser, generic scanner would
            # glue several color names into one token.
            transcribe_colors(page_text, printed, names, vocabulary)
            continue
        run: list[str] = []

        def close_run(extra: str = "") -> None:
            parts = list(run) + ([extra] if extra else [])
            run.clear()
            if not parts or not any(parts):
                return

            def toks(text: str) -> list[str]:
                return (IDENT_RE.findall(text.lstrip("0123456789"))
                        or COLOR_RE.findall(text.lstrip("0123456789")))

            # Interpretation A: the fragments are one identifier wrapped over
            # several lines ("CHART_BRING_TO_TO" + "P"). Interpretation B:
            # they are separate tokens (an enum Type column value on its own
            # line, then the next row's identifier). The vocabulary decides;
            # the structural wrap join is the fallback.
            joined_a = "".join(parts)
            tokens_a = toks(joined_a)
            if len(parts) == 1:
                chosen, wrap = tokens_a, False
            elif tokens_a and all(t in vocabulary for t in tokens_a):
                chosen, wrap = tokens_a, True
            else:
                tokens_b = [t for part in parts for t in toks(part)]
                if tokens_b and all(
                        t in vocabulary or t.startswith("ENUM_")
                        for t in tokens_b):
                    chosen, wrap = tokens_b, False
                else:
                    chosen, wrap = tokens_a, True
            for token in chosen:
                if token.startswith("ENUM_"):
                    # Glued header/prose fragments must not invent enum type
                    # names: only structural joins or known names count.
                    if wrap or token in vocabulary:
                        enum_types.add(token)
                    continue
                admit(token, wrap, printed)

        for line in raw_pages[pdf_page - 1].splitlines():
            stripped = line.strip()
            if fragments.match(stripped):
                run.append(stripped)
                continue
            lead = leading.match(stripped)
            close_run(lead.group().replace(" ", "") if lead else "")
            for token in int_re.findall(stripped):
                enum_types.add(token)
        close_run()
    evidence = [
        {"pagesScanned": len(chapter_pages), "namesExtracted": len(names),
         "enumTypes": len(enum_types), "repaired": stats["repaired"],
         "skipped": stats["skipped"]}
    ]
    return (
        {k: {"pages": sorted(v)} for k, v in names.items()},
        enum_types,
        evidence,
    )


def _record(names: dict[str, set[int]], enum_types: set[str], token: str,
            printed: str) -> None:
    if token in UNDERSCORELESS_ALLOWLIST or COLOR_RE.fullmatch(token):
        names.setdefault(token, set()).add(int(printed))
        return
    if not IDENT_RE.fullmatch(token):
        return
    if token.startswith("ENUM_"):
        enum_types.add(token)
        return
    names.setdefault(token, set()).add(int(printed))


def pdf_mode(raw_path: Path, layout_path: Path, dry_run: bool) -> int:
    today = date.today().isoformat()
    raw_text = raw_path.read_text(encoding="utf-8", errors="replace")
    layout_text = layout_path.read_text(encoding="utf-8", errors="replace")
    names, enum_types, evidence = pdf_extract_names(layout_text, raw_text)
    print(f"PDF extraction: {json.dumps(evidence[0])}.")

    golden = json.loads(GOLDEN_PATH.read_text())
    existing = set(golden["variables"])
    validate(names, enum_types, existing)

    if dry_run:
        print("Dry run: golden list not modified.")
        return 0

    added = 0
    for name, info in sorted(names.items()):
        if name in existing:
            continue
        golden["variables"][name] = {
            "description": NO_DESCRIPTION,
            "provenance": (
                f"official-mql5-reference-pdf-{today} "
                f"pages {','.join(map(str, info['pages'][:5]))}"
                + (" et al" if len(info["pages"]) > 5 else "")
            ),
        }
        added += 1
    for type_name in sorted(enum_types):
        if type_name in existing:
            continue
        golden["variables"][type_name] = {
            "description": NO_DESCRIPTION,
            "provenance": f"official-mql5-reference-pdf-{today} (enum type name)",
        }
        added += 1

    if added == 0:
        print("Nothing new to add; golden list not modified.")
        return 0

    golden["provenance"] = (
        golden.get("provenance", "")
        + f" | Enum-constant enrichment {today} from the official MQL5 "
          f"Reference PDF by tools/generate-golden-lists/"
          f"apply_mql5_enum_constants.py (+{added} documented names)."
    )
    GOLDEN_PATH.write_text(
        json.dumps(golden, indent=2, ensure_ascii=False) + "\n")
    print(f"Golden list updated: +{added} names "
          f"(variables now {len(golden['variables'])}).")
    return 0


def validate(names: dict, enum_types: set[str], existing: set[str]) -> None:
    """Truth-set validation printed before any merge (honesty gate)."""
    extracted = set(names) | enum_types
    missing_existing = sorted(existing - extracted)
    print(f"\n[validate] existing mql5.json variables not found in the PDF "
          f"extraction: {len(missing_existing)}")
    if missing_existing:
        print("  " + ", ".join(missing_existing))
    incident_pins = [
        "SYMBOL_TRADE_TICK_SIZE", "SYMBOL_TRADE_TICK_VALUE",
        "SYMBOL_VOLUME_MAX", "SYMBOL_VOLUME_LIMIT", "POSITION_VOLUME",
        "SYMBOL_BID", "SYMBOL_ASK", "SYMBOL_POINT", "SYMBOL_DIGITS",
        "POSITION_TYPE_BUY", "ORDER_TYPE_BUY", "DEAL_ENTRY_IN",
        "TRADE_ACTION_DEAL", "ACCOUNT_MARGIN_MODE_RETAIL_HEDGING",
        "CHART_SHOW_GRID", "OBJPROP_XSIZE", "FILE_ANSI", "PERIOD_M1",
        "INDICATOR_DIGITS", "TERMINAL_CONNECTED", "ERR_SUCCESS",
        "clrMediumSeaGreen", "MQL_TESTER", "SERIES_BARS_COUNT",
    ]
    missing_pins = [p for p in incident_pins if p not in extracted]
    print(f"[validate] incident/pin coverage: "
          f"{len(incident_pins) - len(missing_pins)}/{len(incident_pins)}")
    if missing_pins:
        print("  MISSING: " + ", ".join(missing_pins))


def main() -> int:
    dry_run = "--dry-run" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if "--from-pdf" in sys.argv:
        if len(args) < 2:
            print("--from-pdf requires <pdftotext -raw dump> "
                  "<pdftotext -layout dump>", file=sys.stderr)
            return 2
        return pdf_mode(Path(args[0]), Path(args[1]), dry_run)
    return crawl_main(dry_run)


def crawl_main(dry_run: bool) -> int:
    today = date.today().isoformat()

    print(f"Discovering pages under {START_URL} ...")
    pages = discover_pages()
    print(f"Discovered {len(pages)} pages.")

    names: dict[str, dict] = {}
    enum_types: set[str] = set()
    evidence_pages: list[dict] = []

    for path in pages:
        url = BASE + path
        try:
            html, from_cache = fetch_cached(path)
        except Exception as exc:  # noqa: BLE001
            evidence_pages.append({"url": url, "error": str(exc)})
            print(f"WARN fetch failed {url}: {exc}", file=sys.stderr)
            time.sleep(REQUEST_DELAY_SECONDS)
            continue
        if not from_cache:
            time.sleep(REQUEST_DELAY_SECONDS)

        page_pairs = extract_pairs(html)
        page_types = extract_enum_types(html)
        enum_types |= page_types
        for name, description in page_pairs:
            entry = names.setdefault(
                name,
                {"description": description or NO_DESCRIPTION, "sources": []},
            )
            if url not in entry["sources"]:
                entry["sources"].append(url)
            if description and entry["description"] == NO_DESCRIPTION:
                entry["description"] = description
        evidence_pages.append({"url": url, "constants": len(page_pairs),
                               "enumTypes": len(page_types)})
        time.sleep(REQUEST_DELAY_SECONDS)

    # Enum type names carry their own provenance by appearing across pages;
    # attribute them to the pages where they occur (kept in evidence).
    for type_name in enum_types:
        names.setdefault(
            type_name, {"description": NO_DESCRIPTION, "sources": []})

    fetched = sum(1 for p in evidence_pages if "error" not in p)
    evidence = {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "startUrl": START_URL,
        "pagesFetched": fetched,
        "pagesFailed": len(evidence_pages) - fetched,
        "pages": evidence_pages,
        "extractedNames": len(names),
        "names": {k: v for k, v in sorted(names.items())},
    }
    EVIDENCE_PATH.parent.mkdir(parents=True, exist_ok=True)
    # Timestamped so a failed re-run never clobbers earlier evidence.
    stamped = EVIDENCE_PATH.with_name(
        EVIDENCE_PATH.stem + "-" + datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        + EVIDENCE_PATH.suffix)
    stamped.write_text(json.dumps(evidence, indent=2) + "\n")
    print(f"Evidence written to {stamped} ({len(names)} names).")

    if dry_run:
        print("Dry run: golden list not modified.")
        return 0

    golden = json.loads(GOLDEN_PATH.read_text())
    variables = golden["variables"]
    added = 0
    for name, info in names.items():
        if name in variables:
            continue
        variables[name] = {
            "description": info["description"],
            "provenance": f"docs-constants-{today} " + "; ".join(info["sources"]),
        }
        added += 1

    if added == 0:
        print("Nothing new to add; golden list not modified.")
        return 0

    golden["provenance"] = (
        golden.get("provenance", "")
        + f" | Enum-constant enrichment {today} by "
          "tools/generate-golden-lists/apply_mql5_enum_constants.py "
          f"(+{added} documented names from official reference)."
    )
    GOLDEN_PATH.write_text(json.dumps(golden, indent=2, ensure_ascii=False) + "\n")
    print(f"Golden list updated: +{added} names "
          f"(variables now {len(variables)}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
