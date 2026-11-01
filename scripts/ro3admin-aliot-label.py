"""Correct the translated fragment label while preserving every other site translation."""
import json
import sys
from pathlib import Path

source, target = map(Path, sys.argv[1:])
text = source.read_text(encoding="utf-8-sig")
old = '"Elio Fragment":"Fragmento de Elio"'
new = '"Elio Fragment":"Fragmento de Aliot"'
if text.count(old) != 1:
    raise SystemExit("Expected exactly one original Aliot fragment translation")
updated = text.replace(old, new)
before, after = json.loads(text), json.loads(updated)
before["item"]["Elio Fragment"] = "Fragmento de Aliot"
if before != after:
    raise SystemExit("Unexpected translation change")
target.write_text(updated, encoding="utf-8", newline="")
