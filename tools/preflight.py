#!/usr/bin/env python3
"""Preflight: lay every sheet's rows over its columns and list what will fail.

Checks, for every sheet in sheets/*.json:
  - every required column is filled in every row (not null, "", "?", "TODO");
  - enum cells hold one of the listed values; int/number/bool cells have that type;
  - every ref cell resolves to a row of the referenced sheet ("sheet.column", or a list of them);
  - no row has a column the sheet doesn't declare;
  - ids are unique.
Rows may carry "_unverified": [columns] for values that are assumptions not yet checked in a
real run. Those are listed as UNVERIFIED (they don't block a build, but they block "done").

Exit code 0 only when there are no ERROR lines.
"""
import json
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SHEETS = os.path.join(ROOT, "sheets")
EMPTY = (None, "", "?", "TODO", "TBD")


def load():
    sheets = {}
    for name in sorted(os.listdir(SHEETS)):
        if name.endswith(".json"):
            with open(os.path.join(SHEETS, name), encoding="utf-8") as f:
                s = json.load(f)
            sheets[s["sheet"]] = s
    return sheets


def type_ok(col, v):
    t = col["type"]
    if t == "int":
        return isinstance(v, int) and not isinstance(v, bool)
    if t == "number":
        return isinstance(v, (int, float)) and not isinstance(v, bool)
    if t == "bool":
        return isinstance(v, bool)
    if t == "enum":
        return v in col["values"]
    if t in ("string", "ref"):
        return isinstance(v, str)
    if t == "list":
        return isinstance(v, list)
    return True  # any


def main():
    sheets = load()
    errors, unverified = [], []
    index = {}
    for sname, s in sheets.items():
        for cname in s["columns"]:
            index[(sname, cname)] = {str(r.get(cname)) for r in s["rows"]}

    for sname, s in sheets.items():
        cols = s["columns"]
        key = "id" if "id" in cols else None
        seen = set()
        for i, row in enumerate(s["rows"]):
            label = f"{sname}[{row.get(key, i) if key else i}]"
            if key:
                if row.get(key) in seen:
                    errors.append(f"{label}: duplicate id")
                seen.add(row.get(key))
            for extra in set(row) - set(cols) - {"_unverified", "_note"}:
                errors.append(f"{label}.{extra}: column not declared in sheet")
            for cname, col in cols.items():
                v = row.get(cname)
                if v in EMPTY or (isinstance(v, list) and not v and col.get("required")):
                    if col.get("required"):
                        errors.append(f"{label}.{cname}: unfilled")
                    continue
                if not type_ok(col, v):
                    errors.append(f"{label}.{cname}: {v!r} is not {col['type']}"
                                  + (f" {col['values']}" if col["type"] == "enum" else ""))
                    continue
                if col["type"] == "ref" or col.get("ref"):
                    refs = col["ref"] if isinstance(col["ref"], list) else [col["ref"]]
                    vals = v if isinstance(v, list) else [v]
                    for one in vals:
                        ok = False
                        for r in refs:
                            rs, rc = r.split(".")
                            if rs not in sheets:
                                errors.append(f"{label}.{cname}: refers to missing sheet {rs}")
                                ok = True
                                break
                            if str(one) in index[(rs, rc)]:
                                ok = True
                                break
                        if not ok:
                            errors.append(f"{label}.{cname}: {one!r} not found in {' or '.join(refs)}")
            for u in row.get("_unverified", []):
                unverified.append(f"{label}.{u}")

    for e in errors:
        print("ERROR", e)
    for u in unverified:
        print("UNVERIFIED", u)
    total = sum(len(s["rows"]) * len(s["columns"]) for s in sheets.values())
    print(f"preflight: {len(sheets)} sheets, {total} cells, {len(errors)} errors, {len(unverified)} unverified")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
