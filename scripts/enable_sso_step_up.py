"""Run on the existing SSO host as root: python3 enable_sso_step_up.py --apply.

Uses the installed operator's lock/current release. Does not deploy code, migrate
the database, change images, or print credentials. Without --apply it only inspects.
"""
import argparse
import json
import os
from pathlib import Path
import runpy
import stat
import subprocess
import time
import urllib.request
from datetime import datetime, timezone
from types import SimpleNamespace

FLAG = 'FEATURE_EXTERNAL_STEP_UP_ENABLED'
DISCOVERY = 'https://auth.alxprgs.tech/.well-known/openid-configuration'


def private_file(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o077:
        raise RuntimeError('Expected a root-owned private regular configuration file')
    return path.read_bytes()


def discovery_enabled():
    with urllib.request.urlopen(DISCOVERY, timeout=10) as response:
        return json.load(response).get('external_step_up_enabled') is True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    if os.geteuid() != 0:
        raise RuntimeError('Run with sudo on the existing SSO host')
    operator = Path('/opt/alxprgs-sso/tools/ssoctl.py')
    info = operator.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_uid != 0 or info.st_mode & 0o022:
        raise RuntimeError('Installed operator is not a safe root-owned regular file')
    adapter = SimpleNamespace(**runpy.run_path(str(operator), run_name='notify_step_up_setup'))
    if adapter.CONFIG != Path('/etc/alxprgs-sso/runtime.env'):
        raise RuntimeError('Unexpected SSO configuration path')
    with adapter.locked():
        runtime = adapter.CONFIG
        app_env = runtime.with_name('app.env')
        originals = {runtime: private_file(runtime), app_env: private_file(app_env)}
        before = adapter.settings()
        print(json.dumps({'configuredEnabled': str(before.get(FLAG, '')).lower() == 'true', 'publicEnabled': discovery_enabled()}), flush=True)
        if not args.apply:
            return
        if str(before.get(FLAG, '')).lower() == 'true' and discovery_enabled():
            print('Already enabled; no restart required', flush=True)
            return
        lines = originals[runtime].decode('utf-8').splitlines(keepends=True)
        matches = [i for i, line in enumerate(lines) if line.startswith(FLAG + '=')]
        if len(matches) > 1:
            raise RuntimeError('Duplicate feature flag; no changes made')
        stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
        for path, data in originals.items():
            backup = path.with_name(path.name + '.notify-step-up-' + stamp + '.bak')
            descriptor = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())
        new_line = FLAG + '=' + json.dumps('true') + '\n'
        if matches:
            lines[matches[0]] = new_line
        else:
            if lines and not lines[-1].endswith('\n'):
                lines[-1] += '\n'
            lines.append(new_line)
        release = adapter.current()

        def recreate():
            command = adapter.compose(release, 'up', '-d', '--no-deps', '--no-build', '--pull', 'never', '--force-recreate', 'backend')
            result = subprocess.run(command, stdin=subprocess.DEVNULL, capture_output=True, timeout=180)
            if result.returncode:
                raise RuntimeError('Backend recreation failed; output withheld to protect environment values')

        try:
            adapter.private_write(runtime, ''.join(lines))
            after = adapter.settings()
            expected = dict(before)
            expected[FLAG] = 'true'
            if after != expected:
                raise RuntimeError('Unexpected configuration difference')
            print('Private backups saved; recreating only the current backend', flush=True)
            recreate()
            for _ in range(15):
                try:
                    if discovery_enabled():
                        print(json.dumps({'external_step_up_enabled': True, 'backendRecreated': True, 'codeDeployed': False}), flush=True)
                        return
                except Exception:
                    pass
                time.sleep(2)
            raise RuntimeError('Public discovery did not confirm the feature flag')
        except Exception:
            for path, data in originals.items():
                adapter.private_write(path, data)
            recreate()
            print('Original configuration restored and backend recreated', flush=True)
            raise


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Step-up setup failed: ' + type(error).__name__)
        if isinstance(error, SyntaxError):
            print(json.dumps({'source': error.filename, 'line': error.lineno, 'reason': error.msg}))
        raise SystemExit(1)
