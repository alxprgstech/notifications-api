"""Isolated routing tests. No Asterisk, audio or network is required."""

import importlib.util
from pathlib import Path
import sys
from tempfile import TemporaryDirectory
from types import ModuleType
import unittest
from unittest.mock import patch


def load(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


with patch.dict(sys.modules, {"aiohttp": ModuleType("aiohttp"), "audioop": ModuleType("audioop")}):
    bridge = load("bridge", "bridge.py")
render = load("render_assistant_route", "render-assistant-route.py").render
migration = load("migrate_assistant_files", "migrate-assistant-files.py")


class Routing(unittest.TestCase):
    def test_legacy_and_new_extensions_keep_the_trusted_context(self):
        routes = bridge.assistant_routes("7820", "8260,8261", "trusted-fixture", "PJSIP/fixture")
        self.assertEqual(set(routes), {"7820", "8260", "8261"})
        self.assertEqual(set(routes.values()), {("trusted-fixture", "PJSIP/fixture")})
        with self.assertRaises(ValueError):
            bridge.assistant_routes("8260", "8260,8261", "trusted-fixture", "PJSIP/fixture")

    def test_incoming_route_requires_exact_context_extension_and_endpoint(self):
        ari = object.__new__(bridge.Ari)
        ari.assistant_routes = bridge.assistant_routes("", "8260,8261", "trusted-fixture", "PJSIP/fixture")
        for number in ("8260", "8261"):
            channel = {"dialplan": {"context": "trusted-fixture", "exten": number},
                       "name": "PJSIP/fixture-deadbeef", "state": "Ring"}
            event = {"args": ["phone-assistant", number], "channel": channel}
            self.assertTrue(ari.trusted_incoming(event))
            self.assertFalse(ari.trusted_incoming({**event, "args": ["phone-assistant", "8262"]}))
            self.assertFalse(ari.trusted_incoming({**event, "channel": {**channel, "name": "PJSIP/other-deadbeef"}}))
            channel["dialplan"]["context"] = "anonymous"
            self.assertFalse(ari.trusted_incoming(event))

    def test_dialplan_dry_run_preserves_ip_restriction(self):
        for number in ("8260", "8261"):
            result = render(number, "192.168.10.9")
            self.assertIn(f"Stasis(alxprgs-notify,phone-assistant,{number})", result)
            self.assertIn('= "192.168.10.9"', result)
            self.assertIn("Hangup(21)", result)
        with self.assertRaises(ValueError):
            render("8260", "203.0.113.2")


class MigrationRehearsal(unittest.TestCase):
    def setUp(self):
        temporary = TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        directory = Path(temporary.name).resolve()
        self.site = directory / "site"
        self.bundle = directory / "private-backup"
        self.original = {
            "dialplan": b"[trusted]; production-style fixture\r\nexten => 7820,1,Hangup()\r\n[other]\r\n",
            "environment": b"ASTERISK_ENDPOINT=PJSIP/fixture\nASTERISK_ASSISTANT_CONTEXT=trusted\n"
                           b"ASTERISK_ASSISTANT_EXTENSION=7820\nARI_PASSWORD=synthetic\n\n",
            "bridge": b"# legacy fixture\n",
        }
        for name, data in self.original.items():
            path = self.site / migration.FILES[name]
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)

    def stage(self, numbers):
        migration.stage(self.site, self.bundle, "trusted", "192.168.10.9", numbers)

    def test_stage_apply_and_idempotent_rollback_preserve_legacy(self):
        self.stage(["8260", "8261"])
        self.assertEqual(migration.read_site(self.site), self.original)
        self.assertEqual(set(migration.reconcile(self.site, self.bundle, "after").values()), {"after"})
        applied = migration.read_site(self.site)
        self.assertIn(b"exten => 7820,1,Hangup()\r\n", applied["dialplan"])
        self.assertIn(b"ASTERISK_ASSISTANT_EXTENSION=7820\n", applied["environment"])
        self.assertIn(b"ARI_PASSWORD=synthetic\n\n", applied["environment"])
        for number in (b"8260", b"8261"):
            self.assertIn(b"Stasis(alxprgs-notify,phone-assistant," + number + b")\r\n", applied["dialplan"])
        for _ in range(2):
            self.assertEqual(set(migration.reconcile(self.site, self.bundle, "before").values()), {"before"})
            self.assertEqual(migration.read_site(self.site), self.original)

    def test_partial_application_can_be_restored(self):
        self.stage(["8260", "8261"])
        replace = migration.atomic_replace

        def fail_on_second_file(path, data):
            if path.name == "asterisk.env":
                raise OSError("fixture write failure")
            replace(path, data)

        with patch.object(migration, "atomic_replace", fail_on_second_file):
            with self.assertRaises(OSError):
                migration.reconcile(self.site, self.bundle, "after")
        self.assertEqual(migration.reconcile(self.site, self.bundle),
                         {"dialplan": "after", "environment": "before", "bridge": "before"})
        migration.reconcile(self.site, self.bundle, "before")
        self.assertEqual(migration.read_site(self.site), self.original)

    def test_rollback_stops_before_writing_when_an_outside_edit_exists(self):
        self.stage(["8260", "8261"])
        migration.reconcile(self.site, self.bundle, "after")
        (self.site / migration.FILES["environment"]).write_bytes(b"# operator edit\n")
        edited = migration.read_site(self.site)
        with self.assertRaisesRegex(ValueError, "Unexpected change"):
            migration.reconcile(self.site, self.bundle, "before")
        self.assertEqual(migration.read_site(self.site), edited)

    def test_existing_extension_and_modified_backup_are_refused(self):
        with self.assertRaisesRegex(ValueError, "Existing SIP extension"):
            self.stage(["7820"])
        self.assertFalse(self.bundle.exists())
        self.stage(["8261"])
        (self.bundle / "dialplan.after").write_bytes(b"invalid backup")
        with self.assertRaisesRegex(ValueError, "checksum"):
            migration.reconcile(self.site, self.bundle, "after")
        self.assertEqual(migration.read_site(self.site), self.original)


if __name__ == "__main__":
    unittest.main()
