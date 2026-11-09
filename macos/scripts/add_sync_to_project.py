"""Add Kauri Sync to the Xcode project (idempotent):

- folder-synchronized groups Clipy/Sources/Sync (app) and ClipyTests/Sync (tests),
  so new Swift files there need no project edits;
- PRODUCT_MODULE_NAME = Clipy, so `@testable import Clipy` keeps working after
  the product was renamed to Kauri.

Sync needs no package: CryptoKit does the rest, and XChaCha20 and the
edwards25519 group live in Clipy/Sources/Sync.
"""
import pathlib
import re

P = pathlib.Path(__file__).resolve().parent.parent / "Clipy.xcodeproj/project.pbxproj"
s = P.read_text(encoding="utf-8")
if "4B4155524900000000000001 /* Sync */" in s:
    print("already added")
    raise SystemExit

GROUP, TGROUP = "4B4155524900000000000001", "4B4155524900000000000005"


def insert_after(anchor, text):
    global s
    i = s.index(anchor) + len(anchor)
    s = s[:i] + text + s[i:]


def sync_group_of_target(name, group):
    """Adds `group` to the fileSystemSynchronizedGroups of target `name`."""
    global s
    m = re.search(r"\t\t\w{24} /\* " + name + r" \*/ = \{\n\t\t\tisa = PBXNativeTarget;\n", s)
    assert m, name + " target not found"
    start = m.end()
    end = s.index("\t\t};\n", start)
    block = s[start:end]
    if "fileSystemSynchronizedGroups" in block:
        block = block.replace("fileSystemSynchronizedGroups = (\n", f"fileSystemSynchronizedGroups = (\n\t\t\t\t{group} /* Sync */,\n", 1)
    else:
        block = block.replace(f"\t\t\tname = {name};\n", f"\t\t\tfileSystemSynchronizedGroups = (\n\t\t\t\t{group} /* Sync */,\n\t\t\t);\n\t\t\tname = {name};\n", 1)
    s = s[:start] + block + s[end:]


insert_after("/* Begin PBXFileSystemSynchronizedRootGroup section */\n",
             f'\t\t{GROUP} /* Sync */ = {{\n\t\t\tisa = PBXFileSystemSynchronizedRootGroup;\n\t\t\tpath = Sync;\n\t\t\tsourceTree = "<group>";\n\t\t}};\n'
             f'\t\t{TGROUP} /* Sync */ = {{\n\t\t\tisa = PBXFileSystemSynchronizedRootGroup;\n\t\t\tpath = Sync;\n\t\t\tsourceTree = "<group>";\n\t\t}};\n')
# The Sources group and the ClipyTests group show the folders.
s = s.replace("\t\t\t\tC5700B452FC40BBF00C8F965 /* Repositories */,\n",
              f"\t\t\t\t{GROUP} /* Sync */,\n\t\t\t\tC5700B452FC40BBF00C8F965 /* Repositories */,\n", 1)
m = re.search(r"\t\t\w{24} /\* ClipyTests \*/ = \{\n\t\t\tisa = PBXGroup;\n\t\t\tchildren = \(\n", s)
assert m, "ClipyTests group not found"
insert_after(m.group(0), f"\t\t\t\t{TGROUP} /* Sync */,\n")
sync_group_of_target("Clipy", GROUP)
sync_group_of_target("ClipyTests", TGROUP)

# Keep the Swift module named Clipy.
s = s.replace("\t\t\t\tPRODUCT_NAME = Kauri;\n", "\t\t\t\tPRODUCT_MODULE_NAME = Clipy;\n\t\t\t\tPRODUCT_NAME = Kauri;\n")
P.write_text(s, encoding="utf-8", newline="\n")
print("added the Sync groups")
