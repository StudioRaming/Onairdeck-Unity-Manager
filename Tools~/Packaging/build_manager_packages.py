#!/usr/bin/env python3
"""Builds the Git-free install files of the Atelier Manager (standard library only).

    python Tools~/Packaging/build_manager_packages.py [--out DIR] [--version X.Y.Z]

Writes, into --out (default Tools~/Packaging/out):

  Atelier-Manager.unitypackage             the same bytes without the version, so
                                                   releases/latest/download/Atelier-Manager.unitypackage is a stable link
  Atelier-Manager-<version>.unitypackage   every file at Packages/com.studioraming.manager/<relative path>,
                                                   keeping the GUIDs in the committed .meta files
  com.studioraming.manager-<version>.zip           the package root at the zip root (what the Manager's own
                                                   self-update and PackageZip validation expect)

Excluded: Tools~ and any other folder ending in "~", every dot-folder or dot-file (.git*, .github, .claude),
and the output folder. Every included file and folder must have its committed .meta (a package without
metas is ignored by Unity), otherwise the build stops. Both outputs are reopened and checked before the
script reports success; any failure exits non-zero. Run it from a clean checkout of the release commit.
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import re
import shutil
import sys
import tarfile
import zipfile

PACKAGE_NAME = "com.studioraming.manager"
PATHNAME_PREFIX = "Packages/" + PACKAGE_NAME + "/"
GUID_RE = re.compile(r"^guid:\s*([0-9a-f]{32})\s*$", re.MULTILINE)
ZIP_TIME = (1980, 1, 1, 0, 0, 0)


class BuildError(Exception):
    pass


def repo_root():
    return os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))


def excluded(segment):
    return segment.startswith(".") or segment.endswith("~")


def collect(root, out_dir):
    """Returns (files, folders): sorted relative paths with '/' separators, metas included in files."""
    files, folders = [], []
    out_abs = os.path.abspath(out_dir)
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if not excluded(d) and os.path.abspath(os.path.join(dirpath, d)) != out_abs)
        rel_dir = os.path.relpath(dirpath, root).replace(os.sep, "/")
        if rel_dir != ".":
            folders.append(rel_dir)
        for name in sorted(filenames):
            if excluded(name):
                continue
            rel = name if rel_dir == "." else rel_dir + "/" + name
            files.append(rel)
    return sorted(files), sorted(folders)


def read_guid(path):
    with open(path, "r", encoding="utf-8") as fh:
        match = GUID_RE.search(fh.read())
    if not match:
        raise BuildError("no guid in " + path)
    return match.group(1)


def plan(root, out_dir):
    """Checks the tree and returns the assets: list of (rel_path, guid, is_folder)."""
    files, folders = collect(root, out_dir)
    file_set, folder_set = set(files), set(folders)
    problems = []
    assets = []
    for rel in folders:
        meta = rel + ".meta"
        if meta not in file_set:
            problems.append("folder without .meta: " + rel)
    for rel in files:
        if rel.endswith(".meta"):
            target = rel[:-5]
            if target not in file_set and target not in folder_set:
                problems.append("orphan .meta (its asset is missing): " + rel)
        elif rel + ".meta" not in file_set:
            problems.append("file without .meta: " + rel)
    # The package root has no .meta of its own: Unity treats Packages/<name> as the package.
    if "package.json" not in file_set:
        problems.append("package.json is missing")
    if problems:
        raise BuildError("The tree is not ready to package:\n  " + "\n  ".join(problems))
    guids = {}
    for rel in folders + [f for f in files if not f.endswith(".meta")]:
        guid = read_guid(os.path.join(root, rel.replace("/", os.sep) + ".meta"))
        if guid in guids:
            raise BuildError("GUID " + guid + " is used by both " + guids[guid] + " and " + rel)
        guids[guid] = rel
        assets.append((rel, guid, rel in folder_set))
    return files, assets


def package_version(root):
    with open(os.path.join(root, "package.json"), "r", encoding="utf-8") as fh:
        data = json.load(fh)
    if data.get("name") != PACKAGE_NAME:
        raise BuildError("package.json names " + str(data.get("name")) + ", expected " + PACKAGE_NAME)
    return data["version"]


def build_zip(root, files, path):
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for rel in files:
            info = zipfile.ZipInfo(rel, ZIP_TIME)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            with open(os.path.join(root, rel.replace("/", os.sep)), "rb") as fh:
                archive.writestr(info, fh.read())


def add_tar_file(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = 0
    info.mode = 0o644
    info.uname = info.gname = ""
    tar.addfile(info, io.BytesIO(data))


def build_unitypackage(root, assets, path):
    raw = io.BytesIO()
    with tarfile.open(fileobj=raw, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for rel, guid, is_folder in sorted(assets, key=lambda a: a[1]):
            native = rel.replace("/", os.sep)
            with open(os.path.join(root, native + ".meta"), "rb") as fh:
                add_tar_file(tar, guid + "/asset.meta", fh.read())
            if not is_folder:
                with open(os.path.join(root, native), "rb") as fh:
                    add_tar_file(tar, guid + "/asset", fh.read())
            # Unity writes the path followed by "\n00".
            add_tar_file(tar, guid + "/pathname", (PATHNAME_PREFIX + rel + "\n00").encode("utf-8"))
    with open(path, "wb") as fh:
        with gzip.GzipFile(filename="", mode="wb", fileobj=fh, mtime=0) as gz:
            gz.write(raw.getvalue())


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def unsafe_name(name):
    return (name.startswith("/") or "\\" in name or ":" in name or ".." in name.split("/")
            or any(excluded(part) for part in name.split("/") if part))


def check_zip(root, files, path, version):
    errors = []
    with zipfile.ZipFile(path) as archive:
        if archive.testzip() is not None:
            errors.append("zip: corrupt member " + archive.testzip())
        names = archive.namelist()
        if len(names) != len(set(n.lower() for n in names)):
            errors.append("zip: duplicate (case-insensitive) entries")
        if sorted(names) != sorted(files):
            errors.append("zip: entries differ from the tree: missing=%s extra=%s" % (
                sorted(set(files) - set(names))[:5], sorted(set(names) - set(files))[:5]))
        for name in names:
            if unsafe_name(name):
                errors.append("zip: unsafe or excluded entry " + name)
        if "package.json" not in names:
            errors.append("zip: no package.json at the root")
        else:
            manifest = json.loads(archive.read("package.json").decode("utf-8"))
            if manifest.get("name") != PACKAGE_NAME or manifest.get("version") != version:
                errors.append("zip: package.json says %s %s" % (manifest.get("name"), manifest.get("version")))
        for rel in files:
            if rel in names:
                with open(os.path.join(root, rel.replace("/", os.sep)), "rb") as fh:
                    if sha256(fh.read()) != sha256(archive.read(rel)):
                        errors.append("zip: content differs from the tree: " + rel)
        for rel in files:
            if rel.endswith(".meta") and rel in names:
                twin = root_guid(root, rel)
                if twin != read_guid_bytes(archive.read(rel)):
                    errors.append("zip: GUID differs from the tree: " + rel)
    return errors


def root_guid(root, rel_meta):
    return read_guid(os.path.join(root, rel_meta.replace("/", os.sep)))


def read_guid_bytes(data):
    match = GUID_RE.search(data.decode("utf-8"))
    return match.group(1) if match else None


def check_unitypackage(root, assets, path, version):
    errors = []
    members = {}
    with tarfile.open(path, mode="r:gz") as tar:
        for member in tar.getmembers():
            if not member.isfile():
                errors.append("unitypackage: unexpected non-file member " + member.name)
                continue
            members[member.name] = tar.extractfile(member).read()
    expected = {guid: (rel, is_folder) for rel, guid, is_folder in assets}
    seen = set()
    for name in members:
        parts = name.split("/")
        if len(parts) != 2 or parts[1] not in ("asset", "asset.meta", "pathname"):
            errors.append("unitypackage: unexpected member " + name)
            continue
        seen.add(parts[0])
    if seen != set(expected):
        errors.append("unitypackage: GUID set differs from the tree: missing=%s extra=%s" % (
            sorted(set(expected) - seen)[:5], sorted(seen - set(expected))[:5]))
    pathnames = set()
    for guid, (rel, is_folder) in expected.items():
        pathname = members.get(guid + "/pathname")
        meta = members.get(guid + "/asset.meta")
        asset = members.get(guid + "/asset")
        if pathname is None or meta is None:
            errors.append("unitypackage: " + rel + " lacks pathname or asset.meta")
            continue
        text = pathname.decode("utf-8")
        if text.split("\n")[0] != PATHNAME_PREFIX + rel:
            errors.append("unitypackage: wrong pathname for " + rel + ": " + text)
        if unsafe_name(text.split("\n")[0][len("Packages/"):]):
            errors.append("unitypackage: unsafe or excluded pathname " + text)
        pathnames.add(text.split("\n")[0])
        if read_guid_bytes(meta) != guid:
            errors.append("unitypackage: asset.meta GUID is not the folder GUID for " + rel)
        with open(os.path.join(root, rel.replace("/", os.sep) + ".meta"), "rb") as fh:
            if fh.read() != meta:
                errors.append("unitypackage: asset.meta differs from the tree for " + rel)
        if is_folder:
            if asset is not None:
                errors.append("unitypackage: folder has asset data: " + rel)
        else:
            if asset is None:
                errors.append("unitypackage: file has no asset data: " + rel)
            else:
                with open(os.path.join(root, rel.replace("/", os.sep)), "rb") as fh:
                    if fh.read() != asset:
                        errors.append("unitypackage: content differs from the tree: " + rel)
    if len(pathnames) != len(expected):
        errors.append("unitypackage: duplicate pathnames")
    if PATHNAME_PREFIX + "package.json" not in pathnames:
        errors.append("unitypackage: package.json is not included")
    manifest_guid = next((g for g, (rel, _) in expected.items() if rel == "package.json"), None)
    if manifest_guid and json.loads(members[manifest_guid + "/asset"].decode("utf-8")).get("version") != version:
        errors.append("unitypackage: package.json version differs from " + version)
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", default=None, help="output folder (default Tools~/Packaging/out)")
    parser.add_argument("--version", default=None, help="must equal package.json version when given")
    args = parser.parse_args()

    root = repo_root()
    out_dir = os.path.abspath(args.out or os.path.join(root, "Tools~", "Packaging", "out"))
    try:
        version = package_version(root)
        if args.version and args.version != version:
            raise BuildError("--version %s does not match package.json %s" % (args.version, version))
        files, assets = plan(root, out_dir)
        os.makedirs(out_dir, exist_ok=True)
        zip_path = os.path.join(out_dir, PACKAGE_NAME + "-" + version + ".zip")
        pkg_path = os.path.join(out_dir, "Atelier-Manager-" + version + ".unitypackage")
        build_zip(root, files, zip_path)
        build_unitypackage(root, assets, pkg_path)
        latest_path = os.path.join(out_dir, "Atelier-Manager.unitypackage")
        shutil.copyfile(pkg_path, latest_path)
        errors = check_zip(root, files, zip_path, version) + check_unitypackage(root, assets, pkg_path, version)
        with open(pkg_path, "rb") as a, open(latest_path, "rb") as b:
            if a.read() != b.read():
                errors.append("version-less unitypackage differs from the versioned one")
    except BuildError as error:
        print("BUILD FAILED: " + str(error))
        return 1
    if errors:
        print("SELF-CHECK FAILED:\n  " + "\n  ".join(errors))
        return 1
    for path in (zip_path, pkg_path, latest_path):
        with open(path, "rb") as fh:
            digest = sha256(fh.read())
        print("%s  %d bytes  sha256 %s" % (path, os.path.getsize(path), digest))
    print("Self-check passed: %d files in the zip, %d assets (%d folders) in the unitypackage." % (
        len(files), len(assets), sum(1 for a in assets if a[2])))
    return 0


if __name__ == "__main__":
    sys.exit(main())
