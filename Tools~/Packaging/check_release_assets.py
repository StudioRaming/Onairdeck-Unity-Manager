#!/usr/bin/env python3
"""Checks a published GitHub release of the Atelier Manager before plugin-version points at it (standard library + gh CLI).

    python Tools~/Packaging/check_release_assets.py <tag> [--repo StudioRaming/Onairdeck-Unity-Manager]

A first install must always end on the latest version (owner decision 2026-10-31): the website links the version-less
releases/latest/download/Atelier-Manager.unitypackage, so every release must carry it again, byte for byte the versioned one,
and be the repository's "Latest" release. The Manager then updates itself from plugin-version's download_url (the zip) or git_url.

Fails (exit 1) unless:
  - Atelier-Manager-<tag>.unitypackage, Atelier-Manager.unitypackage and com.studioraming.manager-<tag>.zip are all attached;
  - the version-less .unitypackage has the same size and sha256 digest as the versioned one;
  - the release is not a draft or pre-release and is the one releases/latest resolves to.
Prints the values the plugin_releases row (client unity-manager, line atelier) should carry.
"""
import argparse
import json
import subprocess
import sys


def gh_json(*args):
    out = subprocess.run(["gh", "api", *args], check=True, capture_output=True).stdout
    return json.loads(out.decode("utf-8"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("tag")
    parser.add_argument("--repo", default="StudioRaming/Onairdeck-Unity-Manager")
    args = parser.parse_args()
    tag = args.tag
    release = gh_json(f"repos/{args.repo}/releases/tags/{tag}")
    latest = gh_json(f"repos/{args.repo}/releases/latest")
    assets = {a["name"]: a for a in release.get("assets", [])}
    errors = []

    versioned = f"Atelier-Manager-{tag}.unitypackage"
    stable = "Atelier-Manager.unitypackage"
    zipname = f"com.studioraming.manager-{tag}.zip"
    for name in (versioned, stable, zipname):
        if name not in assets:
            errors.append(f"missing asset {name}")
    if versioned in assets and stable in assets:
        a, b = assets[versioned], assets[stable]
        if a["size"] != b["size"] or (a.get("digest") or "") != (b.get("digest") or "") or not a.get("digest"):
            errors.append(f"{stable} is not the same file as {versioned} (size {b['size']} vs {a['size']}, digest {b.get('digest')} vs {a.get('digest')})")
    if release.get("draft") or release.get("prerelease"):
        errors.append("the release is a draft or a pre-release, so releases/latest does not point at it")
    if latest.get("tag_name") != tag:
        errors.append(f"releases/latest is {latest.get('tag_name')}, not {tag}")

    base = f"https://github.com/{args.repo}"
    print("plugin_releases row for client unity-manager, line atelier:")
    print(f"  version       {tag}")
    print(f"  git_url       {base}.git#{tag}")
    print(f"  download_url  {base}/releases/download/{tag}/{zipname}")
    if zipname in assets and assets[zipname].get("digest"):
        print(f"  zip digest    {assets[zipname]['digest']}  (for a future sha256 column)")
    print("  published     true once this check passes; min_supported_version only to force an update")

    if errors:
        for e in errors:
            print("FAIL " + e)
        return 1
    print(f"OK {tag}: three assets, {stable} equals {versioned}, and it is the latest release.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
