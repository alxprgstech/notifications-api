"""Prepare, apply or restore the three file changes for additional SIP assistants."""

import argparse
import ast
import hashlib
import json
import os
from pathlib import Path
import re
from runpy import run_path
import stat
from tempfile import NamedTemporaryFile

BASE = Path(__file__).resolve().parent
FILES = {
    "dialplan": "etc/asterisk/extensions.conf",
    "environment": "etc/alxprgs-notify/asterisk.env",
    "bridge": "opt/alxprgs-notify/asterisk/bridge.py",
}
RENDER = run_path(str(BASE / "render-assistant-route.py"))["render"]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def regular(path):
    resolved = path.resolve(strict=True)
    if resolved != path or not stat.S_ISREG(path.lstat().st_mode):
        raise ValueError("Expected a regular file with no symbolic links")
    return path


def read_site(root):
    return {name: regular(root / relative).read_bytes() for name, relative in FILES.items()}


def stage(root, bundle, context, phone, numbers):
    if re.fullmatch(r"[A-Za-z0-9_.-]+", context) is None:
        raise ValueError("Invalid SIP context")
    if len(numbers) != len(set(numbers)):
        raise ValueError("Duplicate SIP extensions")
    if bundle.is_relative_to(root) and root != Path(root.anchor):
        raise ValueError("Use a backup directory outside the site copy")
    previous = read_site(root)
    dialplan = previous["dialplan"].decode("utf-8")
    section = re.compile(r"(?m)^\[" + re.escape(context) + r"\][ \t]*(?:;[^\r\n]*)?\r?\n")
    sections = list(section.finditer(dialplan))
    if len(sections) != 1:
        raise ValueError("Expected exactly one local SIP context section")

    environment = previous["environment"].decode("utf-8")
    values = {}
    for key in ("ASTERISK_ENDPOINT", "ASTERISK_ASSISTANT_CONTEXT", "ASTERISK_ASSISTANT_EXTENSION",
                "ASTERISK_ASSISTANT_ADDITIONAL_EXTENSIONS"):
        lines = re.findall(r"(?m)^[ \t]*(?:export[ \t]+)?" + key + r"[ \t]*=([^\r\n]*)", environment)
        if len(lines) > 1:
            raise ValueError("Ambiguous bridge environment")
        values[key] = lines[0].strip() if lines else ""
    endpoint = values["ASTERISK_ENDPOINT"]
    if not re.fullmatch(r"PJSIP/[A-Za-z0-9_.-]+", endpoint) or values["ASTERISK_ASSISTANT_CONTEXT"] != context:
        raise ValueError("Retain the existing PJSIP endpoint and trusted assistant context")
    legacy = values["ASTERISK_ASSISTANT_EXTENSION"]
    extra = values["ASTERISK_ASSISTANT_ADDITIONAL_EXTENSIONS"]
    assigned = ([legacy] if legacy else []) + (extra.split(",") if extra else [])
    if any(not re.fullmatch(r"[0-9]{2,8}", number) for number in assigned) or len(assigned) != len(set(assigned)):
        raise ValueError("Invalid configured extensions")

    routes = []
    for number in numbers:
        route = RENDER(number, phone)
        if number in assigned or re.search(r"(?mi)^[ \t]*exten[ \t]*=>[ \t]*" + number + r"[ \t]*,", dialplan):
            raise ValueError("Existing SIP extension detected")
        routes.append(route)
    offset = sections[0].end()
    newline = "\r\n" if sections[0].group().endswith("\r\n") else "\n"
    routes_block = ("\n".join(routes) + "\n").replace("\n", newline)
    setting = "ASTERISK_ASSISTANT_ADDITIONAL_EXTENSIONS=" + ",".join(([extra] if extra else []) + numbers)
    setting_line = r"(?m)^[ \t]*(?:export[ \t]+)?ASTERISK_ASSISTANT_ADDITIONAL_EXTENSIONS[ \t]*=[^\r\n]*"
    environment, replaced = re.subn(setting_line, setting, environment)
    if not replaced:
        newline_env = "\r\n" if "\r\n" in environment else "\n"
        separator = "" if environment.endswith("\n") else newline_env
        environment += separator + setting + newline_env

    bridge = (BASE / "bridge.py").read_bytes()
    ast.parse(bridge)
    proposed = {
        "dialplan": (dialplan[:offset] + routes_block + dialplan[offset:]).encode("utf-8"),
        "environment": environment.encode("utf-8"),
        "bridge": bridge,
    }
    bundle.mkdir(mode=0o700)
    manifest = {"root": str(root), "hashes": {}}
    for name in FILES:
        manifest["hashes"][name] = {}
        for version, files in (("before", previous), ("after", proposed)):
            (bundle / f"{name}.{version}").write_bytes(files[name])
            manifest["hashes"][name][version] = digest(files[name])
    (bundle / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")


def read_bundle(root, bundle):
    manifest = json.loads(regular(bundle / "manifest.json").read_text(encoding="utf-8"))
    if manifest["root"] != str(root):
        raise ValueError("Backup belongs to a different site directory")
    snapshots = {}
    for name in FILES:
        snapshots[name] = {}
        for version in ("before", "after"):
            data = regular(bundle / f"{name}.{version}").read_bytes()
            if digest(data) != manifest["hashes"][name][version]:
                raise ValueError("Backup content did not match its recorded checksum")
            snapshots[name][version] = data
    return snapshots


def atomic_replace(path, data):
    previous = path.stat()
    temporary = None
    try:
        with NamedTemporaryFile(dir=path.parent, delete=False) as output:
            temporary = Path(output.name)
            output.write(data)
            output.flush()
            if os.name == "posix":
                os.fchown(output.fileno(), previous.st_uid, previous.st_gid)
                os.fchmod(output.fileno(), stat.S_IMODE(previous.st_mode))
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        if temporary:
            temporary.unlink(missing_ok=True)


def reconcile(root, bundle, target=None):
    snapshots = read_bundle(root, bundle)
    current = read_site(root)
    state = {}
    for name, data in current.items():
        matches = [version for version, snapshot in snapshots[name].items() if snapshot == data]
        if not matches:
            raise ValueError(f"Unexpected change in {name}; no files written")
        state[name] = "unchanged" if len(matches) == 2 else matches[0]
    if target:
        order = reversed(FILES) if target == "before" else FILES
        for name in order:
            desired = snapshots[name][target]
            if current[name] == desired:
                continue
            path = regular(root / FILES[name])
            if path.read_bytes() != current[name]:
                raise ValueError(f"Concurrent change in {name}; inspect status before proceeding")
            atomic_replace(path, desired)
        result = reconcile(root, bundle)
        if any(value not in (target, "unchanged") for value in result.values()):
            raise ValueError("State changed during migration; inspect status before activating services")
        return result
    return state


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("stage", "status", "apply", "rollback"))
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--context")
    parser.add_argument("--phone")
    parser.add_argument("--extension", action="append", default=[])
    args = parser.parse_args()
    os.umask(0o077)
    try:
        root = args.root.resolve(strict=True)
        bundle = args.bundle.parent.resolve(strict=True) / args.bundle.name
        if args.action == "stage":
            if not args.context or not args.phone or not args.extension:
                parser.error("stage requires --context, --phone and --extension")
            stage(root, bundle, args.context, args.phone, args.extension)
            print("Bundle staged. Site files unchanged.")
        else:
            target = {"apply": "after", "rollback": "before"}.get(args.action)
            print(json.dumps(reconcile(root, bundle, target), sort_keys=True))
    except (OSError, ValueError, TypeError, KeyError, SyntaxError) as error:
        parser.exit(1, f"Migration stopped ({type(error).__name__}). Verify the private bundle and file status.\n")


if __name__ == "__main__":
    main()
