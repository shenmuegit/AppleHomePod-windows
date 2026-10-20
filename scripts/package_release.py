"""Create a reproducible native candidate from explicit, matching release evidence."""

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import uuid
import zipfile
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).absolute().parents[1]
SOURCE_DOCUMENTS = (
    "LICENSE", "LICENSE-GPLv3", "README.md", "docs/first-run.md", "docs/COMPATIBILITY.md",
)
NOTICES = "desktop/AirFlash.App/Assets/ThirdPartyNotices.txt"


def safe_path(root, value):
    path = Path(value)
    path = Path(os.path.abspath(path if path.is_absolute() else root / path))
    if not path.is_relative_to(root):
        raise ValueError(f"Path is outside the project: {path}")
    for ancestor in (path, *path.parents):
        try:
            attributes = ancestor.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(attributes.st_mode) or (
            getattr(attributes, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        ):
            raise ValueError(f"Refusing linked release path: {ancestor}")
    return path


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def git(root, *arguments, input_data=None):
    result = subprocess.run(
        ["git", "--no-replace-objects", "-C", str(root), *arguments],
        input=input_data, capture_output=True, check=True,
    )
    return result.stdout


def clean_revision(root, revision):
    if not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("source_revision must be a full lowercase commit hash")
    if Path(git(root, "rev-parse", "--show-toplevel").decode().strip()) != root:
        raise ValueError("source_root must be the checkout root")
    if git(root, "rev-parse", "HEAD").decode().strip() != revision:
        raise ValueError("source_revision does not match checkout HEAD")
    if git(root, "status", "--porcelain=v1", "--untracked-files=all").strip():
        raise ValueError("Packaging requires a clean tracked and untracked source tree")


def source_blobs(root, revision):
    entries = []
    for record in git(root, "ls-tree", "-rz", revision).split(b"\0"):
        if not record:
            continue
        metadata, name_bytes = record.split(b"\t", 1)
        mode, kind, oid = metadata.decode().split()
        name = name_bytes.decode("utf-8")
        path = PurePosixPath(name)
        if mode not in ("100644", "100755") or kind != "blob":
            raise ValueError(f"Unsupported source entry: {name}")
        if path.is_absolute() or ".." in path.parts or "\\" in name:
            raise ValueError(f"Unsafe source entry: {name}")
        if (
            path.name == "AGENTS.md" or path.name.startswith(".env")
            or path.suffix.lower() in (".key", ".pem", ".pfx", ".dpapi", ".dmp")
            or path.parts[0] in (".git", ".runtime", ".worktrees", "artifacts", "dist")
            or name.startswith((".impeccable/questions/", ".impeccable/review/",
                                ".impeccable/mocks/", ".impeccable/build/"))
        ):
            raise ValueError(f"Refusing private or work-data source entry: {name}")
        entries.append((name, mode, oid))
    data = git(root, "cat-file", "--batch", input_data="".join(
        f"{oid}\n" for _, _, oid in entries
    ).encode())
    blobs = {}
    offset = 0
    for name, mode, oid in entries:
        end = data.index(b"\n", offset)
        actual_oid, kind, size = data[offset:end].decode().split()
        if actual_oid != oid or kind != "blob":
            raise ValueError(f"Invalid source blob: {name}")
        offset = end + 1
        blobs[name] = (data[offset:offset + int(size)], int(mode, 8))
        offset += int(size) + 1
    for name in (*SOURCE_DOCUMENTS, NOTICES):
        if name not in blobs:
            raise ValueError(f"Missing source/license document: {name}")
    return blobs


def zip_info(name, mode=0o100644):
    info = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
    info.create_system = 3
    info.external_attr = mode << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    return info


def json_bytes(value):
    return (json.dumps(value, sort_keys=True, ensure_ascii=False, indent=2) + "\n").encode()


def package_release(args):
    root = Path(os.path.abspath(args.source_root))
    safe_path(root, root)
    if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", args.version):
        raise ValueError("version must be canonical X.Y.Z")
    output_root = safe_path(root, args.output_root)
    delivery = safe_path(root, output_root / f"{args.version}-{args.source_revision}")
    if os.path.lexists(delivery):
        raise ValueError(f"Release delivery already exists: {delivery}")
    clean_revision(root, args.source_revision)
    paths = {
        "AppleHome.exe": safe_path(root, args.exe),
        "provenance.json": safe_path(root, args.provenance),
        "validation.json": safe_path(root, args.validation),
        "vendor/VBCABLE-original.zip": safe_path(root, args.vendor_archive),
        "vendor/VB-CABLE-original-readme.txt": safe_path(root, args.vendor_readme),
    }
    for path in paths.values():
        if not path.is_file():
            raise ValueError(f"Release input is not a regular file: {path}")
    provenance = json.loads(paths["provenance.json"].read_text(encoding="utf-8-sig"))
    validation = json.loads(paths["validation.json"].read_text(encoding="utf-8-sig"))
    expected_hashes = {
        "exe_sha256": sha256(paths["AppleHome.exe"]),
        "vendor_archive_sha256": sha256(paths["vendor/VBCABLE-original.zip"]),
        "vendor_readme_sha256": sha256(paths["vendor/VB-CABLE-original-readme.txt"]),
    }
    for name, document in (("provenance", provenance), ("validation", validation)):
        if not isinstance(document, dict) or document.get("schema") != 1:
            raise ValueError(f"{name} must use schema 1")
        if document.get("source_commit") != args.source_revision:
            raise ValueError(f"{name}.source_commit does not match source_revision")
        for key in (expected_hashes if name == "provenance" else ("exe_sha256",)):
            if document.get(key) != expected_hashes[key]:
                raise ValueError(f"{name}.{key} does not match release input")
    if not re.fullmatch(r"[0-9a-f]{64}", str(provenance.get("engine_sha256", ""))):
        raise ValueError("provenance.engine_sha256 must identify the built engine")
    checks = validation.get("software_checks")
    if not isinstance(checks, dict) or not checks or any(
        not isinstance(value, dict) or value.get("status") != "passed" for value in checks.values()
    ):
        raise ValueError("validation.software_checks must explicitly report passed checks")
    qualification = validation.get("qualification")
    if not isinstance(qualification, dict) or not qualification:
        raise ValueError("validation.qualification must explicitly describe hardware limits")
    blobs = source_blobs(root, args.source_revision)
    stage = safe_path(root, Path("artifacts/package-release") / uuid.uuid4().hex)
    stage.mkdir(parents=True)
    for name, source in paths.items():
        target = stage / name
        target.parent.mkdir(parents=True, exist_ok=True)
        with source.open("rb") as incoming, target.open("xb") as outgoing:
            shutil.copyfileobj(incoming, outgoing)
    for key, name in (
        ("exe_sha256", "AppleHome.exe"),
        ("vendor_archive_sha256", "vendor/VBCABLE-original.zip"),
        ("vendor_readme_sha256", "vendor/VB-CABLE-original-readme.txt"),
    ):
        if sha256(stage / name) != expected_hashes[key]:
            raise ValueError(f"{key} changed during packaging")
    if json.loads((stage / "provenance.json").read_text(encoding="utf-8-sig")) != provenance or (
        json.loads((stage / "validation.json").read_text(encoding="utf-8-sig")) != validation
    ):
        raise ValueError("Release evidence changed during packaging")
    for name in SOURCE_DOCUMENTS:
        target = stage / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(blobs[name][0])
    (stage / "ThirdPartyNotices.txt").write_bytes(blobs[NOTICES][0])
    source_name = f"AppleHome-source-{args.source_revision[:12]}.zip"
    with zipfile.ZipFile(stage / source_name, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, (content, mode) in sorted(blobs.items()):
            archive.writestr(zip_info(name, mode), content)
    files = {
        path.relative_to(stage).as_posix(): sha256(path)
        for path in sorted(stage.rglob("*")) if path.is_file()
    }
    manifest = {
        "schema": 1, "product": "AppleHome", "version": args.version,
        "source_commit": args.source_revision, **expected_hashes,
        "engine_sha256": provenance["engine_sha256"], "source_blob_count": len(blobs),
        "software_checks": checks, "qualification": qualification, "files": files,
    }
    (stage / "manifest.json").write_bytes(json_bytes(manifest))
    files["manifest.json"] = sha256(stage / "manifest.json")
    (stage / "SHA256SUMS.txt").write_text(
        "".join(f"{value}  {name}\n" for name, value in sorted(files.items())),
        encoding="utf-8", newline="\n",
    )
    files["SHA256SUMS.txt"] = sha256(stage / "SHA256SUMS.txt")
    archive_name = f"AppleHome-{args.version}-{args.source_revision[:12]}.zip"
    with zipfile.ZipFile(stage / archive_name, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in sorted(files):
            with (stage / name).open("rb") as source, archive.open(zip_info(name), "w") as target:
                shutil.copyfileobj(source, target)
    archive_hash = sha256(stage / archive_name)
    (stage / f"{archive_name}.sha256").write_text(
        f"{archive_hash}  {archive_name}\n", encoding="utf-8", newline="\n",
    )
    clean_revision(root, args.source_revision)
    safe_path(root, delivery)
    output_root.mkdir(parents=True, exist_ok=True)
    if os.path.lexists(delivery):
        raise ValueError(f"Release delivery already exists: {delivery}")
    os.rename(stage, delivery)
    return {"ok": True, "delivery": delivery.relative_to(root).as_posix(),
            "archive_sha256": archive_hash, "exe_sha256": expected_hashes["exe_sha256"]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, default=ROOT)
    parser.add_argument("--output-root", type=Path, default=Path("dist"))
    for name in ("version", "source-revision", "exe", "provenance", "validation",
                 "vendor-archive", "vendor-readme"):
        parser.add_argument(f"--{name}", required=True)
    args = parser.parse_args()
    try:
        result = package_release(args)
    except (ValueError, OSError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Packaging failed: {error}\n")
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
