#!/usr/bin/env python3
"""
clauses_gen.py - Resolve title-based .json sources into cuid-based .clause files.

JSON source format (human-readable):
    { "title": "SYS_ROOT", "accordance": ["SYS_MULTIPROC"], "content": "..." }

Clause output format (hash-based):
    { "cuid": "27f25001", "accordance": ["SYS_MULTIPROC"], "accordance-cuid": ["3c7f41b1"], "content": "..." }

cuid = SHA-1(content)[:8]

Usage:
    python Clauses/gen_clauses.py                          # resolve + format + write
    python Clauses/gen_clauses.py 01-core.json             # process subset
    python Clauses/gen_clauses.py --check                  # resolve only, no write
"""

import hashlib
import json
import sys
from pathlib import Path


def format_json(clauses, fields):
    """Serialize clauses with specified fields. Arrays stay on single lines."""
    lines = ["["]
    for i, c in enumerate(clauses):
        lines.append("  {")
        for j, (key, getter) in enumerate(fields):
            val = getter(c)
            val_str = json.dumps(val, ensure_ascii=False)
            comma = "," if j < len(fields) - 1 else ""
            lines.append(f'    "{key}": {val_str}{comma}')
        lines.append("  }" + ("," if i < len(clauses) - 1 else ""))
    lines.append("]")
    return "\n".join(lines) + "\n"


def load_and_resolve(clauses_dir=None):
    """
    Load all .json, build title->cuid map, resolve accordance.
    Returns (all_data, title_map) where all_data is [(Path, resolved_clauses)].
    Each resolved clause has: title, cuid, accordance (titles), accordance-cuid (cuids), content.
    Raises ValueError on title conflicts.
    """
    if clauses_dir is None:
        clauses_dir = Path(__file__).parent
    elif not isinstance(clauses_dir, Path):
        clauses_dir = Path(clauses_dir)

    all_json = sorted(clauses_dir.glob("*.json"))
    if not all_json:
        return [], {}

    all_data = []
    for p in all_json:
        with open(p, "r", encoding="utf-8") as f:
            data = json.load(f)
        all_data.append((p, data))

    title_map = {}
    for p, clauses in all_data:
        for c in clauses:
            title = c["title"]
            cuid = hashlib.sha1(c["content"].encode("utf-8")).hexdigest()[:8]
            if title in title_map and title_map[title] != cuid:
                raise ValueError(
                    f"Title '{title}' in {p.name} maps to "
                    f"{title_map[title]} (previous) vs {cuid} (current)"
                )
            title_map[title] = cuid

    resolved = []
    for p, clauses in all_data:
        out = []
        for c in clauses:
            out.append({
                "title": c["title"],
                "cuid": title_map[c["title"]],
                "accordance": c["accordance"],
                "accordance-cuid": [title_map.get(a, a) for a in c["accordance"]],
                "content": c["content"],
            })
        resolved.append((p, out))

    return resolved, title_map


def write_outputs(all_data, target_paths=None, out_dir=None):
    """Write formatted .json (in place) and .clause files (to out_dir)."""
    if out_dir is None:
        out_dir = Path(__file__).parent / "clause_file"
    out_dir.mkdir(exist_ok=True)

    json_fields = [
        ("title", lambda c: c["title"]),
        ("accordance", lambda c: c["accordance"]),
        ("content", lambda c: c["content"]),
    ]
    clause_fields = [
        ("cuid", lambda c: c["cuid"]),
        ("accordance", lambda c: c["accordance"]),
        ("accordance-cuid", lambda c: c["accordance-cuid"]),
        ("content", lambda c: c["content"]),
    ]

    written = 0
    for p, clauses in all_data:
        if target_paths and p not in target_paths:
            continue

        # Tidy .json in place
        src = [{"title": c["title"], "accordance": c["accordance"], "content": c["content"]}
               for c in clauses]
        with open(p, "w", encoding="utf-8") as f:
            f.write(format_json(src, json_fields))

        # Write .clause to out_dir
        out_path = out_dir / p.with_suffix(".clause").name
        with open(out_path, "w", encoding="utf-8") as f:
            f.write(format_json(clauses, clause_fields))
        print(f"  {out_path.name} ({len(clauses)} clauses)")
        written += 1

    return written


def main():
    clauses_dir = Path(__file__).parent
    args = [a for a in sys.argv[1:] if a != "--check"]
    check_only = "--check" in sys.argv

    all_data, title_map = load_and_resolve(clauses_dir)
    if not all_data:
        print("No .json files found in", clauses_dir)
        return 1

    total = sum(len(c) for _, c in all_data)
    print(f"Loaded {len(all_data)} source file(s), {total} clause(s) total.")

    print(f"\nResolved {len(title_map)} title(s):")
    for title, cuid in sorted(title_map.items()):
        print(f"  {title} -> {cuid}")

    if check_only:
        print("\n(--check mode: skipping write)")
        return 0

    # Filter targets
    if args:
        wanted = {Path(a).stem for a in args}
        target_paths = {p for p, _ in all_data if p.stem in wanted}
        if not target_paths:
            print(f"No matching files for: {args}")
            return 1
        print(f"\nTarget: {len(target_paths)} file(s).")
    else:
        target_paths = None

    print(f"\nWriting output:")
    written = write_outputs(all_data, target_paths)
    print(f"\nDone. {written} .clause file(s) generated.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
