#!/usr/bin/env python3
"""
clauses_plot.py - Visualize clause dependency graph from .clause files.

Produces a Graphviz DOT file and renders PNG if graphviz is available.

Usage:
    python Clauses/clauses_plot.py                    # plot all .clause files
    python Clauses/clauses_plot.py 01-core.clause     # plot specific files
"""

import json
import subprocess
import sys
from pathlib import Path

# Import clauses_gen for resolution
sys.path.insert(0, str(Path(__file__).parent))
import clauses_gen


def build_dot(all_data, title_map):
    """Build Graphviz DOT string from resolved clauses."""
    # Reverse map for cuid -> title
    cuid_to_title = {v: k for k, v in title_map.items()}

    lines = [
        "digraph clauses {",
        "  rankdir=LR;",
        "  node [shape=box, style=rounded, fontname=\"Segoe UI\", fontsize=10, "
        "margin=\"0.15,0.08\"];",
        "  edge [color=\"#999999\", arrowsize=0.7];",
    ]

    # Nodes
    for _, clauses in all_data:
        for c in clauses:
            cuid = c["cuid"]
            title = c.get("title", cuid)
            label = f"{title}\\n{cuid}"
            # Root nodes in light blue, others white
            is_root = len(c["accordance-cuid"]) == 0
            color = "#E3F2FD" if is_root else "#FFFFFF"
            lines.append(f'  "{cuid}" [label="{label}", fillcolor="{color}", style="filled,rounded"];')

    # Edges (child -> parent)
    seen_edges = set()
    for _, clauses in all_data:
        for c in clauses:
            child = c["cuid"]
            for parent in c["accordance-cuid"]:
                edge = (child, parent)
                if edge not in seen_edges:
                    seen_edges.add(edge)
                    lines.append(f'  "{child}" -> "{parent}";')

    lines.append("}")
    return "\n".join(lines)


def main():
    clauses_dir = Path(__file__).parent
    args = [a for a in sys.argv[1:] if not a.startswith("-")]

    # Resolve all .json (single source of truth)
    all_data, title_map = clauses_gen.load_and_resolve(clauses_dir)
    if not all_data:
        print("No .json files found in", clauses_dir)
        return 1

    # Filter by args if given (match clause file stems)
    if args:
        wanted = {Path(a).stem for a in args}
        all_data = [(p, c) for p, c in all_data if p.stem in wanted]
        if not all_data:
            print(f"No matching files for: {args}")
            return 1

    total = sum(len(c) for _, c in all_data)
    print(f"Plotting {len(all_data)} file(s), {total} clause(s).")

    # Build DOT
    dot = build_dot(all_data, title_map)

    # Find dot executable
    dot_exe = "dot"
    if subprocess.run(["where", "dot"], capture_output=True).returncode != 0:
        for candidate in [
            r"C:\Program Files\Graphviz\bin\dot.exe",
            r"C:\Program Files (x86)\Graphviz\bin\dot.exe",
        ]:
            if Path(candidate).exists():
                dot_exe = candidate
                break

    # Render PNG directly via stdin/stdout pipe (no intermediate .dot file)
    png_path = clauses_dir / "clauses.png"
    try:
        result = subprocess.run(
            [dot_exe, "-Tpng", "-Gdpi=200"],
            input=dot.encode("utf-8"), capture_output=True, timeout=60
        )
        if result.returncode == 0:
            with open(png_path, "wb") as f:
                f.write(result.stdout)
            print(f"PNG rendered to {png_path}")
        else:
            err = result.stderr.decode("utf-8", errors="replace").strip()
            print(f"graphviz render failed: {err}")
            print("Install graphviz to render: winget install graphviz")
    except FileNotFoundError:
        print("graphviz not found. Install: winget install graphviz")
    except subprocess.TimeoutExpired:
        print("graphviz render timed out.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
