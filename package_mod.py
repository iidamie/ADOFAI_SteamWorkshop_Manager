#!/usr/bin/env python3
from __future__ import annotations

import hashlib
import json
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parent
PROJECT = ROOT / "SteamWorkshopManager.csproj"
OUTPUT = ROOT / "bin" / "Release" / "net10.0"
DIST = ROOT / "dist"
MOD_FOLDER = "SteamWorkshopManager"
EXCLUDED = {"StArray.ModManager.dll", "StArray.ModManager.Android.dll", "ImGui.NET.dll"}


def main() -> None:
    subprocess.run(["dotnet", "build", str(PROJECT), "--configuration", "Release"], cwd=ROOT, check=True)
    version = ET.parse(PROJECT).findtext("./PropertyGroup/Version")
    if not version:
        raise ValueError("Project version is missing")
    deps_name = MOD_FOLDER + ".deps.json"
    deps = json.loads((OUTPUT / deps_name).read_text())
    target = deps["targets"][deps["runtimeTarget"]["name"]]
    libraries = {
        PurePosixPath(name).name
        for library in target.values()
        for name in library.get("runtime", {})
        if name.endswith(".dll") and PurePosixPath(name).name not in EXCLUDED
    }
    required = {MOD_FOLDER + ".dll", "SteamKit2.dll", "SteamKitSDK.dll", "StbImageSharp.dll"}
    if not required.issubset(libraries):
        raise ValueError("Build dependency manifest is incomplete")
    files = {name: OUTPUT / name for name in sorted(libraries)}
    files[deps_name] = OUTPUT / deps_name
    for name in ["README.md", "THIRD-PARTY-NOTICES.md"]:
        files[name] = ROOT / name
    for path in sorted((ROOT / "licenses").iterdir()):
        if path.is_file() and path.suffix.lower() in {".txt", ".md"}:
            files["licenses/" + path.name] = path
    manifest = {
        "format": 1,
        "id": MOD_FOLDER,
        "version": version,
        "files": [
            {"path": name, "size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
            for name, path in sorted(files.items())
        ],
    }
    DIST.mkdir(exist_ok=True)
    versioned = DIST / f"{MOD_FOLDER}-{version}.zip"
    with zipfile.ZipFile(versioned, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, path in sorted(files.items()):
            archive.write(path, MOD_FOLDER + "/" + name)
        archive.writestr(MOD_FOLDER + "/update-manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    shutil.copyfile(versioned, DIST / (MOD_FOLDER + ".zip"))
    checksum = hashlib.sha256(versioned.read_bytes()).hexdigest()
    versioned.with_suffix(".zip.sha256").write_text(f"{checksum}  {versioned.name}\n")
    print(f"Created {versioned}")


if __name__ == "__main__":
    main()
