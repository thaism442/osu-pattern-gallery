"""Adds a pattern from an approved GitHub issue to the community pack.

Reads ISSUE_TITLE, ISSUE_BODY and ISSUE_USER, checks the pattern, keeps only safe .osu data,
saves it to community/<category>/<name>.osu and rebuilds packs/community.zip and packs/index.json.
"""
import json, os, re, sys, zipfile

title = os.environ.get("ISSUE_TITLE", "")
body = os.environ.get("ISSUE_BODY", "").replace("\r\n", "\n")
user = os.environ.get("ISSUE_USER", "unknown")

def clean(s, default):
    s = re.sub(r"[^0-9A-Za-zçğıöşüÇĞİÖŞÜ _\-]", "", s or "").strip()[:40]
    return s or default

name = clean(re.sub(r"^\s*\[pattern\]\s*", "", title, flags=re.I), "pattern")
m = re.search(r"^(?:category|kategori)\s*:\s*(.+)$", body, flags=re.I | re.M)
category = clean(m.group(1) if m else "", "genel").lower()

block = re.search(r"```[a-z]*\n(.*?)```", body, flags=re.S)
if not block:
    sys.exit("No pattern found in the issue (expected a ``` code block).")
text = block.group(1)
if len(text) > 50_000:
    sys.exit("Pattern is too big.")

# keep only the parts the program uses, with strict line checks
sections, current = {"Difficulty": [], "TimingPoints": [], "HitObjects": []}, None
for raw in text.split("\n"):
    line = raw.strip()
    if line.startswith("["):
        current = line.strip("[]") if line.strip("[]") in sections else None
        continue
    if not current or not line:
        continue
    if current == "Difficulty" and re.fullmatch(r"(SliderMultiplier|SliderTickRate)\s*:\s*[0-9.]+", line):
        sections[current].append(line)
    elif current == "TimingPoints" and re.fullmatch(r"[0-9.,\-]+", line):
        sections[current].append(line)
    elif current == "HitObjects" and re.fullmatch(r"[0-9A-Za-z.,:|\-]+", line):
        sections[current].append(line)

if not sections["HitObjects"]:
    sys.exit("The pattern has no hit objects.")
if len(sections["HitObjects"]) > 300:
    sys.exit("Too many objects for a pattern.")

out = ["osu file format v14", "", f"// Community pattern by {clean(user, 'unknown')}", ""]
for sec in ("Difficulty", "TimingPoints", "HitObjects"):
    out += [f"[{sec}]"] + sections[sec] + [""]

folder = os.path.join("community", category)
os.makedirs(folder, exist_ok=True)
path = os.path.join(folder, f"{name}.osu")
i = 2
while os.path.exists(path):
    path = os.path.join(folder, f"{name} ({i}).osu"); i += 1
with open(path, "w", encoding="utf-8", newline="\r\n") as f:
    f.write("\n".join(out))
print("Saved", path)

# rebuild the community pack
os.makedirs("packs", exist_ok=True)
count = 0
with zipfile.ZipFile("packs/community.zip", "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk("community"):
        for fn in sorted(files):
            if fn.endswith(".osu"):
                full = os.path.join(root, fn)
                z.write(full, os.path.relpath(full, "community").replace(os.sep, "/"))
                count += 1

index_path = "packs/index.json"
index = json.load(open(index_path, encoding="utf-8")) if os.path.exists(index_path) else {"version": 1, "packs": []}
entry = {
    "id": "community", "file": "community.zip", "author": "community", "count": count,
    "name": {"tr": "Topluluk Patternleri", "en": "Community Patterns"},
    "desc": {"tr": "Mapperların programdan paylaştığı patternler.", "en": "Patterns shared by mappers from the program."},
}
index["packs"] = [p for p in index["packs"] if p.get("id") != "community"] + [entry]
json.dump(index, open(index_path, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("Community pack:", count, "patterns")
