"""Turn the imported Clipy 1.4.0 tree into Kauri Clip (idempotent).

usage: python scripts/rebrand.py   (from macos/)
"""
import json
import pathlib
import re

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
BRANDING = ROOT.parent / "branding"


def edit(path, fn):
    p = ROOT / path
    s = p.read_text(encoding="utf-8")
    n = fn(s)
    if n != s:
        p.write_text(n, encoding="utf-8", newline="\n")
        print("edited", path)


# 1. User-facing strings: every localized value that names Clipy.
for fn in ["Clipy/Resources/Localizable.xcstrings", "Clipy/Resources/Settings.xcstrings"]:
    p = ROOT / fn
    d = json.loads(p.read_text(encoding="utf-8"))
    for key, v in d["strings"].items():
        if key in ("basedOnClipyDescription", "Clipy on GitHub…"):
            continue  # Kauri's own strings that name the upstream project
        locs = v.setdefault("localizations", {})
        if "Clipy" in key and "en" not in locs:
            locs["en"] = {"stringUnit": {"state": "translated", "value": key}}
        for loc in locs.values():
            unit = loc.get("stringUnit")
            if unit and "Clipy" in unit["value"]:
                unit["value"] = unit["value"].replace("Clipy", "Kauri")
    # Xcode's own layout ("key" : value), so the diff shows only the renames.
    p.write_text(json.dumps(d, ensure_ascii=False, indent=2, separators=(",", " : ")) + "\n", encoding="utf-8", newline="\n")
    print("rebranded", fn)


# 2. Bundle identity and product name of the app target.
def project(s):
    s = s.replace('PRODUCT_BUNDLE_IDENTIFIER = "com.clipy-app.Clipy.debug";', 'PRODUCT_BUNDLE_IDENTIFIER = "io.github.vizzletf.kauri.debug";')
    s = s.replace('PRODUCT_BUNDLE_IDENTIFIER = "com.clipy-app.Clipy";', 'PRODUCT_BUNDLE_IDENTIFIER = "io.github.vizzletf.kauri";')
    s = s.replace('PRODUCT_BUNDLE_IDENTIFIER = "com.clipyapp.$(PRODUCT_NAME:rfc1034identifier)";', 'PRODUCT_BUNDLE_IDENTIFIER = "io.github.vizzletf.kauri.tests";')
    # The app target's configurations are the ones with INFOPLIST_FILE of the app.
    out, block = [], []
    for line in s.split("\n"):
        block.append(line)
        if line.strip() == "};":
            text = "\n".join(block)
            if 'INFOPLIST_FILE = "Clipy/Supporting Files/Info.plist";' in text and "PRODUCT_NAME = Kauri;" not in text:
                text = re.sub(r"(\t+)INFOPLIST_FILE = \"Clipy/Supporting Files/Info.plist\";",
                              r'\1INFOPLIST_FILE = "Clipy/Supporting Files/Info.plist";\n\1PRODUCT_NAME = Kauri;', text)
            out.append(text)
            block = []
    out.extend(block)
    s = "\n".join(out)
    # The tests load the app as TEST_HOST.
    s = s.replace("Clipy.app/Contents/MacOS/Clipy", "Kauri.app/Contents/MacOS/Kauri")
    return s


edit("Clipy.xcodeproj/project.pbxproj", project)


# 3. Info.plist: no Sparkle feed of Clipy's, our copyright and version.
def plist(s):
    s = re.sub(r"\t<key>SUFeedURL</key>\n\t<string>[^<]*</string>\n", "", s)
    s = re.sub(r"\t<key>SUPublicDSAKeyFile</key>\n\t<string>[^<]*</string>\n", "", s)
    s = re.sub(r"\t<key>SUPublicEDKey</key>\n\t<string>[^<]*</string>\n", "", s)
    s = s.replace("<string>Copyright © 2015年 Shunsuke Furubayashi. All rights reserved.</string>",
                  "<string>© 2026 Kauri Clip contributors. Based on Clipy, © 2015-2026 Clipy Project (MIT).</string>")
    s = s.replace("<key>CFBundleShortVersionString</key>\n\t<string>1.4.0</string>", "<key>CFBundleShortVersionString</key>\n\t<string>1.0.0</string>")
    s = s.replace("<key>CFBundleVersion</key>\n\t<string>1.4.0</string>", "<key>CFBundleVersion</key>\n\t<string>1</string>")
    if "CFBundleDisplayName" not in s:
        s = s.replace("\t<key>CFBundleExecutable</key>", "\t<key>CFBundleDisplayName</key>\n\t<string>Kauri Clip</string>\n\t<key>CFBundleExecutable</key>")
    return s


edit("Clipy/Supporting Files/Info.plist", plist)

# 4. Signing: ad-hoc by default (Clipy's certificate belongs to its maintainer).
(ROOT / "Configurations/CodeSigning.xcconfig").write_text(
    "// Kauri Clip builds are ad-hoc signed by default. To keep Accessibility\n"
    "// permission across builds, sign with your own Developer ID instead.\n\n"
    '#include "Configurations/CodeSigning-AdHoc.xcconfig"\n', encoding="utf-8", newline="\n")

# 5. App icon and menu bar icon from branding/.
icon = Image.open(BRANDING / "kauri-icon-1024.png").convert("RGBA")
appicon = ROOT / "Clipy/Resources/Assets.xcassets/AppIcon.appiconset"
images = []
for size in (16, 32, 128, 256, 512):
    for scale in (1, 2):
        name = f"{size}{'@2x' if scale == 2 else ''}.png"
        icon.resize((size * scale, size * scale), Image.LANCZOS).save(appicon / name)
        images.append({"filename": name, "idiom": "mac", "scale": f"{scale}x", "size": f"{size}x{size}"})
(appicon / "Contents.json").write_text(json.dumps({"images": images, "info": {"author": "xcode", "version": 1}}, indent=2) + "\n", encoding="utf-8")

status = ROOT / "Clipy/Resources/Assets.xcassets/StatusIcon"
for name, src in [("statusbar_menu_black", "kauri-tray-glyph-64.png"), ("statusbar_menu_white", "kauri-tray-glyph-reversed-64.png")]:
    glyph = Image.open(BRANDING / src).convert("RGBA")
    d = status / f"{name}.imageset"
    for old in d.glob("*.png"):
        old.unlink()
    glyph.resize((36, 36), Image.LANCZOS).save(d / f"{name}@2x.png")
    glyph.resize((18, 18), Image.LANCZOS).save(d / f"{name}.png")
    (d / "Contents.json").write_text(json.dumps({
        "images": [
            {"filename": f"{name}.png", "idiom": "universal", "scale": "1x"},
            {"filename": f"{name}@2x.png", "idiom": "universal", "scale": "2x"},
            {"idiom": "universal", "scale": "3x"},
        ],
        "info": {"author": "xcode", "version": 1},
        "properties": {"template-rendering-intent": "template"},
    }, indent=2) + "\n", encoding="utf-8")
print("icons done")
