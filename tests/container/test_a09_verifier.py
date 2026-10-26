#!/usr/bin/env python3
"""Behavioral safety tests. All Docker, .NET and OpenSSL commands are fakes."""
import json
import os
from pathlib import Path
import re
import signal
import socket
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "tests/container/a09-secret-boundary.sh"
REQUIRED = {
    "ManagedSecretStoreTests": [
        "Missing_root_certificate_does_not_prevent_boot_and_flowplane_operations_fail_safely",
        "Managed_secrets_are_isolated_encrypted_and_survive_restart_with_same_root",
        "Wrong_missing_tampered_or_moved_protection_material_fails_without_plaintext_disclosure",
    ],
    "ProviderInstancePersistenceTests": [
        "Environment_reference_round_trips_with_optimistic_concurrency_without_persisting_value",
        "Provider_instance_and_owned_secret_lifecycle_is_atomic_and_reference_preserving",
        "Failed_provider_creation_does_not_leave_orphaned_managed_secret",
    ],
}


def fake(tool, args):
    path = Path(os.environ["A09_FAKE_STATE"])
    state = json.loads(path.read_text())
    case = state["case"]
    output, code = "", 0
    state["calls"].append([tool] + args)

    def resources(kind):
        return state[kind]

    def labels(kind, logical):
        key = {"container": "service", "network": "network", "volume": "volume"}[kind]
        return {"com.docker.compose.project": state["project"],
                "com.docker.compose." + key: logical, **state.get("run_labels", {})}

    def add(kind, logical, preexisting=False):
        name = state["project"] + ("_postgres-data" if kind == "volume" else
                                    "_default" if kind == "network" else "-" + logical + "-1")
        ident = name if kind == "volume" else ("pre-" if preexisting else "new-") + kind + "-" + logical
        item = {"Id": ident, "Name": name, "Labels": labels(kind, logical),
                "preexisting": preexisting}
        if kind == "container":
            item.update(Config={"Labels": item["Labels"], "User": f"{os.getuid()}:{os.getgid()}"},
                        State={"Running": True, "Health": {"Status": "healthy"}},
                        Mounts=([{"Type": "volume", "Name": state["project"] + "_postgres-data",
                                  "Source": "/fake/test-volume", "Destination": "/var/lib/postgresql", "RW": True}]
                                if logical == "postgres" else
                                [{"Type": "bind", "Source": state["certificate"],
                                  "Destination": "/run/secrets/flowplane_root_certificate", "RW": False}]),
                        NetworkSettings={"Networks": {state["project"] + "_default": {}}})
        elif kind == "network":
            item.update(Containers={})
        else:
            item.update(Driver="local", Mountpoint="/fake/test-volume", CreatedAt="2026-10-25T00:00:00Z")
            if case == "missing_volume_identity":
                item.pop("CreatedAt")
        resources(kind)[ident] = item
        return item

    if tool == "openssl":
        if "-keyout" in args:
            Path(args[args.index("-keyout") + 1]).write_text("synthetic-private-key")
        if "-out" in args:
            Path(args[args.index("-out") + 1]).write_text("synthetic-certificate")
    elif tool == "dotnet":
        if "test" in args:
            if case == "gate_b_approval" and os.environ.get("FLOWPLANE_TEST_DISCOVERY_HOSTING_MIGRATION_APPROVED") != "true":
                path.write_text(json.dumps(state))
                return 42
            if "--results-directory" in args:
                directory = Path(args[args.index("--results-directory") + 1])
                directory.mkdir(parents=True, exist_ok=True)
                if case != "missing_trx":
                    result = directory / "a09.trx"
                    if case in ("empty_trx", "malformed_trx"):
                        result.write_text("" if case == "empty_trx" else "<not-xml")
                    else:
                        suites = {p.stem for p in (ROOT / "tests/integration/FlowPlane.Persistence.Tests").glob("*Tests.cs")}
                        if case == "missing_suite":
                            suites.remove("ManagedSecretStoreTests")
                        entries = [(s, method) for s in sorted(suites)
                                   for method in REQUIRED.get(s, ["Fixture"])]
                        if case == "zero_tests":
                            entries = []
                        tree = ET.Element("TestRun", xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010")
                        defs = ET.SubElement(tree, "TestDefinitions")
                        results = ET.SubElement(tree, "Results")
                        bad = case in ("skipped", "inconclusive", "cancelled", "failed_test")
                        for i, (suite, method) in enumerate(entries):
                            test = ET.SubElement(defs, "UnitTest", id=str(i), name=method)
                            ET.SubElement(test, "TestMethod", className="FlowPlane.Persistence.Tests." + suite, name=method)
                            outcome = ({"skipped": "NotExecuted", "inconclusive": "Inconclusive",
                                        "cancelled": "Aborted", "failed_test": "Failed"}.get(case, "Passed")
                                       if i == 0 else "Passed")
                            ET.SubElement(results, "UnitTestResult", testId=str(i), executionId="exec-" + str(i), outcome=outcome)
                        summary = ET.SubElement(tree, "ResultSummary", outcome="Completed")
                        total = len(entries)
                        counters = dict(total=str(total), executed=str(total - (case == "skipped")),
                                        passed=str(total - bad), failed=str(int(case == "failed_test")),
                                        notExecuted=str(int(case == "skipped")),
                                        inconclusive=str(int(case == "inconclusive")), aborted=str(int(case == "cancelled")))
                        if case == "dishonest_counters":
                            counters["passed"] = str(total + 1)
                        ET.SubElement(summary, "Counters", **counters)
                        ET.ElementTree(tree).write(result, encoding="utf-8", xml_declaration=True)
            if case in ("changed_labels", "missing_labels", "replaced_volume", "shared_volume", "shared_network", "cleanup_failure", "null_labels", "test_and_null_labels", "changed_container_labels", "missing_network_labels"):
                volume = next(iter(state["volume"].values()))
                if case == "changed_labels":
                    volume["Labels"]["com.docker.compose.project"] = "foreign-project"
                elif case == "missing_labels":
                    volume["Labels"].pop("com.docker.compose.volume", None)
                elif case == "replaced_volume":
                    volume["CreatedAt"] = "2026-10-26T00:00:00Z"
                elif case == "shared_volume":
                    state["shared_volume"] = True
                elif case == "shared_network":
                    state["shared_network"] = True
                elif case in ("null_labels", "test_and_null_labels"):
                    volume["Labels"] = None
                elif case == "changed_container_labels":
                    item = next(iter(state["container"].values()))
                    item["Labels"]["com.docker.compose.project"] = "foreign-project"
                    item["Config"]["Labels"]["com.docker.compose.project"] = "foreign-project"
                elif case == "missing_network_labels":
                    next(iter(state["network"].values()))["Labels"] = {}
            if case in ("process_failure", "test_and_null_labels", "test_and_pfx_cleanup_failure", "test_and_trx_cleanup_failure"):
                code = 23
                if case == "test_and_pfx_cleanup_failure":
                    Path(state["certificate"]).parent.chmod(0o500)
                if case == "test_and_trx_cleanup_failure":
                    directory.chmod(0o500)
            elif case == "test_and_cleanup_failure":
                code = 23
                state["cleanup_failure"] = True
            output = "synthetic-password-canary-must-not-be-public\n"
    elif tool == "docker":
        if args[:2] == ["--host", "unix:///var/run/docker.sock"]:
            args = args[2:]
        if args[0] == "compose":
            project = args[args.index("--project-name") + 1] if "--project-name" in args else "flowplane"
            if project != "flowplane" and "project" not in state:
                state["project"] = project
                state["certificate"] = os.environ["FLOWPLANE_SECRET_ROOT_CERTIFICATE_FILE"]
                files = [args[i + 1] for i, a in enumerate(args) if a in ("-f", "--file")]
                for f in files:
                    if f.endswith(".json"):
                        overlay = json.loads(Path(f).read_text())
                        state["run_labels"] = overlay["services"]["backend"].get("labels", {})
                if case.startswith("collision_"):
                    kind = case.removeprefix("collision_")
                    add(kind, {"container": "postgres", "network": "default", "volume": "postgres-data"}[kind], True)
                if case == "unrelated_volume":
                    state["volume"]["flowplane_postgres-data"] = {
                        "Id": "flowplane_postgres-data", "Name": "flowplane_postgres-data",
                        "Labels": {"com.docker.compose.project": "flowplane"}, "preexisting": True,
                        "Driver": "local", "Mountpoint": "/fake/development", "CreatedAt": "old"}
            if "config" in args:
                certificate = os.environ.get("FLOWPLANE_SECRET_ROOT_CERTIFICATE_FILE", "/fake/absent")
                config = {"name": project,
                          "services": {"postgres": {"ports": [{"host_ip": "127.0.0.1", "published": os.environ.get("FLOWPLANE_POSTGRES_PORT", "15449"), "target": 5432}],
                                                    "environment": {"POSTGRES_DB": "flowplane", "POSTGRES_USER": "flowplane", "POSTGRES_PASSWORD": os.environ.get("FLOWPLANE_POSTGRES_PASSWORD", "test")},
                                                    "volumes": [{"type": "volume", "source": "postgres-data", "target": "/var/lib/postgresql"}]},
                                       "backend": {"user": f"{os.getuid()}:{os.getgid()}", "environment": {"FlowPlane__Secrets__RootCertificatePath": "/run/secrets/flowplane_root_certificate", "FlowPlane__PostgreSql__Host": "postgres", "FlowPlane__PostgreSql__Port": "5432", "FlowPlane__PostgreSql__Database": "flowplane", "FlowPlane__PostgreSql__Username": "flowplane", "FlowPlane__PostgreSql__Password": os.environ.get("FLOWPLANE_POSTGRES_PASSWORD", "test")}, "secrets": [{"source": "flowplane_root_certificate", "target": "flowplane_root_certificate"}]},
                                       "nginx": {}, "frontend-dev": {}, "keycloak": {}},
                          "volumes": {"postgres-data": {"name": project + "_postgres-data"}},
                          "networks": {"default": {"name": project + "_default"}},
                          "secrets": {"flowplane_root_certificate": {"file": certificate}}}
                if case == "external_volume":
                    config["volumes"]["postgres-data"] = {"name": "flowplane_postgres-data", "external": True}
                if case == "wrong_project":
                    config["name"] = "flowplane"
                if case == "wrong_certificate":
                    config["secrets"]["flowplane_root_certificate"]["file"] = "/home/amin/.local/state/flowplane/secrets/flowplane-root.pfx"
                output = json.dumps(config)
            elif "up" in args:
                state["started"] = True
                if not state["network"]:
                    add("network", "default")
                if not any(r["Name"] == project + "_postgres-data" for r in state["volume"].values()):
                    add("volume", "postgres-data")
                service = args[-1]
                add("container", service)
                if case == "signal_during_start":
                    path.write_text(json.dumps(state))
                    os.kill(os.getppid(), signal.SIGTERM)
                    return 0  # Do not race the terminating CLI by rewriting state.
                if case == "partial_start_failure":
                    code = 18
            elif "run" in args:
                output = "readable-readonly"
            elif "down" in args:
                state["unsafe_down"] = True
                for kind in ("container", "network", "volume"):
                    if kind != "volume" or "--volumes" in args or "-v" in args:
                        for ident in list(state[kind]):
                            state["removed"].append([kind, ident])
                            del state[kind][ident]
        elif args[0] in ("container", "network", "volume"):
            kind, action = args[:2]
            items = resources(kind)
            if action == "ls":
                listed = list(items.values())
                filters = [args[i + 1] for i, a in enumerate(args) if a == "--filter"]
                for f in filters:
                    if f.startswith("label="):
                        key, value = f[6:].split("=", 1)
                        listed = [r for r in listed if (r.get("Labels") or {}).get(key) == value]
                    if f.startswith("volume="):
                        listed = []
                        if state.get("shared_volume"):
                            listed = [{"Id": "foreign-container", "Name": "foreign-container", "Labels": {}}]
                if "--quiet" in args or "-q" in args:
                    output = "\n".join(r["Name"] if kind == "volume" else r["Id"] for r in listed)
                else:
                    output = "\n".join(json.dumps({"ID": r["Id"], "Name": r["Name"], "Names": r["Name"]}) for r in listed)
            elif action == "inspect":
                item = next((r for r in items.values() if args[-1] in (r["Id"], r["Name"])), None)
                if item is None:
                    code = 1
                else:
                    item = json.loads(json.dumps(item))
                    if kind == "network":
                        item["Containers"] = {r["Id"]: {} for r in state["container"].values()}
                        if state.get("shared_network"):
                            item["Containers"]["foreign-container"] = {}
                    output = json.dumps([item])
            elif action == "rm":
                ident = args[-1]
                if case == "cleanup_failure" or state.get("cleanup_failure"):
                    code = 1
                elif ident in items:
                    state["removed"].append([kind, ident])
                    del items[ident]
                else:
                    code = 1
        elif args[0] == "exec":
            output = "root-mount-verified"
            if case == "late_unknown_resource":
                item = json.loads(json.dumps(next(iter(state["volume"].values()))))
                item["Name"] = item["Id"] = state["project"] + "_unrecognized"
                item["Labels"]["com.docker.compose.volume"] = "unrecognized"
                state["volume"][item["Id"]] = item
        elif args[0] == "inspect":
            output = "/run/secrets/flowplane_root_certificate:false"
    path.write_text(json.dumps(state))
    print(output, end="\n" if output else "")
    return code


class VerifierSafetyTests(unittest.TestCase):
    def test_latest_isolated_schema_approval_is_forwarded_without_adopting_an_external_target(self):
        result, state = self.invoke("gate_b_approval")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotEqual(state["project"], "flowplane")
        self.assertEqual(state["volume"], {})

    def invoke(self, case="success", extra=None):
        with tempfile.TemporaryDirectory(prefix="a09-safety-test-") as directory:
            d = Path(directory)
            state_path = d / "state.json"
            state_path.write_text(json.dumps({"case": case, "container": {}, "network": {}, "volume": {}, "calls": [], "removed": []}))
            bin_dir = d / "bin"
            bin_dir.mkdir()
            for tool in ("docker", "dotnet", "openssl"):
                launcher = bin_dir / tool
                launcher.write_text(f'#!/bin/sh\nexec "{sys.executable}" "{Path(__file__).resolve()}" --fake {tool} "$@"\n')
                launcher.chmod(0o700)
            env = {k: v for k, v in os.environ.items() if not k.startswith(("FLOWPLANE_", "FlowPlane__", "ConnectionStrings__", "COMPOSE_"))}
            env.update(PATH=str(bin_dir) + os.pathsep + env["PATH"], A09_FAKE_STATE=str(state_path), TMPDIR=directory)
            env.update(extra or {})
            completed = subprocess.run(["sh", str(SCRIPT)], env=env, cwd=ROOT, capture_output=True, text=True, timeout=30)
            state = json.loads(state_path.read_text())
            # Test-created private artifacts are removed only inside this owned temporary tree.
            return completed, state

    def test_fresh_invocations_use_different_non_pid_identities(self):
        a, first = self.invoke()
        b, second = self.invoke()
        self.assertEqual(a.returncode, 0, a.stderr)
        self.assertEqual(b.returncode, 0, b.stderr)
        for state in (first, second):
            suffix = state["project"].removeprefix("flowplane-a09-secret-verification-")
            self.assertRegex(suffix, r"^[0-9a-f]{32}$")
            self.assertNotEqual(state["project"], "flowplane")
        self.assertNotEqual(first["project"], second["project"])

    def test_preexisting_project_resources_prevent_start_and_deletion(self):
        for kind in ("container", "network", "volume"):
            with self.subTest(kind=kind):
                result, state = self.invoke("collision_" + kind)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(state.get("started"))
                self.assertEqual(state["removed"], [])

    def test_inherited_conflicting_settings_fail_before_resources(self):
        for extra in ({"FLOWPLANE_POSTGRES_PORT": "5432"}, {"FLOWPLANE_POSTGRES_DB": "normal_dev"},
                      {"FLOWPLANE_SECRET_ROOT_CERTIFICATE_FILE": "/real/root.pfx"},
                      {"ConnectionStrings__FlowPlane": "must-not-be-used"}, {"COMPOSE_FILE": "/real/compose.yml"},
                      {"DOCKER_HOST": "tcp://remote.invalid:2375"}, {"DOCKER_CONTEXT": "remote"}):
            with self.subTest(extra=tuple(extra)):
                result, state = self.invoke(extra=extra)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(state.get("started"))
                self.assertEqual(state["removed"], [])

    def test_configuration_cannot_use_external_volume_wrong_project_or_root(self):
        for case in ("external_volume", "wrong_project", "wrong_certificate"):
            with self.subTest(case=case):
                result, state = self.invoke(case)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(state.get("started"))
                self.assertEqual(state["removed"], [])

    def test_changed_missing_or_replaced_volume_identity_blocks_its_removal(self):
        for case in ("changed_labels", "missing_labels", "replaced_volume"):
            with self.subTest(case=case):
                result, state = self.invoke(case)
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(state["volume"])
                self.assertFalse(any(kind == "volume" for kind, _ in state["removed"]))
                self.assertIn("cleanup", result.stderr.lower())

    def test_volume_or_network_used_by_another_project_is_preserved(self):
        for case, kind in (("shared_volume", "volume"), ("shared_network", "network")):
            with self.subTest(case=case):
                result, state = self.invoke(case)
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(state[kind])
                self.assertFalse(any(k == kind for k, _ in state["removed"]))

    def test_process_failure_survives_successful_cleanup(self):
        result, state = self.invoke("process_failure")
        self.assertEqual(result.returncode, 23)
        self.assertFalse(state["volume"])
        self.assertNotIn("verification passed", result.stdout.lower())

    def test_cleanup_failure_is_independent_and_preserves_original_test_failure(self):
        good_tests, _ = self.invoke("cleanup_failure")
        failed_tests, _ = self.invoke("test_and_cleanup_failure")
        self.assertNotEqual(good_tests.returncode, 0)
        self.assertEqual(failed_tests.returncode, 23)
        self.assertIn("cleanup", good_tests.stderr.lower())
        self.assertIn("cleanup", failed_tests.stderr.lower())

    def test_nonpassing_or_inconsistent_TRX_is_never_accepted(self):
        for case in ("skipped", "inconclusive", "cancelled", "failed_test", "missing_trx", "empty_trx",
                     "malformed_trx", "zero_tests", "dishonest_counters", "missing_suite"):
            with self.subTest(case=case):
                result, _ = self.invoke(case)
                self.assertNotEqual(result.returncode, 0)
                self.assertNotIn("verification passed", result.stdout.lower())

    def test_success_reports_counts_without_canaries_or_broad_cleanup(self):
        result, state = self.invoke()
        self.assertEqual(result.returncode, 0, result.stderr)
        # New persistence suites add a synthetic row; retain exact count verification.
        suites = (ROOT / "tests/integration/FlowPlane.Persistence.Tests").glob("*Tests.cs")
        expected = sum(len(REQUIRED.get(suite.stem, ["Fixture"])) for suite in suites)
        self.assertGreater(expected, 0)
        self.assertIn(f"passed={expected} failed=0 skipped=0", result.stdout)
        self.assertNotIn("synthetic-password-canary", result.stdout + result.stderr)
        self.assertFalse(state.get("unsafe_down"))
        self.assertEqual(state["volume"], {})
        self.assertTrue(state["removed"])

    def test_unrelated_development_volume_is_never_removed(self):
        result, state = self.invoke("unrelated_volume")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(set(state["volume"]), {"flowplane_postgres-data"})
        self.assertNotIn(["volume", "flowplane_postgres-data"], state["removed"])

    def test_missing_volume_identity_is_not_adopted_for_cleanup(self):
        result, state = self.invoke("missing_volume_identity")
        self.assertNotEqual(result.returncode, 0)
        self.assertTrue(state["volume"])
        self.assertFalse(any(kind == "volume" for kind, _ in state["removed"]))

    def test_unknown_resource_appearing_after_capture_is_preserved_and_reported(self):
        result, state = self.invoke("late_unknown_resource")
        self.assertNotEqual(result.returncode, 0)
        unknown = state["project"] + "_unrecognized"
        self.assertIn(unknown, state["volume"])
        self.assertNotIn(["volume", unknown], state["removed"])
        self.assertIn("cleanup", result.stderr.lower())

    def test_null_or_missing_resource_labels_preserve_failure_and_resources(self):
        for case, kind in (("test_and_null_labels", "volume"), ("changed_container_labels", "container"),
                           ("missing_network_labels", "network")):
            with self.subTest(case=case):
                result, state = self.invoke(case)
                self.assertNotEqual(result.returncode, 0)
                if case == "test_and_null_labels":
                    self.assertEqual(result.returncode, 23)
                self.assertTrue(state[kind])
                self.assertIn("cleanup", result.stderr.lower())
                self.assertNotIn("Traceback", result.stderr)

    def test_partial_or_interrupted_start_records_and_guards_created_resources(self):
        for case, status in (("partial_start_failure", 18), ("signal_during_start", 143)):
            with self.subTest(case=case):
                result, state = self.invoke(case)
                self.assertEqual(result.returncode, status, result.stderr)
                self.assertFalse(state["container"])
                self.assertFalse(state["network"])
                self.assertFalse(state["volume"])

    def test_certificate_cleanup_failure_does_not_mask_original_test_failure(self):
        result, _ = self.invoke("test_and_pfx_cleanup_failure")
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertIn("cleanup", result.stderr.lower())
        self.assertNotIn("Traceback", result.stderr)

    def test_trx_cleanup_failure_does_not_mask_original_test_failure(self):
        result, _ = self.invoke("test_and_trx_cleanup_failure")
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertIn("cleanup", result.stderr.lower())
        self.assertNotIn("Traceback", result.stderr)

    def test_busy_test_port_prevents_startup(self):
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 15449))
            listener.listen()
            result, state = self.invoke()
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(state.get("started"))
        self.assertEqual(state["removed"], [])


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--fake":
        sys.exit(fake(sys.argv[2], sys.argv[3:]))
    unittest.main()
