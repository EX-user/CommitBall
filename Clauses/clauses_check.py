#!/usr/bin/env python3
"""
clauses_check.py - Validate clause DAG, dangling references, and structure.

Calls clauses_gen.load_and_resolve() to get resolved data (single source of truth),
then validates. Does not duplicate resolution logic.

Usage:
    python Clauses/clauses_check.py      # validate
    python Clauses/clauses_check.py -g   # generate .clause first, then validate
"""

import json
import sys
from collections import defaultdict
from pathlib import Path

# Import clauses_gen from same directory
sys.path.insert(0, str(Path(__file__).parent))
import clauses_gen


def validate(all_data):
    """Validate resolved clauses: DAG, dangling, roots, leaves."""
    all_cuids = set()
    edges = defaultdict(list)
    dangling = set()

    for _, clauses in all_data:
        for c in clauses:
            cuid = c["cuid"]
            all_cuids.add(cuid)
            for a in c["accordance-cuid"]:
                edges[cuid].append(a)

    for cuid, parents in edges.items():
        for p in parents:
            if p not in all_cuids:
                dangling.add((cuid, p))

    # Cycle detection (DFS coloring)
    WHITE, GRAY, BLACK = 0, 1, 2
    color = defaultdict(lambda: WHITE)
    has_cycle = [False]
    cycle_path = []

    def dfs(node, path):
        if has_cycle[0]:
            return
        color[node] = GRAY
        path.append(node)
        for parent in edges.get(node, []):
            if parent not in all_cuids:
                continue
            if color[parent] == GRAY:
                has_cycle[0] = True
                idx = path.index(parent)
                cycle_path.extend(path[idx:] + [parent])
                return
            if color[parent] == WHITE:
                dfs(parent, path)
                if has_cycle[0]:
                    return
        path.pop()
        color[node] = BLACK

    for cuid in all_cuids:
        if color[cuid] == WHITE:
            dfs(cuid, [])
            if has_cycle[0]:
                break

    referenced_by = defaultdict(set)
    has_parent = set()
    for _, clauses in all_data:
        for c in clauses:
            for a in c["accordance-cuid"]:
                referenced_by[a].add(c["cuid"])
                has_parent.add(c["cuid"])

    roots = sorted(c for c in all_cuids if c not in has_parent)
    leaves = sorted(c for c in all_cuids if c not in referenced_by)

    return {
        "dag_ok": not has_cycle[0],
        "cycle": cycle_path,
        "dangling": sorted(dangling),
        "roots": roots,
        "leaves": leaves,
    }


def main():
    clauses_dir = Path(__file__).parent
    generate_first = "-g" in sys.argv

    if generate_first:
        print("=== Generating .clause files first ===")
        # Remove -g from argv so clauses_gen doesn't treat it as a filename
        saved_argv = sys.argv[:]
        sys.argv = [a for a in sys.argv if a != "-g"]
        ret = clauses_gen.main()
        sys.argv = saved_argv
        if ret != 0:
            return ret
        print()

    # Resolve (single source of truth - always from .json)
    all_data, title_map = clauses_gen.load_and_resolve(clauses_dir)
    if not all_data:
        print("No .json files found in", clauses_dir)
        return 1

    total = sum(len(c) for _, c in all_data)
    print(f"Loaded {len(all_data)} file(s), {total} clause(s) total.\n")

    r = validate(all_data)

    print("--- Validation ---")
    if r["dag_ok"]:
        print("[OK]   DAG: no cycles")
    else:
        print(f"[FAIL] DAG: cycle detected: {' -> '.join(r['cycle'])}")

    if not r["dangling"]:
        print("[OK]   Dangling references: none")
    else:
        print(f"[FAIL] Dangling references: {len(r['dangling'])}")
        for cuid, ref in r["dangling"]:
            print(f"  {cuid} -> {ref}")

    print(f"\nRoot clauses (no accordance): {len(r['roots'])}")
    for c in r["roots"]:
        print(f"  {c}")

    print(f"Leaf clauses (unreferenced): {len(r['leaves'])}")
    for c in r["leaves"]:
        print(f"  {c}")

    # Plot
    print("\n--- Plot ---")
    import clauses_plot
    clauses_plot.main()

    return 0 if (r["dag_ok"] and not r["dangling"]) else 1


if __name__ == "__main__":
    sys.exit(main())
