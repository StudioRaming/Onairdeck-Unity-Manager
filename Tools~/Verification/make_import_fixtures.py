"""Create two tiny unitypackages for the isolated Editor verification project."""
import io
import json
from pathlib import Path
import sys
import tarfile
import uuid


def record(archive, name, content):
    data = content.encode("utf-8")
    item = tarfile.TarInfo(name)
    item.size = len(data)
    item.mode = 0o644
    archive.addfile(item, io.BytesIO(data))


def package(destination, asset_path, content):
    with tarfile.open(destination, "w:gz") as archive:
        # Include only the two fixture assets. Unity creates missing parent folders;
        # this avoids replacing metadata on an existing shared download directory.
        guid = uuid.uuid4().hex
        directory = tarfile.TarInfo(guid)
        directory.type = tarfile.DIRTYPE
        directory.mode = 0o755
        archive.addfile(directory)
        record(archive, f"{guid}/asset", content)
        importer = "MonoImporter" if asset_path.endswith(".cs") else "TextScriptImporter"
        record(archive, f"{guid}/asset.meta", f"fileFormatVersion: 2\nguid: {guid}\n{importer}:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n")
        record(archive, f"{guid}/pathname", asset_path)


def main():
    project = Path(sys.argv[1]).resolve()
    if not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        raise SystemExit("Pass an existing isolated Unity project directory.")
    identifier = uuid.uuid4().hex
    root = f"Assets/StudioRaming_Onairdeck/__Verification_{identifier}"
    marker = f"Marker_{identifier}"
    cache = project / "Library/OnAirDeckCache"
    first = cache / f"verification-{identifier}-script"
    second = cache / f"verification-{identifier}-text"
    first.mkdir(parents=True)
    second.mkdir(parents=True)
    script_package = first / "ScriptReload.unitypackage"
    text_package = second / "AfterReload.unitypackage"
    package(script_package, f"{root}/Editor/{marker}.cs", f"namespace OnAirDeckVerificationFixtures {{ public static class {marker} {{ }} }}\n")
    package(text_package, f"{root}/after-reload.txt", "OnAirDeck package queue verification\n")
    work = project / "Library/OnAirDeckVerification"
    work.mkdir(parents=True, exist_ok=True)
    fixture = {
        "root": root,
        "markerType": f"OnAirDeckVerificationFixtures.{marker}",
        "textAsset": f"{root}/after-reload.txt",
        "packages": [str(script_package), str(text_package)],
    }
    (work / "fixtures.json").write_text(json.dumps(fixture, indent=2), encoding="utf-8")
    print(f"Prepared script-reload and text packages in {work}")


if __name__ == "__main__":
    main()
