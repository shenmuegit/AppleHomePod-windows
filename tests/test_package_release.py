"""Package tiny committed fixtures without compilers, installers, or hardware."""

import hashlib
import json
import os
import subprocess
import sys
import zipfile
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]


def digest(data):
    return hashlib.sha256(data).hexdigest()


class PackageFixture:
    def __init__(self, root):
        self.root = root
        root.mkdir()
        self.git("init", "--initial-branch=main")
        self.git("config", "user.name", "Package fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        files = {
            ".gitignore": "artifacts/\ndist/\n.runtime/\n",
            "LICENSE": "Fixture copyright and GPL notice\n",
            "LICENSE-GPLv3": "Fixture GPL full text\n",
            "README.md": "Fixture instructions\n",
            "docs/first-run.md": "Fixture setup instructions\n",
            "docs/COMPATIBILITY.md": "Hardware remains explicitly unverified\n",
            "desktop/AirFlash.App/Assets/ThirdPartyNotices.txt": "Fixture vendor notices\n",
            "source.py": "print('fixture source')\n",
        }
        for name, content in files.items():
            path = root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(content, encoding="utf-8", newline="\n")
        self.commit()
        inputs = root / "artifacts/inputs"
        inputs.mkdir(parents=True)
        self.exe = inputs / "built app.exe"
        self.exe.write_bytes(b"MZ fixture executable, never run")
        self.vendor = inputs / "original vendor.zip"
        with zipfile.ZipFile(self.vendor, "w") as archive:
            archive.writestr("readme.txt", "Fixture vendor terms")
        self.readme = inputs / "vendor readme.txt"
        self.readme.write_text("Fixture vendor terms", encoding="utf-8")
        self.provenance = {
            "schema": 1,
            "source_commit": self.revision,
            "exe_sha256": digest(self.exe.read_bytes()),
            "engine_sha256": "a" * 64,
            "vendor_archive_sha256": digest(self.vendor.read_bytes()),
            "vendor_readme_sha256": digest(self.readme.read_bytes()),
        }
        self.validation = {
            "schema": 1,
            "source_commit": self.revision,
            "exe_sha256": self.provenance["exe_sha256"],
            "software_checks": {"fixture regression": {"status": "passed", "passed": 1}},
            "qualification": {
                "current_homepod": "pending_hardware",
                "win11": "pending_hardware",
                "network_disconnect": "not_tested",
            },
        }

    def git(self, *arguments, input_data=None):
        result = subprocess.run(
            ["git", "-c", "core.autocrlf=false", "-c", "commit.gpgsign=false", *arguments],
            cwd=self.root,
            input=input_data,
            capture_output=True,
            check=True,
        )
        return result.stdout.decode().strip()

    def commit(self):
        self.git("add", ".")
        self.git("commit", "-m", "Fixture source")
        self.revision = self.git("rev-parse", "HEAD")

    @property
    def delivery(self):
        return self.root / "dist" / f"1.2.3-{self.revision}"

    def run(self, *extra):
        provenance = self.root / "artifacts/inputs/provenance.json"
        validation = self.root / "artifacts/inputs/validation.json"
        provenance.write_text(json.dumps(self.provenance), encoding="utf-8")
        validation.write_text(json.dumps(self.validation), encoding="utf-8")
        return subprocess.run(
            [
                sys.executable, "-m", "scripts.package_release",
                "--source-root", str(self.root), "--version", "1.2.3",
                "--source-revision", self.revision, "--exe", str(self.exe),
                "--provenance", str(provenance), "--validation", str(validation),
                "--vendor-archive", str(self.vendor), "--vendor-readme", str(self.readme),
                *extra,
            ],
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            timeout=20,
        )


@pytest.fixture
def package(tmp_path):
    return PackageFixture(tmp_path / "source checkout")


def test_package_contains_bound_source_licenses_evidence_and_verified_hashes(package):
    historical = package.root / "dist/previous-release.zip"
    historical.parent.mkdir()
    historical.write_bytes(b"historical delivery")
    result = package.run()
    assert result.returncode == 0, result.stderr
    delivery = package.delivery
    manifest = json.loads((delivery / "manifest.json").read_text())
    assert manifest["source_commit"] == package.revision
    assert manifest["exe_sha256"] == package.provenance["exe_sha256"]
    assert manifest["qualification"] == package.validation["qualification"]
    assert historical.read_bytes() == b"historical delivery"
    for name, sha256 in manifest["files"].items():
        assert digest((delivery / name).read_bytes()) == sha256
    for name in ("LICENSE", "LICENSE-GPLv3", "ThirdPartyNotices.txt", "validation.json"):
        assert name in manifest["files"]
    source_name = f"AppleHome-source-{package.revision[:12]}.zip"
    with zipfile.ZipFile(delivery / source_name) as source:
        assert source.read("source.py") == b"print('fixture source')\n"
        assert not any(name.startswith(("artifacts/", "dist/")) for name in source.namelist())
    sums = (delivery / "SHA256SUMS.txt").read_text().splitlines()
    for line in sums:
        sha256, name = line.split("  ", 1)
        assert digest((delivery / name).read_bytes()) == sha256
    archive = delivery / f"AppleHome-1.2.3-{package.revision[:12]}.zip"
    with zipfile.ZipFile(archive) as release:
        assert release.read("AppleHome.exe") == package.exe.read_bytes()
        assert "manifest.json" in release.namelist()


def test_identical_inputs_make_identical_archives_in_another_new_output_root(package):
    first = package.run()
    assert first.returncode == 0, first.stderr
    second = package.run("--output-root", "artifacts/reproduced")
    assert second.returncode == 0, second.stderr
    name = f"AppleHome-1.2.3-{package.revision[:12]}.zip"
    reproduced = package.root / "artifacts/reproduced" / package.delivery.name
    assert (package.delivery / name).read_bytes() == (reproduced / name).read_bytes()


@pytest.mark.parametrize("has_archive", [False, True])
def test_existing_delivery_is_never_replaced_even_if_empty(package, has_archive):
    package.delivery.mkdir(parents=True)
    if has_archive:
        (package.delivery / "historical.zip").write_bytes(b"preserve this release")
    before = {path.name: path.read_bytes() for path in package.delivery.iterdir()}
    result = package.run()
    assert result.returncode != 0
    assert "already exists" in result.stderr
    assert {path.name: path.read_bytes() for path in package.delivery.iterdir()} == before


@pytest.mark.parametrize("untracked", [False, True])
def test_dirty_sources_fail_without_creating_a_delivery(package, untracked):
    target = package.root / ("untracked.txt" if untracked else "source.py")
    target.write_text("uncommitted source", encoding="utf-8")
    result = package.run()
    assert result.returncode != 0
    assert "clean" in result.stderr
    assert not package.delivery.exists()


@pytest.mark.parametrize(
    "document, field, value",
    [
        ("provenance", "source_commit", "b" * 40),
        ("validation", "source_commit", "b" * 40),
        ("provenance", "exe_sha256", "b" * 64),
        ("validation", "exe_sha256", "b" * 64),
        ("provenance", "vendor_archive_sha256", "b" * 64),
        ("provenance", "vendor_readme_sha256", "b" * 64),
    ],
)
def test_mismatched_inputs_are_rejected_before_output_creation(package, document, field, value):
    getattr(package, document)[field] = value
    result = package.run()
    assert result.returncode != 0
    assert field in result.stderr
    assert not package.delivery.exists()


def test_failed_software_validation_does_not_produce_a_release(package):
    package.validation["software_checks"]["fixture regression"]["status"] = "failed"
    result = package.run()
    assert result.returncode != 0
    assert "software_checks" in result.stderr
    assert not package.delivery.exists()


def test_external_output_is_rejected_without_writing(package):
    outside = package.root.parent / "outside checkout"
    result = package.run("--output-root", str(outside))
    assert result.returncode != 0
    assert "outside" in result.stderr
    assert not outside.exists()


@pytest.mark.skipif(os.name != "nt", reason="Windows directory junction regression")
def test_output_junction_is_rejected_without_writing_through_it(package):
    target = package.root.parent / "junction target"
    target.mkdir()
    link = package.root / "dist"
    subprocess.run(
        ["cmd", "/c", "mklink", "/J", str(link), str(target)],
        capture_output=True, check=True,
    )
    result = package.run()
    assert result.returncode != 0
    assert "linked" in result.stderr
    assert list(target.iterdir()) == []


def test_private_tracked_files_are_rejected_instead_of_silently_leaked(package):
    (package.root / ".env.private").write_text("fixture only, no secret", encoding="utf-8")
    package.commit()
    package.provenance["source_commit"] = package.revision
    package.validation["source_commit"] = package.revision
    result = package.run()
    assert result.returncode != 0
    assert "private" in result.stderr
    assert not package.delivery.exists()


def test_source_archive_uses_original_commit_blobs_despite_git_replace(package):
    original = package.git("rev-parse", "HEAD:source.py")
    replacement = package.git("hash-object", "-w", "--stdin", input_data=b"replacement bytes\n")
    package.git("replace", original, replacement)
    result = package.run()
    assert result.returncode == 0, result.stderr
    source_name = f"AppleHome-source-{package.revision[:12]}.zip"
    with zipfile.ZipFile(package.delivery / source_name) as source:
        assert source.read("source.py") == b"print('fixture source')\n"
