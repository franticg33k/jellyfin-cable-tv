#!/usr/bin/env python3
"""Builds the preset channel lineups from Nostalgex's channel definitions.

Nostalgex (https://github.com/chadMueller/Nostalgex, MIT) defines ~130 channels as rule-based filters over a media
library, plus a curated TMDB membership list. This script converts them into Cable TV "Rule" sources and writes
src/Jellyfin.Plugin.CableTv/Packs/Presets/nostalgex.json, which the plugin offers under "Suggest channels".

    git clone --depth 1 https://github.com/chadMueller/Nostalgex /tmp/nostalgex
    python3 scripts/nostalgex_presets.py /tmp/nostalgex
"""

import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "src/Jellyfin.Plugin.CableTv/Packs/Presets/nostalgex.json"
LICENSE_OUT = ROOT / "src/Jellyfin.Plugin.CableTv/Packs/Presets/NOSTALGEX-LICENSE.txt"

# Channels that need per-user watch history ("rewatched": watched three times), which a server-wide channel can't use.
UNSUPPORTED_RULES = {"rewatched", "watchedOnly", "unwatchedOnly"}

KEEP_UPPER = {"TV", "TV+", "HBO", "LOL", "Y2K", "DVD", "VHS", "HQ", "R", "MTV"}
MINOR = {"of", "the", "a", "an", "and", "in", "on"}


def title_case(name):
    """'SAT MORNING CARTOONS' -> 'Sat Morning Cartoons', keeping acronyms and decades ('90s')."""
    words = []
    for index, word in enumerate(name.split(" ")):
        if word.upper() in KEEP_UPPER:
            words.append(word.upper())
        elif index > 0 and word.lower() in MINOR:
            words.append(word.lower())
        elif re.fullmatch(r"\d+(S|ST|ND|RD|TH)", word):
            words.append(word.lower())
        else:
            parts = re.split(r"([-'])", word.lower())
            out = []
            for i, part in enumerate(parts):
                # Capitalize after a hyphen, not after an apostrophe ("Santa's", "Rock'n").
                if part and part not in "-'" and (i == 0 or parts[i - 1] == "-"):
                    part = part[0].upper() + part[1:]
                out.append(part)
            words.append("".join(out))
    return " ".join(words)


def rename(name):
    # Nostalgex's own brand stays with Nostalgex.
    return re.sub(r"^NOSTALGEX\b", "RETRO", name).strip()


def item_types(rules):
    if rules.get("source") == "musicVideo":
        return ["MusicVideo"]
    return {"movie": ["Movie"], "episode": ["Episode"]}.get(rules.get("type"), ["Episode", "Movie"])


def convert_rule(channel, members, exclusive_rules):
    r = channel["rules"]
    genres = r.get("genres") or {}
    rule = {
        "Genres": genres.get("include", []),
        "RequireAllGenres": genres.get("requireAll", []),
        "ExcludeGenres": list(genres.get("exclude", [])),
        "Keywords": r.get("keywords", []),
        "KeywordGenres": r.get("keywordsRequireAnyGenre", []),
        "KeywordGatedGenres": r.get("keywordGatedGenres", []),
        "ExcludeKeywords": list(r.get("keywordsExclude", [])),
        "TitleWords": r.get("titleContains", []),
        "ExcludeTitleWords": list(r.get("titleExcludes", [])),
        "AlwaysTitles": r.get("editorialOverrides", []),
        "EpisodeWords": r.get("episodeTitleContains", []),
        "Studios": r.get("studios", []),
        # Jellyfin keeps TV networks and production companies together, as studios.
        "Companies": sorted(set(r.get("networks", [])) | set(r.get("productionCompanies", [])), key=str.lower),
        "Ratings": r.get("contentRatings", []),
        "AllowUnrated": bool(r.get("allowUnrated")),
        "YearFrom": (r.get("yearRange") or {}).get("min"),
        "YearTo": (r.get("yearRange") or {}).get("max"),
        # Jellyfin's community rating stands in for both Plex's audience rating and IMDb's. Vote counts aren't kept.
        "MinRating": r.get("imdbRatingMin", r.get("ratingMin")),
        "MinMinutes": (r.get("durationRange") or {}).get("min"),
        "MaxMinutes": (r.get("durationRange") or {}).get("max"),
        "AddedWithinDays": r.get("addedWithinDays"),
        "ReleasedWithinMonths": r.get("releasedWithinMonths"),
        "Kids": channel.get("category") == "kids",
        "LockGenres": True,
        "Members": members,
        "MembersOnly": bool(r.get("manifestOnly")),
    }

    # Titles another channel owns (holiday films, Halloween, anime) stay off this one.
    for ex in exclusive_rules:
        ids = ex.get("channelIDs") or ([ex["channelID"]] if "channelID" in ex else [])
        if channel["id"] in ids:
            continue
        rule["ExcludeGenres"] += [g for g in ex.get("genres", []) if g not in rule["ExcludeGenres"]]
        rule["ExcludeTitleWords"] += [t for t in ex.get("titleContains", []) if t not in rule["ExcludeTitleWords"]]

    # Stand-up specials belong to the stand-up channel, not every comedy channel.
    if channel["id"] != 58 and any("comedy" in g.lower() for g in rule["Genres"] + rule["RequireAllGenres"]):
        if "stand-up comedy" not in rule["ExcludeKeywords"]:
            rule["ExcludeKeywords"].append("stand-up comedy")

    return {k: v for k, v in rule.items() if v not in (None, [], False)}


def sources(channel, rule):
    blocked = (channel.get("timeRestrictions") or {}).get("blockRatings")
    if not blocked:
        return [{"Type": "Rule", "Rule": rule}]
    # "No R before 9pm": the rule without those ratings, plus the rule limited to them, late at night.
    late = dict(rule, Ratings=[x for x in blocked["ratings"] if not rule.get("Ratings") or x in rule["Ratings"]])
    early = dict(rule, ExcludeRatings=sorted(set(rule.get("ExcludeRatings", [])) | set(blocked["ratings"])))
    start = int(blocked.get("blockBefore", 21))
    return [
        {"Type": "Rule", "Rule": early},
        {"Type": "Rule", "Rule": late, "AirHours": f"{start:02d}:00-06:00"},
    ]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("nostalgex", type=pathlib.Path, help="Nostalgex checkout")
    args = parser.parse_args()

    config = json.loads((args.nostalgex / "channels.json").read_text(encoding="utf-8"))
    manifest = json.loads((args.nostalgex / "channels-memberships.json").read_text(encoding="utf-8"))
    license_text = (args.nostalgex / "LICENSE").read_text(encoding="utf-8")

    members = {}
    for key, entry in manifest["items"].items():
        for channel_id in entry["channels"]:
            members.setdefault(channel_id, []).append(key)

    channels = {c["id"]: c for c in config["channels"]}
    bundles = []
    skipped = []
    for bundle in config["bundles"]:
        out = []
        for channel_id in bundle["channelIDs"]:
            channel = channels.get(channel_id)
            if channel is None:
                continue
            if UNSUPPORTED_RULES & set(channel["rules"]):
                skipped.append(channel["name"])
                continue
            keys = sorted(members.get(channel_id, []), key=lambda k: (k.split(":")[0], int(k.split(":")[1])))
            rule = convert_rule(channel, keys, config.get("exclusiveRules", []))
            out.append({
                "Number": str(channel["number"]),
                "Name": title_case(rename(channel["name"])),
                "MinItems": channel.get("minItems", 3),
                "ItemTypes": item_types(channel["rules"]),
                "Sources": sources(channel, rule),
            })
        out.sort(key=lambda c: int(c["Number"]))
        bundles.append({
            "Id": bundle["id"],
            "Name": title_case(rename(bundle["name"])),
            "Description": bundle.get("description", ""),
            "Channels": out,
        })

    bundles.sort(key=lambda b: min((int(c["Number"]) for c in b["Channels"]), default=0))
    data = {
        "Name": "Nostalgex lineups",
        "Source": "https://github.com/chadMueller/Nostalgex",
        "SourceVersion": config.get("version"),
        "License": license_text.strip(),
        "Bundles": bundles,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(data, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    LICENSE_OUT.write_text(license_text, encoding="utf-8")
    total = sum(len(b["Channels"]) for b in bundles)
    print(f"Wrote {OUT.relative_to(ROOT)}: {total} channels in {len(bundles)} bundles; skipped {', '.join(skipped) or 'none'}")


if __name__ == "__main__":
    sys.exit(main())
