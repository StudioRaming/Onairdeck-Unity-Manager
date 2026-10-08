"""Prepare uniquely named import destinations for real Unity Editor verification."""
import json
from pathlib import Path
import sys
import uuid


def main():
    project = Path(sys.argv[1]).resolve()
    if not (project / "ProjectSettings/ProjectVersion.txt").is_file():
        raise SystemExit("Pass an existing Unity test project directory.")
    identifier = uuid.uuid4().hex
    root = f"Assets/StudioRaming_Onairdeck/__Verification_{identifier}"
    marker = f"Marker_{identifier}"
    cache = project / "Library/OnAirDeckCache"
    work = project / "Library/OnAirDeckVerification"
    work.mkdir(parents=True, exist_ok=True)
    fixture = {
        "root": root,
        "markerType": f"OnAirDeckVerificationFixtures.{marker}",
        "textAsset": f"{root}/after-reload.txt",
        "packages": [str(cache / f"verification-{identifier}-script/ScriptReload.unitypackage"), str(cache / f"verification-{identifier}-text/AfterReload.unitypackage")],
    }
    (work / "fixtures.json").write_text(json.dumps(fixture, indent=2), encoding="utf-8")
    print(f"Prepared import destinations in {work}; Unity exports the fixtures during verification.")


if __name__ == "__main__":
    main()