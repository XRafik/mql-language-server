#!/usr/bin/env python3
"""Enrich data/builtins/mql5.json with real descriptions (issue #166).

Baseline (main @ 15458f0): 1,644 of 2,003 variables carry the placeholder
"(description not yet captured)" (PDF-enriched enum constants, ENUM_* type
names, 133 web colors) and all 816 functions have no description field.

This tool fills the `description` field ONLY — never names or signatures
(#136 contract: the golden list is the source of truth; registries are
projections enforced by RegistryGoldenListParityTests).

Sources (all offline, from the official MQL5 Reference PDF dumps in
tools/generate-golden-lists/data/pdf/):

- Constants: (identifier, description) pairs parsed from the constants
  chapter of the pdftotext -raw dump, anchored on the chapter's table
  headers ("Identifier Description Type", "Constant Code Description",
  "Identifier Value Description", "ID Description", "Variable Value").
  Identifiers are clean in the -raw dump (the -layout dump kerns them,
  e.g. "SYM BOL _T RADE_TICK_S IZE"); identifiers wrapped mid-name across
  lines are repaired against the golden-list vocabulary (the same
  vocabulary-voted repair apply_mql5_enum_constants.py uses). Narrow-column
  description fragments are reassembled with a complete-word vocabulary
  extracted from the markitdown dump's prose.
- Function pages: prose between the page heading (bare identifier line)
  and the captured signature start, same page structure as
  apply_mql5_functions.py.
- ENUM_* type names: prose directly under the enum's standalone heading
  line; fallback to the page's section title when the heading opens the
  page's section.
- Web colors: synthetic deterministic descriptions
  ("Web color 0xRRGGBB (Name)") derived from src/Color/MqlColorRegistry.cs
  (the PDF swatch grid has no description column; user decision
  2026-10-19). clrNONE (0xFFFFFFFF, no swatch) maps to 0xFFFFFF.

The run is idempotent: entries whose description is already real are never
touched, so re-running makes no further changes. The full extraction is
written to tools/generate-golden-lists/data/mql5-descriptions.json for
review, including every unrecoverable entry with its page evidence.

Usage:
    python3 apply_mql5_descriptions.py <raw.txt> <layout.txt> <markitdown.md> [--dry-run]
"""

from __future__ import annotations

import json
import re
import sys
from datetime import date, datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GOLDEN_PATH = REPO_ROOT / "data" / "builtins" / "mql5.json"
COLOR_REGISTRY_PATH = REPO_ROOT / "src" / "Color" / "MqlColorRegistry.cs"
MAPPING_PATH = Path(__file__).resolve().parent / "data" / "mql5-descriptions.json"

PLACEHOLDER = "(description not yet captured)"
DESC_MARKER_PDF = "descriptions-official-mql5-reference-pdf-{today}"
DESC_MARKER_COLOR = "descriptions-synthetic-web-colors"

# Identifier token: uppercase run with underscores (see enum-constants tool).
IDENT_RE = re.compile(r"(?<![A-Za-z0-9_])[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+(?![a-z])")
COLOR_RE = re.compile(r"(?<![A-Za-z0-9_])clr[A-Za-z]+(?![a-z])")
CHAPTER_FOOTER_RE = re.compile(
    r"^\s*(\d+)\s+Constants, Enumerations and Structures\s*$", re.MULTILINE)
COLOR_HEX_RE = re.compile(
    r'\["(clr[A-Za-z]+)"\]\s*=\s*\((0x[0-9A-Fa-f]{2}),\s*'
    r"(0x[0-9A-Fa-f]{2}),\s*(0x[0-9A-Fa-f]{2})\)")

TYPE_TOKENS = {
    "bool", "char", "uchar", "short", "ushort", "int", "uint", "color",
    "long", "ulong", "datetime", "float", "double", "string", "void",
    "enum", "long&", "int&", "double&", "string&", "void*",
}
HEADER_FIRST = {"Identifier", "ID", "Constant", "Code", "Variable",
                "Mode", "Flag", "Property", "Value"}
NUMERIC_LAST_WORDS = {"Code", "Value"}

# Words that always stand alone even though a fragment may look splittable.
STOPWORDS = {
    "a", "an", "the", "of", "in", "on", "is", "are", "was", "to", "for",
    "and", "or", "as", "at", "by", "be", "it", "its", "if", "no", "not",
    "we", "so", "up", "do", "has", "had", "can", "may", "one", "two",
    "per", "via", "off", "out", "use", "used", "when", "with", "from",
    "an", "as", "at", "be", "by", "do", "go", "if", "in", "is", "it",
    "my", "no", "on", "or", "so", "to", "up", "us", "we",
}
CODE_CHARS = set("={};")


def load_golden_names() -> tuple[set[str], set[str]]:
    """Golden-list variable and function names from both dialects."""
    variables: set[str] = set()
    functions: set[str] = set()
    for dialect in ("mql4", "mql5"):
        path = REPO_ROOT / "data" / "builtins" / f"{dialect}.json"
        if path.exists():
            golden = json.loads(path.read_text())
            variables.update(golden.get("variables", {}))
            functions.update(golden.get("functions", {}))
    return variables, functions


def build_complete_words(markdown_text: str) -> set[str]:
    """Complete English words, extracted from full-width prose lines of the
    markitdown dump. Used to decide whether a narrow-column fragment pair
    must be joined without a space ("Calculat"+"ed" -> "Calculated") or
    with one ("for"+"a")."""
    words: set[str] = set(STOPWORDS)
    for line in markdown_text.splitlines():
        if len(line) < 60:
            continue
        for token in re.findall(r"[A-Za-z]+", line):
            # Short fragments ("e", "ts") are narrow-column debris, never
            # reliable complete words; 2-letter words are listed explicitly.
            if token.islower() and len(token) >= 3:
                words.add(token)
    return words


# --------------------------------------------------------------- assembly --

ALLCAPS_RE = re.compile(r"^_?[A-Z][A-Z0-9_]*$")


def assemble(words: list[str], vocabulary: set[str],
             complete: set[str]) -> str:
    """Reassemble narrow-column fragments into readable prose.

    Two rules:
    - Consecutive ALLCAPS fragments of a wrapped identifier reference are
      joined without spaces ("SYMBOL"+"_TRADE"+"..." -> the full constant),
      while back-to-back complete references stay separate.
    - Two lowercase fragments join without a space only when both sides
      are incomplete words ("Calculat"+"ed"); otherwise a space is kept.
    """
    out: list[str] = []
    ident_buf: list[str] = []

    def flush_ident() -> None:
        if ident_buf:
            out.append("".join(ident_buf))
            ident_buf.clear()

    for word in words:
        stripped = word.strip()
        if not stripped:
            continue
        if ALLCAPS_RE.fullmatch(stripped) and ("_" in stripped
                                               or ident_buf
                                               or stripped in vocabulary):
            if (ident_buf and not stripped.startswith("_")
                    and not ident_buf[-1].endswith("_")
                    and len(ident_buf) == 1
                    and ident_buf[0] in vocabulary
                    and stripped in vocabulary):
                flush_ident()  # two complete references back to back
            ident_buf.append(stripped)
            continue
        flush_ident()
        if not out:
            out.append(stripped)
            continue
        prev = out[-1]
        prev_tail = prev.split()[-1] if " " in prev else prev
        joinable = (
            prev_tail[-1:].isdigit() and stripped[0].isdigit()
        ) or (
            re.match(r"[a-z]", stripped)
            and prev_tail[-1:].isalpha()
            and prev_tail.lower() not in complete
            and stripped.lower() not in complete
            and not prev_tail.endswith((".", ",", ";", ":", ")"))
        )
        if joinable:
            out[-1] = prev + stripped
        else:
            out.append(stripped)
    flush_ident()
    return " ".join(out)


def is_useful(text: str) -> bool:
    """Reject numeric-only or single-token garbage descriptions."""
    letters = sum(ch.isalpha() for ch in text)
    return len(text) >= 3 and letters >= 3


def split_type_glue(text: str) -> str:
    """A narrow description column can glue its last word to the Type
    column ("Bid price levelcolor"); when the trailing word ends with a
    primitive type name, split it back off ("level" + "color")."""
    words = text.split()
    while len(words) >= 2:
        last = words[-1]
        for t in sorted(TYPE_TOKENS, key=len, reverse=True):
            if last.lower().endswith(t) and len(last) > len(t) + 2 \
                    and last[:-len(t)].isalpha():
                words[-1] = last[:-len(t)]
                break
        else:
            break
    return " ".join(words)


def strip_access_modifiers(words: list[str]) -> list[str]:
    """Property tables trail the type with an access column ("int r/o")."""
    while words and words[-1].lower() in {"r/o", "r/w"}:
        words.pop()
    return words


def cleanup_text(text: str) -> str:
    """Cosmetic cleanup: the Type column's "enum" keyword glues into the
    description ("... in the chart enum ENUM_CHART_VOLUME_MODE"), and the
    Formula column of the symbol-calc tables bleeds into the description
    column ("... for Forex Margin: Lots * Contract_Size / ...")."""
    text = re.sub(r"\benum\s+(ENUM_[A-Z0-9_]+)", r"\1", text)
    cut = re.search(r"\s(Margin|Profit|Commission|Swap|Stop-out):\s", text)
    if cut:
        text = text[:cut.start()]
    return text.strip()


def strip_trailing_values(text: str) -> str:
    """Value-column residue after the description ("... open charts in the
    terminal 100"): drop trailing pure numbers / hex constants."""
    words = text.split()
    while words and re.fullmatch(r"-?\d+(\.\d+)?|0x[0-9A-Fa-f]+", words[-1]):
        words.pop()
    return " ".join(words)


# ----------------------------------------------------------- table parsing --

def is_footer(line: str) -> bool:
    return (line.startswith("©")
            or line.rstrip().endswith("MetaQuotes Ltd."))


def classify_header(line: str) -> str | None:
    """Table kind from the header line: 'numeric' (a code/value column
    before the description), 'typed' (description + type column), 'plain'
    (identifier + description). None when the line is not a header."""
    stripped = line.strip()
    tokens = stripped.split()
    if stripped == "Variable Value":
        return "plain"
    if not (2 <= len(tokens) <= 8 and len(stripped) <= 60):
        return None
    if not any(t in HEADER_FIRST for t in tokens[:2]):
        return None
    desc_idx = next((i for i, t in enumerate(tokens)
                     if t.lower() in ("des", "desc", "descrip")
                     or t.lower().startswith("descrip")), None)
    if desc_idx is None:
        return None
    if any(t in NUMERIC_LAST_WORDS for t in tokens[:desc_idx]):
        return "numeric"
    if any(t.lower() == "type" for t in tokens[desc_idx + 1:]):
        return "typed"
    return "plain"


def is_bare_type_header(line: str) -> bool:
    """Wrapped headers split the Type column onto its own line
    ("ID Description of a" / "statistic parameter" / "Type")."""
    return bool(re.fullmatch(r"(Property )?[Tt]ype(\s+\w{1,10}){0,2}",
                             line.strip())) and len(line.strip()) <= 30


def numeric_row(line: str, names: set[str]) -> tuple[str, str] | None:
    """Row of a code/value table: `IDENT <num> desc...` or
    `<num> IDENT desc...` (retcode tables)."""
    parts = line.split()
    if len(parts) < 3:
        return None
    if parts[0] in names and re.fullmatch(r"0x[0-9A-Fa-f]+|\d+", parts[1]):
        return parts[0], " ".join(parts[2:])
    if re.fullmatch(r"\d+", parts[0]) and parts[1] in names:
        return parts[1], " ".join(parts[2:])
    return None


def row_start(line: str, names: set[str]) -> str | None:
    """Identifier at line start for identifier/description tables."""
    parts = line.split()
    if not parts or parts[0] not in names:
        return None
    rest = line[len(parts[0]):]
    if not rest.startswith(" "):
        return None
    if any(ch in rest for ch in CODE_CHARS):
        return None  # code line like "SYMBOL_TRADE_TICK_SIZE = %f ..."
    return parts[0]


def parse_tables(raw_text: str, chapter_pages: list[int],
                 variables: set[str], vocabulary: set[str],
                 complete: set[str],
                 wanted: set[str]) -> tuple[dict[str, dict], list[dict]]:
    """(identifier -> {description, pages}) from reference tables.

    Only `wanted` names are recorded. First extraction wins so genuine
    chapter tables cannot be overridden by later look-alike rows. Row
    boundaries anchor on any golden-list variable name; descriptions are
    captured only for wanted rows.
    """
    pages = raw_text.split("\f")
    mapping: dict[str, dict] = {}
    evidence: list[dict] = []
    chapter_set = set(chapter_pages)

    for pdf_page, page in enumerate(pages, start=1):
        if pdf_page not in chapter_set and not any(
                row_start(l.strip(), wanted) for l in page.splitlines()):
            continue
        lines = [l.rstrip() for l in page.splitlines()]
        mode: str | None = None
        typed_hint = False
        row_ident: str | None = None
        row_words: list[str] = []
        row_page = pdf_page
        found_here = 0
        aliases: list[str] = []

        def close_aliases(text: str) -> None:
            nonlocal aliases
            for alias in aliases:
                if alias in wanted and alias not in mapping and is_useful(text):
                    mapping[alias] = {"description": text,
                                      "pages": [row_page]}
            aliases = []

        def close_row() -> None:
            nonlocal row_ident, row_words
            if row_ident is not None:
                words = list(row_words)
                # Narrow property tables trail the description with the
                # Type column ("... maximized bool r/o") in every mode.
                words = strip_access_modifiers(words)
                if words and words[-1].lower() in TYPE_TOKENS:
                    words = strip_access_modifiers(words[:-1])
                text = assemble(words, vocabulary, complete).strip()
                text = cleanup_text(strip_trailing_values(split_type_glue(text)))
                text = text.strip("–- ").strip()
                if is_useful(text):
                    if row_ident not in mapping:
                        mapping[row_ident] = {"description": text,
                                              "pages": [row_page]}
                elif words == [w for w in words] and len(words) <= 1 \
                        and row_ident not in wanted:
                    pass  # spurious heading row: dropped
                row_ident, row_words = None, []

        i = 0
        while i < len(lines):
            line = lines[i].strip()
            i += 1
            if not line or is_footer(line) or re.fullmatch(r"\d{1,5}", line):
                close_row()
                close_aliases("")
                continue
            kind = classify_header(line)
            if kind:
                close_row()
                mode, typed_hint = kind, kind == "typed"
                continue
            if mode is None:
                # Tables continued from the previous page repeat no header:
                # enter plain mode when this line and a following line both
                # start with golden-list names ("FILE_IS_COMMON The file
                # is ...").
                bullet = next((b for b in ("\u00b7 ", "\u2022 ")
                               if line.startswith(b)), "")
                candidate = line[len(bullet):] if bullet else line
                if (row_start(candidate, wanted)
                        and any(row_start(n.strip(), variables)
                                for n in lines[i:i + 3]
                                if n.strip() and not is_footer(n.strip()))):
                    mode, typed_hint = "plain", False
                else:
                    continue
            if row_ident is None and is_bare_type_header(line):
                typed_hint = True
                continue
            if any(ch in line for ch in CODE_CHARS):
                close_row()  # code sample or pseudo-table line
                continue
            alias_match = re.fullmatch(r"_?[A-Z][A-Z0-9_]*,?", line)
            if alias_match and (aliases or line.endswith(",")) \
                    and line.rstrip(",") in variables:
                aliases.append(line.rstrip(","))
                continue
            if aliases and mode == "numeric":
                value_row = re.fullmatch(r"(0x[0-9A-Fa-f]+|\d+)\s+(.+)", line)
                if value_row:
                    close_aliases(assemble(value_row.group(2).split(),
                                           vocabulary, complete))
                    found_here += len(aliases)
                    continue
            numeric = numeric_row(line, variables) \
                if mode == "numeric" else None
            if numeric:
                close_row()
                close_aliases("")
                ident, desc_text = numeric
                if ident in wanted and ident not in mapping:
                    row_ident, row_words = ident, desc_text.split()
                    row_page = pdf_page
                    found_here += 1
                else:
                    row_ident, row_words = None, []
                continue
            bullet = next((b for b in ("\u00b7 ", "\u2022 ")
                           if line.startswith(b)), "")
            bullet_line = line[len(bullet):]
            start = row_start(bullet_line, variables)
            if start is not None and mode in ("typed", "plain"):
                close_row()
                close_aliases("")
                if start in wanted and start not in mapping:
                    row_ident = start
                    row_words = bullet_line[len(start):].split()
                    row_page = pdf_page
                    found_here += 1
                else:
                    # Row boundary for a non-wanted name: closes the open
                    # row and is otherwise discarded.
                    row_ident, row_words = None, []
                continue
            if mode in ("typed", "plain"):
                # Wrapped identifier spanning lines: a partial ALLCAPS
                # token whose continuation on a following line completes a
                # golden-list name ("CHART_COLOR_CAND" / "LE_BULL"). This
                # also closes a still-open row before the new one starts.
                repaired = repair_wrapped_identifier(line, lines, i, wanted)
                if repaired:
                    ident, tail_idx = repaired
                    close_row()
                    tail_line = lines[tail_idx].strip()
                    row_ident = ident
                    row_words = tail_line.split()[1:]
                    row_page = pdf_page
                    found_here += 1
                    i = tail_idx + 1
                    continue
            if row_ident is None:
                continue
            # Continuation fragment of the open row. A wrapped-identifier
            # tail ("CHART_SHOW_TRADE" / "_LEVELS") upgrades the open row.
            if len(row_words) <= 1:
                head = line.split()[0]
                merged = row_ident + head
                if ALLCAPS_RE.fullmatch(head) and merged in wanted:
                    row_ident = merged
                    row_words = line[len(head):].split()
                    found_here += 1
                    continue
            if len(line) > 62 and not line.split()[0].isdigit():
                close_row()  # full-width prose: table region ended
                close_aliases("")
                mode = None
                continue
            row_words.extend(line.split())
        close_row()
        if found_here:
            evidence.append({"page": pdf_page, "rows": found_here})
    return mapping, evidence


def repair_wrapped_identifier(line: str, lines: list[str], next_idx: int,
                              wanted: set[str]) -> tuple[str, int] | None:
    """If `line` starts a wrapped identifier, return (fullIdentifier, index
    of the line carrying the identifier tail) - the tail line's remainder
    is the row's description. None when this is not a wrapped-identifier
    row."""
    parts = line.split()
    if not parts:
        return None
    tok = parts[0]
    if "_" not in tok or not ALLCAPS_RE.fullmatch(tok):
        return None
    merged = tok
    idx = next_idx
    for _ in range(3):
        if idx >= len(lines):
            return None
        nxt = lines[idx].strip().split()
        if not nxt or not ALLCAPS_RE.fullmatch(nxt[0]):
            return None
        if merged + nxt[0] in wanted:
            merged += nxt[0]
            if merged in wanted:
                return merged, idx
            idx += 1
        else:
            return None
    return None


# ------------------------------------------------------------ enum types --

def parse_enum_types(raw_text: str, enum_names: list[str],
                     variables: set[str], vocabulary: set[str],
                     complete: set[str]) -> tuple[dict[str, dict], list[dict]]:
    """Descriptions for ENUM_* type names from their documentation section:
    prose directly under the enum's standalone heading line; fallback to
    the page's section title when the heading opens the page's section."""
    pages = raw_text.split("\f")
    heading_pages: dict[str, list[int]] = {n: [] for n in enum_names}
    for idx, page in enumerate(pages, start=1):
        for line in page.splitlines():
            stripped = line.strip()
            if stripped in heading_pages:
                heading_pages[stripped].append(idx)

    mapping: dict[str, dict] = {}
    unrecoverable: list[dict] = []
    for name in enum_names:
        best: dict | None = None
        for pdf_page in heading_pages[name]:
            page_lines = [l.strip() for l in pages[pdf_page - 1].splitlines()
                          if l.strip()]
            hidx = page_lines.index(name)
            # Prose between the heading and the next structural element.
            prose: list[str] = []
            for line in page_lines[hidx + 1:]:
                if (is_footer(line) or re.fullmatch(r"\d{1,5}", line)
                        or classify_header(line)
                        or is_bare_type_header(line)
                        or row_start(line, variables)
                        or any(ch in line for ch in "={};")):
                    break
                prose.extend(line.split())
                if len(prose) > 60:
                    break
            text = assemble(prose, vocabulary, complete).strip()
            if is_useful(text):
                best = {"description": text, "pages": [pdf_page]}
                break
            # Fallback: the section title of this page or of the nearest
            # preceding page (first content line after the running header /
            # footer / page number). Sections in the reference open at a
            # page top, and enum tables follow their section prose.
            # Fallback: the section title of this page or of the nearest
            # preceding page (first content line after the running header /
            # footer / page number). A title only counts when the enum
            # heading actually belongs to that section: on the same page
            # the heading must sit within a few lines of the section
            # opening; on an earlier page the heading must open THIS page
            # (i.e. the section continued onto the heading's page).
            title = None
            hidx_pos = page_lines.index(name)
            for scan in range(pdf_page, max(pdf_page - 4, 0), -1):
                candidates: list[str] = []
                for line in [l.strip() for l in pages[scan - 1].splitlines()
                             if l.strip()]:
                    if (is_footer(line) or re.fullmatch(r"\d{1,5}", line)
                            or "Enumerationsand Structures" in line
                            or line.startswith("©")
                            or classify_header(line)
                            or is_bare_type_header(line)
                            or ALLCAPS_RE.fullmatch(line.replace(" ", ""))
                            or len(line) < 10):
                        continue
                    if scan == pdf_page and line == name:
                        break  # this page's first content line IS the enum
                    candidates.append(line)
                if not candidates:
                    continue
                candidate = candidates[0]
                # A row-anchored line ("ACCOUNT_MARGIN_MODE Margin") is
                # table debris, never a section title.
                if row_start(candidate, variables):
                    continue
                if scan == pdf_page:
                    # Section titles open a page; enum tables follow their
                    # section prose, sometimes across a full table.
                    heading_ok = (page_lines.index(candidate) < hidx_pos
                                  and hidx_pos - page_lines.index(candidate)
                                  <= 45)
                else:
                    heading_ok = hidx_pos <= 3
                if heading_ok:
                    title = candidate
                break
            if title and title != name and len(title) < 60:
                best = {"description": title, "pages": [pdf_page],
                        "sectionTitle": True}
                break
        if best:
            mapping[name] = best
        else:
            unrecoverable.append({
                "name": name,
                "reason": ("no standalone heading line in the reference dump"
                           if not heading_pages[name]
                           else "no prose or section title under the heading"),
                "pages": heading_pages[name][:5],
            })

    # Inheritance pass: sub-headings mid-table ("For the function
    # OrderGetDouble()" / ENUM_ORDER_PROPERTY_DOUBLE) belong to the same
    # section as their recovered sibling enums. Inherit the nearest
    # preceding recovered enum's description.
    ordered = sorted(
        ((name, page, page_lines_index(pages, page, name))
         for name in enum_names
         for page in heading_pages[name][:1]),
        key=lambda t: (t[1], t[2] if t[2] is not None else 10 ** 6))
    last: tuple[str, str] | None = None
    for name, page, _line in ordered:
        if name in mapping:
            entry = mapping[name]
            last = (entry["description"], name)
            continue
        if last is not None:
            mapping[name] = {"description": last[0],
                             "inheritedFrom": last[1],
                             "pages": heading_pages[name][:1]}
    for entry in unrecoverable:
        if entry["name"] in mapping:
            unrecoverable.remove(entry)
    return mapping, unrecoverable

def page_lines_index(pages: list[str], page: int, name: str) -> int | None:
    lines = [l.strip() for l in pages[page - 1].splitlines() if l.strip()]
    return lines.index(name) if name in lines else None


# -------------------------------------------------------------- functions --

RETURN_TYPE_RE = re.compile(
    r"\b(bool|int|long|ulong|uint|double|string|void|datetime|color|char|"
    r"uchar|short|ushort|float|matrix|vector|ENUM_\w+|Mql\w+|void\*|"
    r"object\*|T\*?)[ \t]+")


def parse_function_descriptions(raw_text: str, function_names: list[str]) \
        -> tuple[dict[str, dict], list[dict]]:
    """Prose between the page heading (bare identifier line) and the first
    captured signature start, for each documented function page."""
    pages = raw_text.split("\f")
    mapping: dict[str, dict] = {}
    unrecoverable: list[dict] = []
    name_set = set(function_names)
    heading_pages: dict[str, list[int]] = {n: [] for n in function_names}
    for idx, page in enumerate(pages, start=1):
        for line in page.splitlines():
            stripped = line.strip()
            if stripped in name_set:
                heading_pages[stripped].append(idx)

    def page_prose(name: str, pdf_page: int) -> tuple[str | None, bool]:
        """(description, signatureFound) for one candidate heading page."""
        page_lines = [l.strip() for l in pages[pdf_page - 1].splitlines()
                      if l.strip()]
        hidx = page_lines.index(name)
        prose: list[str] = []
        sig_line = False
        for line in page_lines[hidx + 1:]:
            if is_footer(line) or re.fullmatch(r"\d{1,5}", line):
                continue
            m = re.search(rf"(?<![A-Za-z0-9_]){re.escape(name)}\s*\(",
                          line)
            if m:
                prefix = line[:m.start()].strip()
                # Signature start: primitive return type, or a C++-ish type
                # (CLinkedListNode<T>*, matrix::SVD). Prose that merely
                # mentions the function ("The OrderSend() function is
                # used...") still counts as description.
                if (RETURN_TYPE_RE.fullmatch(prefix + " ")
                        or re.fullmatch(r"[\w:]+(<[\w: ,]+>)?\*?", prefix)
                        and ("<" in prefix or "*" in prefix or "::" in prefix)
                        and "(" not in prefix):
                    sig_line = True
                    break
                prose.extend(line.split())
                continue
            if any(ch in line for ch in "={};"):
                break
            prose.extend(line.split())
            if len(prose) > 80:
                break
        text = " ".join(prose).strip()
        lowercase = sum(1 for w in text.split() if w.islower())
        if sig_line and is_useful(text):
            return text, True
        # Pages whose description prose is cut by the page break before the
        # signature (continued on the next page) are acceptable when the
        # heading opens the page and the prose reads like sentences.
        if (not sig_line and is_useful(text) and hidx <= 4
                and len(text.split()) >= 5 and lowercase >= 3):
            return text, False
        return None, sig_line

    for name in function_names:
        best: dict | None = None
        candidates = heading_pages[name]
        for pdf_page in candidates:  # signature-bearing pages first
            text, _ = page_prose(name, pdf_page)
            if text is not None:
                best = {"description": text, "pages": [pdf_page]}
                break
        if best is None:
            for pdf_page in candidates:  # prose-cut-at-page-break fallback
                text, sig = page_prose(name, pdf_page)
                if text is not None:
                    best = {"description": text, "pages": [pdf_page]}
                    break
        if best:
            mapping[name] = best
        else:
            unrecoverable.append({
                "name": name,
                "reason": ("no usable description prose under the heading"
                           if candidates
                           else "no standalone heading line in the "
                                "MQL5 Reference PDF"),
                "pages": candidates[:5],
            })
    return mapping, unrecoverable


# ------------------------------------------------------------- web colors --

def parse_web_colors() -> dict[str, tuple[int, int, int]]:
    """RGB values for the 133 web colors, from src/Color/MqlColorRegistry.cs
    (read-only source; the PDF swatch grid carries no values)."""
    hexes: dict[str, tuple[int, int, int]] = {}
    if COLOR_REGISTRY_PATH.exists():
        for m in COLOR_HEX_RE.finditer(COLOR_REGISTRY_PATH.read_text()):
            hexes[m.group(1)] = (int(m.group(2), 16), int(m.group(3), 16),
                                 int(m.group(4), 16))
    return hexes


# ------------------------------------------------------------------ apply --

def apply(golden: dict, mapping: dict[str, dict], today: str,
          dry_run: bool) -> tuple[int, int, list[str]]:
    """Update placeholder variable descriptions and missing function
    descriptions in place. Returns (variablesUpdated, functionsUpdated,
    skippedNames)."""
    color_hexes = parse_web_colors()
    vars_updated = 0
    fns_updated = 0
    skipped: list[str] = []

    variables = golden["variables"]
    for name, entry in variables.items():
        if entry.get("description") != PLACEHOLDER:
            continue
        is_color = name.startswith("clr")
        if not is_color and name not in mapping:
            skipped.append(name)
            continue
        if dry_run:
            vars_updated += 1
            continue
        vars_updated += 1
        if is_color:
            rgb = color_hexes.get(name, (0xFF, 0xFF, 0xFF))
            entry["description"] = (
                f"Web color 0x{rgb[0]:02X}{rgb[1]:02X}{rgb[2]:02X} "
                f"({name[3:]})")
            entry["provenance"] = entry.get("provenance", "") \
                + f"; {DESC_MARKER_COLOR}"
        else:
            entry["description"] = mapping[name]["description"]
            entry["provenance"] = entry.get("provenance", "") \
                + f"; {DESC_MARKER_PDF.format(today=today)}"

    functions = golden["functions"]
    for name, entry in functions.items():
        if "description" in entry and entry.get("description") != PLACEHOLDER:
            continue
        if name not in mapping:
            skipped.append(name)
            continue
        fns_updated += 1
        if dry_run:
            continue
        rebuilt: dict = {}
        for key, value in entry.items():
            rebuilt[key] = value
            if key == "signature":
                rebuilt["description"] = mapping[name]["description"]
        if "description" not in rebuilt:
            rebuilt["description"] = mapping[name]["description"]
        rebuilt["provenance"] = rebuilt.get("provenance", "") + (
            f"; {DESC_MARKER_PDF.format(today=today)}")
        functions[name] = rebuilt
    return vars_updated, fns_updated, skipped


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dry_run = "--dry-run" in sys.argv
    if len(args) < 3:
        print("usage: apply_mql5_descriptions.py <raw.txt> <layout.txt> "
              "<markitdown.md> [--dry-run]", file=sys.stderr)
        return 2
    raw_path, layout_path, md_path = (Path(a) for a in args[:3])

    golden = json.loads(GOLDEN_PATH.read_text())
    variables, functions = load_golden_names()
    golden_variables = set(golden["variables"])
    placeholders = {
        name for name, entry in golden["variables"].items()
        if entry.get("description") == PLACEHOLDER
    } - {n for n in golden["variables"] if n.startswith("clr")}
    enum_names = sorted(n for n in placeholders if n.startswith("ENUM_"))
    function_names = sorted(
        n for n, e in golden["functions"].items()
        if "description" not in e or e["description"] == PLACEHOLDER)

    raw_text = raw_path.read_text(encoding="utf-8", errors="replace")
    layout_text = layout_path.read_text(encoding="utf-8", errors="replace")
    markdown_text = md_path.read_text(encoding="utf-8", errors="replace")

    chapter_pages = [
        i for i, page in enumerate(layout_text.split("\f"), start=1)
        if CHAPTER_FOOTER_RE.search(page)
    ]
    vocabulary = set(variables) | set(functions)
    for line in raw_text.splitlines():
        vocabulary.update(IDENT_RE.findall(line))
        vocabulary.update(COLOR_RE.findall(line))
    complete = build_complete_words(markdown_text)

    print(f"Chapter pages: {len(chapter_pages)}; placeholders to fill: "
          f"{len(placeholders)} variables, {len(function_names)} functions.")

    const_map, const_evidence = parse_tables(
        raw_text, chapter_pages, golden_variables, vocabulary, complete,
        placeholders)
    print(f"Constant tables: {len(const_map)} descriptions from "
          f"{len(const_evidence)} table pages.")

    enum_map, enum_unrecoverable = parse_enum_types(
        raw_text, enum_names, golden_variables, vocabulary, complete)
    print(f"ENUM_* types: {len(enum_map)} descriptions, "
          f"{len(enum_unrecoverable)} unrecoverable.")

    fn_map, fn_unrecoverable = parse_function_descriptions(
        raw_text, function_names)
    print(f"Functions: {len(fn_map)} descriptions, "
          f"{len(fn_unrecoverable)} unrecoverable.")

    mapping = {**const_map, **enum_map, **fn_map}
    unrecoverable = enum_unrecoverable + fn_unrecoverable + [
        {"name": n, "reason": "no table row found in the reference dump",
         "pages": []}
        for n in sorted(placeholders - set(const_map) - set(enum_map))
    ]

    today = date.today().isoformat()
    vars_updated, fns_updated, skipped = apply(
        golden, mapping, today, dry_run)
    print(f"Apply: {vars_updated} variable descriptions, "
          f"{fns_updated} function descriptions"
          + (" (dry run)" if dry_run else "") + ".")

    remaining = sum(
        1 for e in golden["variables"].values()
        if e.get("description") == PLACEHOLDER)
    fns_missing = sum(
        1 for e in golden["functions"].values()
        if "description" not in e or e["description"] == PLACEHOLDER)
    print(f"Coverage: variables with placeholder {remaining} "
          f"(baseline 1644); functions without description {fns_missing} "
          f"(baseline 816).")
    if skipped:
        print(f"[validate] placeholders with no extracted description "
              f"({len(skipped)}): " + ", ".join(sorted(skipped)[:40]))

    evidence = {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "source": "official MQL5 Reference PDF (pdftotext raw/layout dumps)",
        "counts": {
            "chapterPages": len(chapter_pages),
            "constantsExtracted": len(const_map),
            "enumTypesExtracted": len(enum_map),
            "functionsExtracted": len(fn_map),
            "variablesUpdated": vars_updated,
            "functionsUpdated": fns_updated,
            "placeholdersRemaining": remaining,
            "functionsWithoutDescription": fns_missing,
        },
        "unrecoverable": unrecoverable,
        "descriptions": {k: v for k, v in sorted(mapping.items())},
    }
    MAPPING_PATH.parent.mkdir(parents=True, exist_ok=True)
    MAPPING_PATH.write_text(json.dumps(evidence, indent=2,
                                       ensure_ascii=False) + "\n")
    print(f"Extraction evidence written to {MAPPING_PATH} "
          f"({len(mapping)} descriptions).")

    if dry_run:
        print("Dry run: golden list not modified.")
        return 0

    if vars_updated or fns_updated:
        golden["provenance"] = (
            golden.get("provenance", "")
            + f" | Description enrichment {today} from the official MQL5 "
              "Reference PDF by tools/generate-golden-lists/"
              f"apply_mql5_descriptions.py "
              f"(+{vars_updated} variable and +{fns_updated} function "
              "descriptions).")
        GOLDEN_PATH.write_text(
            json.dumps(golden, indent=2, ensure_ascii=False) + "\n")
        print("Golden list updated.")
    else:
        print("Nothing new to apply; golden list not modified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
