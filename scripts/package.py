#!/usr/bin/env python3
"""Build the installable plugin zip and a Jellyfin plugin repository manifest.

    python3 scripts/package.py [--base-url URL]

Writes dist/cabletv_<version>.zip (the DLL plus meta.json, ready to unzip into
<jellyfin config>/plugins/) and dist/manifest.json, a plugin repository manifest whose
download link is <base-url>/cabletv_<version>.zip.
"""
import argparse
import datetime
import hashlib
import json
import pathlib
import re
import subprocess
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
PROJECT = ROOT / "src" / "Jellyfin.Plugin.CableTv"
DIST = ROOT / "dist"
DEFAULT_BASE_URL = "https://raw.githubusercontent.com/franticg33k/jellyfin-cable-tv/main/dist"


def build_yaml():
    """Read the flat keys of build.yaml without needing a YAML library."""
    data = {}
    text = (ROOT / "build.yaml").read_text()
    for key in ("name", "guid", "version", "targetAbi", "overview", "category", "owner"):
        match = re.search(rf'^{key}:\s*"?([^"\n]*)"?\s*$', text, re.M)
        data[key] = match.group(1).strip() if match else ""
    for key in ("description", "changelog"):
        match = re.search(rf"^{key}:\s*>\s*\n((?:[ \t]+.*\n?)+)", text, re.M)
        data[key] = " ".join(line.strip() for line in match.group(1).splitlines()).strip() if match else ""
    return data


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="where the zip will be downloadable from")
    args = parser.parse_args()

    meta = build_yaml()
    version = meta["version"]
    out = ROOT / "artifacts" / "plugin"
    subprocess.run(
        ["dotnet", "publish", str(PROJECT), "-c", "Release", "-o", str(out),
         f"-p:AssemblyVersion={version}", f"-p:FileVersion={version}"],
        check=True)

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    plugin_meta = {
        "category": meta["category"],
        "changelog": meta["changelog"],
        "description": meta["description"],
        "guid": meta["guid"],
        "name": meta["name"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "targetAbi": meta["targetAbi"],
        "timestamp": timestamp,
        "version": version,
        "status": "Active",
        "autoUpdate": True,
        "imagePath": "",
        "assemblies": [],
    }

    DIST.mkdir(exist_ok=True)
    for old in DIST.glob("cabletv_*.zip"):
        old.unlink()
    zip_name = f"cabletv_{version}.zip"
    zip_path = DIST / zip_name
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(out / "Jellyfin.Plugin.CableTv.dll", "Jellyfin.Plugin.CableTv.dll")
        archive.writestr("meta.json", json.dumps(plugin_meta, indent=2))

    checksum = hashlib.md5(zip_path.read_bytes()).hexdigest()
    manifest = [{
        "guid": meta["guid"],
        "name": meta["name"],
        "description": meta["description"],
        "overview": meta["overview"],
        "owner": meta["owner"],
        "category": meta["category"],
        "imageUrl": "",
        "versions": [{
            "version": version,
            "changelog": meta["changelog"],
            "targetAbi": meta["targetAbi"],
            "sourceUrl": f"{args.base_url.rstrip('/')}/{zip_name}",
            "checksum": checksum,
            "timestamp": timestamp,
        }],
    }]
    (DIST / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"{zip_path.relative_to(ROOT)}  md5 {checksum}")


if __name__ == "__main__":
    main()
