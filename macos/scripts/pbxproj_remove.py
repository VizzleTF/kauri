"""Remove objects from an Xcode project.pbxproj by ID, with every line that references them.

usage: python scripts/pbxproj_remove.py Clipy.xcodeproj/project.pbxproj ID [ID...]

An object block `ID /* name */ = { ... };` is removed whole. Single-line
references (`ID /* name */,` in lists, or one-line PBXBuildFile entries)
are removed too.
"""
import re
import sys

path, ids = sys.argv[1], sys.argv[2:]
lines = open(path, encoding="utf-8").read().split("\n")
out = []
skip_depth = 0
for line in lines:
    if skip_depth:
        skip_depth += line.count("{") - line.count("}")
        continue
    stripped = line.strip()
    hit = next((i for i in ids if stripped.startswith(i + " ")), None)
    if hit and re.match(re.escape(hit) + r" /\*.*\*/ = \{", stripped):
        depth = line.count("{") - line.count("}")
        if depth > 0:
            skip_depth = depth
        continue
    if any(stripped.startswith(i + " /*") and stripped.endswith(",") for i in ids):
        continue
    out.append(line)
open(path, "w", encoding="utf-8", newline="\n").write("\n".join(out))
print("removed", len(lines) - len(out), "lines")
