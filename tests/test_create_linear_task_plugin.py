import importlib.util
import io
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile


ROOT = Path(__file__).resolve().parent.parent
PLUGIN = ROOT / "plugins" / "create-linear-task"
MARKETPLACE = ROOT / ".agents" / "plugins" / "marketplace.json"
SYNC_SCRIPT = PLUGIN / "scripts" / "sync_plugin.py"
PACKAGE_SCRIPT = PLUGIN / "scripts" / "package_plugin.py"
SYNC_SPEC = importlib.util.spec_from_file_location("plugin_synchronizer", SYNC_SCRIPT)
synchronizer = importlib.util.module_from_spec(SYNC_SPEC)
sys.modules[SYNC_SPEC.name] = synchronizer
SYNC_SPEC.loader.exec_module(synchronizer)
sys.modules["sync_plugin"] = synchronizer
PACKAGE_SPEC = importlib.util.spec_from_file_location("plugin_packager", PACKAGE_SCRIPT)
packager = importlib.util.module_from_spec(PACKAGE_SPEC)
sys.modules[PACKAGE_SPEC.name] = packager
PACKAGE_SPEC.loader.exec_module(packager)


class CreateLinearTaskPluginTests(unittest.TestCase):
    def test_repository_marketplace_references_the_plugin_package(self):
        marketplace = json.loads(MARKETPLACE.read_text())

        self.assertEqual(marketplace["name"], "agentic-workflows")
        self.assertEqual(
            marketplace["interface"], {"displayName": "Agentic Workflows"}
        )
        self.assertEqual(len(marketplace["plugins"]), 1)
        entry = marketplace["plugins"][0]
        self.assertEqual(entry["name"], "create-linear-task")
        self.assertEqual(
            entry["source"],
            {
                "source": "local",
                "path": "./plugins/create-linear-task",
            },
        )
        self.assertEqual(
            entry["policy"],
            {
                "installation": "AVAILABLE",
                "authentication": "ON_INSTALL",
            },
        )
        self.assertEqual(entry["category"], "Productivity")

        source_path = PurePosixPath(entry["source"]["path"])
        self.assertFalse(source_path.is_absolute())
        self.assertNotIn("..", source_path.parts)
        plugin_root = ROOT.joinpath(*source_path.parts)
        self.assertEqual(plugin_root, PLUGIN)
        self.assertTrue((plugin_root / "plugin.json").is_file())
        self.assertTrue((plugin_root / ".codex-plugin" / "plugin.json").is_file())
        self.assertTrue(
            (plugin_root / "skills" / "create-linear-task" / "SKILL.md").is_file()
        )
        plugin_manifest = json.loads((plugin_root / "plugin.json").read_text())
        self.assertEqual(plugin_manifest["name"], entry["name"])

    def test_portable_manifest_describes_a_skills_only_plugin(self):
        manifest = json.loads((PLUGIN / "plugin.json").read_text())

        self.assertEqual(
            manifest["$schema"],
            "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
        )
        self.assertEqual(manifest["name"], "create-linear-task")
        self.assertRegex(manifest["version"], r"^\d+\.\d+\.\d+$")
        self.assertIn("interface", manifest["extensions"]["com.openai"])
        self.assertNotIn("mcpServers", manifest)
        self.assertNotIn("apps", manifest["extensions"]["com.openai"])
        self.assertFalse((PLUGIN / "mcp.json").exists())
        self.assertFalse((PLUGIN / ".mcp.json").exists())

    def test_generated_plugin_files_match_the_canonical_skill(self):
        completed = subprocess.run(
            [sys.executable, "-B", str(SYNC_SCRIPT), "--check"],
            cwd=ROOT,
            text=True,
            capture_output=True,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr)

        portable = json.loads((PLUGIN / "plugin.json").read_text())
        compatibility = json.loads(
            (PLUGIN / ".codex-plugin" / "plugin.json").read_text()
        )
        for field in ("name", "version", "description", "author", "homepage", "repository"):
            self.assertEqual(compatibility[field], portable[field])
        self.assertEqual(compatibility["skills"], "./skills/")
        self.assertEqual(
            compatibility["interface"],
            portable["extensions"]["com.openai"]["interface"],
        )

    def test_sync_check_detects_bundled_skill_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            bundled = Path(directory) / "create-linear-task"
            shutil.copytree(PLUGIN / "skills" / "create-linear-task", bundled)
            skill = bundled / "SKILL.md"
            skill.write_text(skill.read_text() + "\nDrift.\n")

            with mock.patch.object(synchronizer, "BUNDLED_SKILL", bundled):
                differences = synchronizer.check_generated_files(
                    synchronizer.load_portable_manifest()
                )

        self.assertIn("bundled skill file differs: SKILL.md", differences)

    def test_package_archive_is_deterministic_and_contains_only_runtime_files(self):
        with tempfile.TemporaryDirectory() as directory:
            first = Path(directory) / "first.zip"
            second = Path(directory) / "second.zip"
            for output in (first, second):
                completed = subprocess.run(
                    [
                        sys.executable,
                        "-B",
                        str(PACKAGE_SCRIPT),
                        "--output",
                        str(output),
                    ],
                    cwd=ROOT,
                    text=True,
                    capture_output=True,
                )
                self.assertEqual(completed.returncode, 0, completed.stderr)

            self.assertEqual(first.read_bytes(), second.read_bytes())
            with zipfile.ZipFile(first) as archive:
                names = archive.namelist()
                self.assertEqual(names, sorted(names))
                self.assertIn("plugin.json", names)
                self.assertIn(".codex-plugin/plugin.json", names)
                self.assertIn("skills/create-linear-task/SKILL.md", names)
                self.assertIn(
                    "skills/create-linear-task/scripts/prepare_issue.py", names
                )
                self.assertNotIn("README.md", names)
                self.assertFalse(any(name.startswith("scripts/") for name in names))
                self.assertTrue(
                    all(item.date_time == (1980, 1, 1, 0, 0, 0) for item in archive.infolist())
                )

            completed = subprocess.run(
                [
                    sys.executable,
                    "-B",
                    str(PACKAGE_SCRIPT),
                    "--output",
                    str(first),
                    "--check",
                ],
                cwd=ROOT,
                text=True,
                capture_output=True,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr)

    def test_ignored_skill_cache_files_cannot_affect_the_archive(self):
        with tempfile.TemporaryDirectory() as directory:
            bundled = Path(directory) / "create-linear-task"
            shutil.copytree(PLUGIN / "skills" / "create-linear-task", bundled)

            with mock.patch.object(synchronizer, "BUNDLED_SKILL", bundled):
                before = packager.build_archive()
                cache = bundled / "scripts" / "__pycache__" / "generated.pyc"
                cache.parent.mkdir()
                cache.write_bytes(b"host-specific cache contents")
                after = packager.build_archive()

        self.assertEqual(after, before)
        with zipfile.ZipFile(io.BytesIO(after)) as archive:
            self.assertFalse(
                any(
                    "__pycache__" in name or name.endswith((".pyc", ".pyo"))
                    for name in archive.namelist()
                )
            )


if __name__ == "__main__":
    unittest.main()
