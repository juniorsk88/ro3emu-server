#!/usr/bin/env python3
"""Writes docs/guide/npc-services-catalog.md: every NPC of the client's data that has an interaction, with the decision the
client hook makes for it (gets a service, or is left as it is) and why.

Reads data/config (your exported game data) and src/Ro3Emu.ClientHooks/NpcServiceTable.cs (the table of NPCs that get a
service). Run from anywhere:  python tools/npc-catalog/npc_catalog.py
"""
import collections
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "data" / "config"
TABLE = ROOT / "src" / "Ro3Emu.ClientHooks" / "NpcServiceTable.cs"
OUT = ROOT / "docs" / "guide" / "npc-services-catalog.md"


def load(name):
    return json.loads((CONFIG / name).read_text(encoding="utf-8"))


npcs = load("NPCConfig.json")
interactions = load("NPCInteractConfig.json")
actions = load("ActionConfig.json")
dialogues = {k: v for k, v in load("DialogueConfig.json").items() if isinstance(v, list)}
loc = load("Localization_en.json")


def text(i):
    return (loc.get(str(i)) or {}).get("_kDes", f"#{i}")


def action_name(head):
    return (actions.get(str(head)) or {}).get("_iFunctionName", f"action {head}")


# What the table gives each NPC.
source = TABLE.read_text(encoding="utf-8")
SERVICE_TEXT = {
    "Supply": "Open Shop: Supply Shop 140 (consumables, wings)",
    "PetShop": "Open Shop: Pet Shop 210",
    "Headwear": "Open Shop: Appearance Shop, headwear",
    "Hairstyle": "Open Shop: Appearance Shop, hairstyles",
    "Refine": "Refine: equipment panel, refine page",
    "Wardrobe": "Wardrobe: the Wardrobe panel",
    "Guild": "Guild: the Guild panel",
}
service_of = {}
for name, dialogue, rows, service in re.findall(r'new NpcDialogue\("([^"]+)", (\d+), (\d+), NpcService\.(\w+)\)', source):
    service_of[int(dialogue) // 100] = SERVICE_TEXT[service]
for name, npc, dialogue, shared, _text, service in re.findall(
        r'new NpcRedirect\("([^"]+)", (\d+), (\d+), (\d+), (\d+), NpcService\.(\w+)\)', source):
    service_of[int(npc)] = SERVICE_TEXT[service] + " (own dialogue; it shared the line of 7 unrelated NPCs)"

# Why a chatter-only NPC is left as it is. By NPC name; anything else gets DEFAULT.
DEFAULT = ("Ambient chatter; nothing in its line or in the data names a service. If it is a quest NPC, its quest options come "
           "from the player's running tasks (LC_TaskHelper.GetCurNpcTaskDialogueInfos), not from DialogueConfig.")
REASON = {}
for names, reason in [
    (["Swordsman Mentor", "Mage Mentor", "Archer Mentor", "Thief Mentor", "Acolyte Mentor", "Merchant Mentor", "Tilpi",
      "Novice Instructor"],
     "Job advancement runs through quests and the Job Advance function (its options are task options); no service to add."),
    (["Life Skill Mentor"],
     "Would open the Life Skill panel, but the menu opens it with an initial table (iOpenType) that an NPC action cannot pass."),
    (["Librarian"],
     "Would open the Codex (function 6000), but how the client opens its panel (chapters, resources) was not found in its code, so no option is guessed."),
    (["Commentator"], "Offers an introduction to the city: a guide the client's tasks drive; no service."),
    (["Airship Staff", "Airship Passenger"], "No airship function in FunctionConfig or ActionConfig."),
    (["Inn Attendant", "Innkeeper"], "No inn or rest function in FunctionConfig."),
    (["Wedding Officiant", "Wedding Assistant"], "No marriage function in FunctionConfig and no marriage code on the server (its only wedding code is the Dracula's Bride dungeon)."),
    (["Kuskel"], "An arena duel scaled to the level: no function in FunctionConfig matches it."),
    (["Jade", "Miss Smile", "Netvia"], "Exchange NPCs (ores, masks, headbands): no verified link to a client exchange panel."),
    (["Crafting Enthusiast"], "An Alchemist; the Craft page is equipment crafting and needs a quest, so it is not what this NPC does."),
    (["Refining Enthusiast"], "Tutorial NPC (the refine smiths are the ones that get the Refine option)."),
    (["Supply Station Staff"], "Vehicle repair: no function for it."),
    (["Guild Services Staff"], "Reward claim of the guild's daily task; the data has no panel for it."),
    (["Party Host"], "Event host (the Bonfire Party), started by the event itself."),
    (["Kafra Voting Campaigner", "Zhunta Voting Campaigner", "Vote for It Campaigner", "Kafra Voting Promoter"],
     "Voting event NPCs."),
    (["Caravan Member", "Caravan Party Leader", "Captain"], "Caravan escort event NPCs; the escort is run by its own system."),
    (["Blacksmith Making Repairs", "Repairman"], "Ambient: a worker, not a smith that offers a service."),
]:
    for n in names:
        REASON[n] = reason

by_npc = {}
for nid, n in npcs.items():
    if not isinstance(n, dict):
        continue
    ids = n.get("_iInteractID")
    if not isinstance(ids, list) or not ids:
        continue
    kind_names = n.get("_kName")
    name = text(kind_names[0]) if isinstance(kind_names, list) and kind_names else "(no name)"
    entry = {"npc": int(nid), "name": name, "dialogues": [], "heads": set(), "missing": False}
    for i in ids:
        record = interactions.get(str(i))
        if not record:
            entry["missing"] = True
            continue
        for a in record["_iEXECAction"]:
            if not a or a[0] == "":
                continue
            if a[0] == "53":
                rows = dialogues.get(a[1], [])
                entry["dialogues"].append((a[1], len(rows), sum(1 for r in rows if r.get("_iTextType") == 2)))
            else:
                entry["heads"].add(action_name(a[0]))
    by_npc[int(nid)] = entry

served, with_options, chatter, own_action, missing, mixed = [], [], [], [], [], []
for e in by_npc.values():
    if e["npc"] in service_of:
        served.append(e)
    elif e["missing"] and not e["dialogues"] and not e["heads"]:
        missing.append(e)
    elif e["dialogues"] and any(o > 0 for _, _, o in e["dialogues"]):
        with_options.append(e)
    elif e["dialogues"] and e["heads"]:
        mixed.append(e)
    elif e["dialogues"]:
        chatter.append(e)
    else:
        own_action.append(e)


def ids(list_):
    return ", ".join(str(e["npc"]) for e in sorted(list_, key=lambda e: e["npc"]))


def group(list_, key):
    out = collections.defaultdict(list)
    for e in list_:
        out[key(e)].append(e)
    return out


lines = []
w = lines.append
w("# NPC catalog: what each NPC with an interaction gets")
w("")
w("Generated by `tools/npc-catalog/npc_catalog.py` from `data/config` and `NpcServiceTable.cs`; do not edit by hand. See "
  "[NPC services](npc-services.md) for how the options are added and why.")
w("")
w(f"{len(by_npc)} NPCs of NPCConfig have an interaction. Their interactions are the client's own actions (portals, pickups, ...) or a "
  "dialogue (action 53, `NPCInteractDialogue`):")
w("")
w("| Group | NPCs | Decision |")
w("|---|---:|---|")
w(f"| [Gets a service](#1-gets-a-service) | {len(served)} | Options added to the dialogue |")
w(f"| [Dialogue that already has options](#2-dialogues-that-already-have-options) | {len(with_options)} | Left: the original options stay |")
w(f"| [Chatter only, left as it is](#3-chatter-only-left-as-it-is) | {len(chatter)} | Left, with the reason below |")
w(f"| [Own client action](#4-own-client-action) | {len(own_action)} | Left: not a dialogue NPC |")
w(f"| [Dialogue plus own action](#5-dialogue-plus-own-action) | {len(mixed)} | Left |")
w(f"| [Interaction missing from the data](#6-interaction-missing-from-the-data) | {len(missing)} | Left: nothing to extend |")
w("")

w("## 1. Gets a service")
w("")
w("| NPC | Ids | Option |")
w("|---|---|---|")
for (name, service), g in sorted(group(served, lambda e: (e["name"], service_of[e["npc"]])).items()):
    w(f"| {name} | {ids(g)} | {service} |")
w("")

w("## 2. Dialogues that already have options")
w("")
w("The data gives these NPCs their own options (Kafra teleport and shop, mentors, quest steps). They are not touched.")
w("")
w("| NPC | Ids |")
w("|---|---|")
for name, g in sorted(group(with_options, lambda e: e["name"]).items()):
    w(f"| {name} | {ids(g)} |")
w("")

w("## 3. Chatter only, left as it is")
w("")
w("A dialogue of one to several lines and no option. Each is left alone for the reason given (the default reason applies when "
  "nothing is listed).")
w("")
w("| NPC | Ids | Reason |")
w("|---|---|---|")
for (name, reason), g in sorted(group(chatter, lambda e: (e["name"], REASON.get(e["name"], DEFAULT))).items()):
    w(f"| {name} | {ids(g)} | {reason} |")
w("")

w("## 4. Own client action")
w("")
w("Portals, gathering spots, chests, music and event objects: their interaction is one of the client's own actions, not a "
  "dialogue, so there is no dialogue to extend. Grouped by action; the first names are examples.")
w("")
w("| Actions | NPCs | Examples |")
w("|---|---:|---|")
for heads, g in sorted(group(own_action, lambda e: ", ".join(sorted(e["heads"])) or "(none)").items(), key=lambda x: -len(x[1])):
    names = collections.Counter(e["name"] for e in g).most_common(6)
    w(f"| {heads} | {len(g)} | " + "; ".join(f"{n} ({c})" if c > 1 else n for n, c in names) + " |")
w("")

w("## 5. Dialogue plus own action")
w("")
for e in sorted(mixed, key=lambda e: e["npc"]):
    w(f"- {e['name']} {e['npc']}: dialogue and {', '.join(sorted(e['heads']))}")
w("")

w("## 6. Interaction missing from the data")
w("")
w("Their `_iInteractID` names an interaction that NPCInteractConfig does not have, so there is nothing to attach options to.")
w("")
w("| NPC | Ids |")
w("|---|---|")
for name, g in sorted(group(missing, lambda e: e["name"]).items()):
    w(f"| {name} | {ids(g)} |")
w("")

OUT.write_text("\n".join(lines), encoding="utf-8", newline="\n")
print(f"wrote {OUT} ({len(lines)} lines): served {len(served)}, with options {len(with_options)}, chatter {len(chatter)}, "
      f"own action {len(own_action)}, mixed {len(mixed)}, missing {len(missing)}")
