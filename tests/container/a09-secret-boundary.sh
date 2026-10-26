#!/bin/sh
# Standard-library Python supplies strict Docker JSON and VSTest/TRX parsing.
set -eu
exec python3 - "$0" "$@" <<'PY'
import json
import os
from pathlib import Path
import re
import secrets
import signal
import socket
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(sys.argv[1]).resolve().parents[2]
PROJECT_LABEL = "com.docker.compose.project"
RUN_LABEL = "io.flowplane.a09.invocation"
PORT = "15449"
REQUIRED_METHODS = {
    "ManagedSecretStoreTests": {
        "Missing_root_certificate_does_not_prevent_boot_and_flowplane_operations_fail_safely",
        "Managed_secrets_are_isolated_encrypted_and_survive_restart_with_same_root",
        "Wrong_missing_tampered_or_moved_protection_material_fails_without_plaintext_disclosure",
    },
    "ProviderInstancePersistenceTests": {
        "Environment_reference_round_trips_with_optimistic_concurrency_without_persisting_value",
        "Provider_instance_and_owned_secret_lifecycle_is_atomic_and_reference_preserving",
        "Failed_provider_creation_does_not_leave_orphaned_managed_secret",
    },
}


class Failure(Exception):
    def __init__(self, message, code=1):
        super().__init__(message)
        self.code = code


def require(condition, message):
    if not condition:
        raise Failure(message)


def command(args, env, phase, check=True):
    # Never print/persist native diagnostics, which may include assertion canaries.
    if args[0] == "docker":
        # This verifier targets the supported WSL Docker Engine, never a remote
        # current context or an inherited daemon endpoint.
        args = ["docker", "--host", "unix:///var/run/docker.sock"] + args[1:]
    result = subprocess.run(args, cwd=ROOT, env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT)
    if check and result.returncode:
        raise Failure(f"{phase} failed (exit {result.returncode}); output withheld.",
                      result.returncode if result.returncode > 0 else 1)
    return result


def decoded(result):
    try:
        return json.loads(result.stdout)
    except (ValueError, UnicodeError):
        raise Failure("Malformed Docker JSON; refusing to proceed.") from None


def environment():
    env = os.environ.copy()
    accepted = {"FLOWPLANE_UID": str(os.getuid()), "FLOWPLANE_GID": str(os.getgid()),
                "FLOWPLANE_POSTGRES_PORT": PORT, "FLOWPLANE_POSTGRES_USER": "flowplane",
                "FLOWPLANE_POSTGRES_DB": "flowplane",
                "FLOWPLANE_TEST_DISCOVERY_MIGRATION_APPROVED": "true",
                "FLOWPLANE_TEST_DISCOVERY_HOSTING_MIGRATION_APPROVED": "true"}
    for key, value in env.items():
        protected = key.upper().startswith(("FLOWPLANE_", "CONNECTIONSTRINGS__", "COMPOSE_", "DOCKER_", "BUILDX_"))
        protected |= key in {"PGHOST", "PGPORT", "PGDATABASE", "PGUSER", "PGPASSWORD", "PGSERVICE", "PGSERVICEFILE", "PGPASSFILE", "PGOPTIONS"}
        if protected:
            require(key in accepted and value == accepted[key],
                    "Conflicting inherited configuration; no resources were created.")
    require(os.getuid() != 0, "Run as the non-root WSL development user.")
    env.update(accepted)
    env["FLOWPLANE_POSTGRES_PASSWORD"] = secrets.token_urlsafe(32)
    return env


def acceptance(path, process_status):
    require(process_status == 0, "PostgreSQL test process failed.")
    require(path.is_file() and not path.is_symlink(), "Missing TRX result.")
    raw = path.read_bytes()
    require(0 < len(raw) <= 32 * 1024 * 1024, "Empty or oversized TRX result.")
    require(b"<!DOCTYPE" not in raw.upper() and b"<!ENTITY" not in raw.upper(), "Unsafe TRX declaration.")
    try:
        tree = ET.fromstring(raw)
        ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
        summary = tree.find("t:ResultSummary", ns)
        require(summary is not None and summary.get("outcome") in {"Completed", "Passed"}, "TRX run did not complete successfully.")
        counters = summary.find("t:Counters", ns)
        require(counters is not None, "TRX counters missing.")
        require(all(re.fullmatch(r"[0-9]+", v) for v in counters.attrib.values()), "Invalid TRX counters.")
        values = {k: int(v) for k, v in counters.attrib.items()}
        total = values.get("total", 0)
        require(total > 0 and values.get("executed") == total and values.get("passed") == total,
                "TRX contains unexecuted or non-passing tests.")
        require(values.get("failed") == 0 and all(v == 0 for k, v in values.items()
                if k not in {"total", "executed", "passed"}), "TRX contains failed, skipped or inconclusive tests.")
        definitions = {}
        for test in tree.findall("t:TestDefinitions/t:UnitTest", ns):
            method = test.find("t:TestMethod", ns)
            require(method is not None and test.get("id") not in definitions, "Invalid TRX test definition.")
            definitions[test.get("id")] = (method.get("className", "").split(",")[0], method.get("name"))
        results = tree.findall("t:Results/t:UnitTestResult", ns)
        require(len(results) == total and len({r.get("executionId") for r in results}) == total,
                "TRX counters disagree with individual executions.")
        executed = set()
        for result in results:
            require(result.get("outcome") == "Passed" and result.get("testId") in definitions,
                    "TRX contains a non-passing or unknown test execution.")
            executed.add(definitions[result.get("testId")])
        source = ROOT / "tests/integration/FlowPlane.Persistence.Tests"
        expected_suites = {"FlowPlane.Persistence.Tests." + p.stem for p in source.glob("*Tests.cs")}
        require(expected_suites and expected_suites <= {c for c, _ in executed}, "Expected PostgreSQL suite did not execute.")
        for suite, methods in REQUIRED_METHODS.items():
            require(all(("FlowPlane.Persistence.Tests." + suite, m) in executed for m in methods),
                    "Required A-09 security test did not execute.")
        return {"total": total, "passed": total, "failed": 0, "skipped": 0,
                "suites": sorted(expected_suites)}
    except (ET.ParseError, ValueError, TypeError, AttributeError):
        raise Failure("Malformed TRX result; test acceptance rejected.") from None


class Verification:
    def __init__(self, env):
        self.env = env
        self.nonce = secrets.token_hex(16)
        self.project = "flowplane-a09-secret-verification-" + self.nonce
        require(not Path(tempfile.gettempdir()).resolve().is_relative_to(ROOT),
                "Temporary directory must be outside the repository.")
        self.work = Path(tempfile.mkdtemp(prefix="flowplane-a09-verification-"))
        self.work.chmod(0o700)
        require(not self.work.is_relative_to(ROOT), "Private test material must be outside the repository.")
        self.certificate = self.work / "flowplane-root.pfx"
        self.env["FLOWPLANE_SECRET_ROOT_CERTIFICATE_FILE"] = str(self.certificate)
        self.owned = {kind: {} for kind in ("container", "network", "volume")}
        self.before = {}
        self.names = {"container": {self.project + "-postgres-1": "postgres", self.project + "-backend-1": "backend"},
                      "network": {self.project + "_default": "default"},
                      "volume": {self.project + "_postgres-data": "postgres-data"}}
        self.inspection_failed = False
        self.artifact_cleanup_failed = False
        self.overlay = self.work / "ownership.compose.json"
        ownership = {RUN_LABEL: self.nonce}
        self.overlay.write_text(json.dumps({"services": {s: {"labels": ownership} for s in ("backend", "postgres")},
                                            "networks": {"default": {"labels": ownership}},
                                            "volumes": {"postgres-data": {"labels": ownership}}}))
        self.compose = ["docker", "compose", "--project-name", self.project, "--env-file", "/dev/null",
                        "--file", str(ROOT / "deploy/compose/compose.yaml"),
                        "--file", str(ROOT / "deploy/compose/compose.secrets.yaml"), "--file", str(self.overlay)]

    def call(self, args, phase, check=True):
        return command(args, self.env, phase, check)

    def listing(self, kind, project_only=False):
        args = ["docker", kind, "ls"]
        if kind == "container":
            args += ["--all"]
        if kind != "volume":
            args += ["--no-trunc"]
        if project_only:
            args += ["--filter", "label=" + PROJECT_LABEL + "=" + self.project]
        output = self.call(args + ["--format", "{{json .}}"], "Resource inventory").stdout
        try:
            return [json.loads(line) for line in output.splitlines() if line.strip()]
        except ValueError:
            raise Failure("Malformed resource inventory; ownership unknown.") from None

    @staticmethod
    def identifier(kind, row):
        return row["Name"] if kind == "volume" else row["ID"]

    def inspect(self, kind, ident):
        data = decoded(self.call(["docker", kind, "inspect", ident], "Ownership inspection"))
        require(isinstance(data, list) and len(data) == 1 and isinstance(data[0], dict), "Ambiguous resource inspection.")
        return data[0]

    def identity(self, kind, item):
        name = item["Name"].lstrip("/")
        require(name in self.names[kind], "Unrecognized test resource; preserving it.")
        logical = self.names[kind][name]
        config = item.get("Config")
        require(kind != "container" or isinstance(config, dict), "Container configuration is missing; preserving it.")
        labels = config.get("Labels") if kind == "container" else item.get("Labels")
        require(isinstance(labels, dict), "Ownership labels missing; preserving resource.")
        key = {"container": "service", "network": "network", "volume": "volume"}[kind]
        require(labels.get(PROJECT_LABEL) == self.project and labels.get(RUN_LABEL) == self.nonce
                and labels.get("com.docker.compose." + key) == logical,
                "Ownership labels changed or missing; preserving resource.")
        ident = name if kind == "volume" else item["Id"]
        require(ident not in self.before[kind], "Pre-existing resource must never be removed.")
        if kind == "volume":
            require(isinstance(item.get("CreatedAt"), str) and item["CreatedAt"]
                    and item.get("Driver") == "local" and isinstance(item.get("Mountpoint"), str)
                    and item["Mountpoint"].startswith("/"), "Volume identity metadata missing; preserving it.")
        return {"id": ident, "name": name, "labels": labels,
                "created": item.get("CreatedAt", item.get("Created")),
                "driver": item.get("Driver"), "mountpoint": item.get("Mountpoint")}

    def preflight(self):
        config = decoded(self.call(self.compose + ["config", "--format", "json"], "Compose validation"))
        require(self.project != "flowplane" and config.get("name") == self.project, "Unexpected Compose project identity.")
        for kind in self.owned:
            rows = self.listing(kind)
            self.before[kind] = {self.identifier(kind, row) for row in rows}
            require(not self.listing(kind, True), "Project identity already exists; refusing reuse.")
            require(not any(row.get("Names", row.get("Name")) in self.names[kind] for row in rows),
                    "Expected resource name already exists; refusing reuse.")
        volumes, networks = config.get("volumes", {}), config.get("networks", {})
        require(set(volumes) == {"postgres-data"} and set(networks) == {"default"}, "Unexpected storage/network configuration.")
        for values, expected, driver in ((volumes["postgres-data"], self.project + "_postgres-data", "local"),
                                         (networks["default"], self.project + "_default", "bridge")):
            require(values.get("name") == expected and not values.get("external") and not values.get("driver_opts")
                    and values.get("driver", driver) == driver, "Externally named or shared storage/network is forbidden.")
        services = config["services"]
        postgres, backend = services["postgres"], services["backend"]
        mounts = postgres.get("volumes", [])
        require(len(mounts) == 1 and all(mounts[0].get(k) == v for k, v in
                {"type": "volume", "source": "postgres-data", "target": "/var/lib/postgresql"}.items()), "Unexpected PostgreSQL mount.")
        ports = postgres.get("ports", [])
        require(len(ports) == 1 and str(ports[0].get("published")) == PORT and ports[0].get("target") == 5432
                and ports[0].get("host_ip") == "127.0.0.1", "Unexpected PostgreSQL host port.")
        require(postgres.get("environment", {}) == {"POSTGRES_DB": "flowplane", "POSTGRES_USER": "flowplane",
                "POSTGRES_PASSWORD": self.env["FLOWPLANE_POSTGRES_PASSWORD"]}, "Unexpected PostgreSQL configuration.")
        require(config.get("secrets", {}).get("flowplane_root_certificate", {}).get("file") == str(self.certificate)
                and {n for n, s in services.items() if s.get("secrets")} == {"backend"}, "Unexpected certificate grant.")
        require(backend.get("user") == f"{os.getuid()}:{os.getgid()}" and not backend.get("volumes"), "Unsafe backend identity/mounts.")
        expected = {"FlowPlane__Secrets__RootCertificatePath": "/run/secrets/flowplane_root_certificate",
                    "FlowPlane__PostgreSql__Host": "postgres", "FlowPlane__PostgreSql__Port": "5432",
                    "FlowPlane__PostgreSql__Database": "flowplane", "FlowPlane__PostgreSql__Username": "flowplane",
                    "FlowPlane__PostgreSql__Password": self.env["FLOWPLANE_POSTGRES_PASSWORD"]}
        require(all(backend["environment"].get(k) == v for k, v in expected.items())
                and not any(k.lower().startswith("connectionstrings") for k in backend["environment"]), "Unsafe backend database target.")
        try:
            with socket.socket() as probe:
                probe.bind(("127.0.0.1", int(PORT)))
        except OSError:
            raise Failure("Test port 15449 is unavailable; no resources were created.") from None

    def capture_created(self):
        for kind in self.owned:
            for row in self.listing(kind, True):
                ident = self.identifier(kind, row)
                identity = self.identity(kind, self.inspect(kind, ident))
                prior = self.owned[kind].get(identity["name"])
                require(prior is None or prior == identity, "Resource identity changed during verification.")
                self.owned[kind][identity["name"]] = identity
        (self.work / "resources.json").write_text(json.dumps(self.owned, indent=2))

    def start(self, service):
        result = None
        try:
            result = self.call(self.compose + ["up", "--detach", "--wait", "--wait-timeout", "120", "--no-deps", service],
                               "Isolated service startup", check=False)
        finally:
            # A signal or failed CLI acknowledgement may leave a partial stack.
            # Discover only nonce-labelled, previously absent resources; never
            # claim quiescence after an interrupted Docker operation.
            if result is None:
                self.inspection_failed = True
            try:
                self.capture_created()
            except (Failure, OSError, KeyError, ValueError, TypeError, AttributeError):
                self.inspection_failed = True
                if result is not None and result.returncode == 0:
                    raise Failure("Unable to establish created-resource ownership; preserving unknown resources.") from None
        if result.returncode:
            raise Failure("Isolated service startup failed; output withheld.", result.returncode)

    def run(self):
        self.preflight()
        generator = ["sh", str(ROOT / "deploy/compose/create-development-root-certificate.sh"), str(self.certificate)]
        self.call(generator, "Temporary certificate generation")
        before = self.certificate.read_bytes()
        rejected = self.call(generator, "No-overwrite verification", check=False)
        require(rejected.returncode != 0 and self.certificate.read_bytes() == before, "Certificate overwrite guard failed.")
        info = self.certificate.stat()
        require(info.st_mode & 0o777 == 0o400 and (info.st_uid, info.st_gid) == (os.getuid(), os.getgid()), "Unsafe certificate permissions.")
        self.start("postgres")
        self.call(self.compose + ["build", "backend"], "Backend image build")
        results = self.work / "results"
        results.mkdir(mode=0o700)
        env = self.env.copy()
        env["FLOWPLANE_TEST_POSTGRES_CONNECTION_STRING"] = (
            "Host=127.0.0.1;Port=15449;Database=flowplane;Username=flowplane;Password="
            + self.env["FLOWPLANE_POSTGRES_PASSWORD"])
        result = command(["dotnet", "test", "tests/integration/FlowPlane.Persistence.Tests/FlowPlane.Persistence.Tests.csproj",
                          "--no-restore", "--nologo", "-m:1", "--logger", "trx;LogFileName=a09.trx",
                          "--results-directory", str(results)], env, "PostgreSQL tests", check=False)
        try:
            self.counts = acceptance(results / "a09.trx", result.returncode)
            (self.work / "acceptance.json").write_text(json.dumps(self.counts, indent=2))
        except Failure as error:
            if result.returncode:
                raise Failure("PostgreSQL test process failed; native output withheld.", result.returncode) from None
            raise error
        finally:
            # Raw TRX can contain secret-bearing assertion messages. Retain only
            # validated, value-free acceptance data, including on failure.
            try:
                for path in results.glob("*.trx"):
                    path.unlink()
            except OSError:
                self.artifact_cleanup_failed = True
                print("A09 cleanup failed: private raw test results could not be removed; original test status preserved.", file=sys.stderr)
        require(not self.artifact_cleanup_failed, "Private test-result cleanup failed.")
        self.start("backend")
        record = self.owned["container"][self.project + "-backend-1"]
        current = self.inspect("container", record["id"])
        mounts = current.get("Mounts", [])
        require(len(mounts) == 1 and mounts[0].get("Source") == str(self.certificate)
                and mounts[0].get("Destination") == "/run/secrets/flowplane_root_certificate"
                and mounts[0].get("RW") is False, "Unsafe backend secret mount.")
        self.call(["docker", "exec", record["id"], "sh", "-ec",
                   'test "$(id -u):$(id -g)" = "' + f'{os.getuid()}:{os.getgid()}' + '"; '
                   'test -r /run/secrets/flowplane_root_certificate; '
                   'test ! -w /run/secrets/flowplane_root_certificate; '
                   'test -z "$(find /app -type f -name \'*.pfx\' -print -quit)"'], "Runtime mount verification")

    def cleanup(self):
        failed = self.inspection_failed or self.artifact_cleanup_failed
        # Never adopt late resources into the deletion ledger. An unexpected
        # project-labelled resource must survive and make cleanup incomplete.
        for kind in self.owned:
            try:
                known = {record["id"] for record in self.owned[kind].values()}
                require(all(self.identifier(kind, row) in known for row in self.listing(kind, True)),
                        "Unrecorded project resource; preserving it.")
            except (Failure, OSError, KeyError, ValueError, TypeError, AttributeError):
                failed = True
                print("A09 cleanup failed: unrecorded resources or uncertain inventory; unknown resources preserved.", file=sys.stderr)
        # Immutable container/network IDs avoid broad Compose teardown. Local
        # volumes lack immutable IDs: recheck CreatedAt, labels and mountpoint.
        for kind in ("container", "network", "volume"):
            for record in reversed(list(self.owned[kind].values())):
                try:
                    current = self.inspect(kind, record["id"])
                    require(self.identity(kind, current) == record, "Cleanup identity changed; preserving resource.")
                    if kind == "container":
                        require(set(current.get("NetworkSettings", {}).get("Networks", {})) <= set(self.names["network"]),
                                "Container joined an unrecognized network; preserving it.")
                        for mount in current.get("Mounts", []):
                            require((mount.get("Type") == "volume" and mount.get("Name") in self.names["volume"])
                                    or (mount.get("Source") == str(self.certificate) and mount.get("RW") is False),
                                    "Container has an unrecognized mount; preserving it.")
                    elif kind == "network":
                        require(not current.get("Containers"), "Network still in use; preserving it.")
                    else:
                        refs = self.call(["docker", "container", "ls", "--all", "--quiet", "--no-trunc",
                                          "--filter", "volume=" + record["name"]], "Volume attachment inspection").stdout
                        require(not refs.strip(), "Volume still in use; preserving it.")
                    args = ["docker", kind, "rm"] + (["--force"] if kind == "container" else []) + [record["id"]]
                    self.call(args, "Guarded resource cleanup")
                except (Failure, OSError, KeyError, ValueError, TypeError, AttributeError):
                    failed = True
                    print("A09 cleanup failed: ownership, identity, attachment or deletion check failed; resource preserved.", file=sys.stderr)
        if not failed:
            try:
                self.certificate.unlink(missing_ok=True)
            except OSError:
                failed = True
        return failed


def main():
    os.umask(0o077)
    verification = None
    status = 0
    def interrupted(signum, _frame):
        raise Failure("Verifier interrupted; cleanup requires ownership checks.", 128 + signum)
    signal.signal(signal.SIGINT, interrupted)
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGHUP, interrupted)
    try:
        require(len(sys.argv) == 2, "Unexpected verifier arguments.")
        verification = Verification(environment())
        verification.run()
    except Failure as error:
        status = error.code
        print("A09 verification failed: " + str(error), file=sys.stderr)
    except (OSError, ValueError, KeyError, TypeError):
        status = 1
        print("A09 verification failed: operating-system or configuration error; details withheld.", file=sys.stderr)
    finally:
        if verification is not None:
            # Do not let another termination signal replace the original test
            # status midway through the guarded, best-effort cleanup pass.
            for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
                signal.signal(signum, signal.SIG_IGN)
            cleanup_failed = verification.cleanup()
            if cleanup_failed:
                print("A09 cleanup incomplete; uncertain resources/material preserved for review.", file=sys.stderr)
            if cleanup_failed and status == 0:
                status = 2
            print("A09 private structured evidence: " + str(verification.work))
    if status == 0:
        counts = verification.counts
        print(f"A09 PostgreSQL tests: passed={counts['passed']} failed=0 skipped=0")
        print("A-09 secret-boundary verification passed; recorded test resources removed safely.")
    return status


sys.exit(main())
PY
