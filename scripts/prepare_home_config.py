"""Build private home.env from locally provisioned credentials. Never prints values."""
import argparse
import json
import os
from pathlib import Path
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--android-client-id', required=True)
args = parser.parse_args()
root = Path(os.environ['LOCALAPPDATA']) / 'ALXPRGS' / 'Notify'
web = json.loads((root / 'sso-web.json').read_text(encoding='utf-8-sig'))
owner = str(uuid.UUID((root / 'owner-sub.txt').read_text(encoding='utf-8-sig').strip().strip('"')))
certificate = json.loads((root / 'signing' / 'certificate.json').read_text(encoding='utf-8-sig'))
firebase_path = root / 'firebase-service-account.json'
firebase = json.loads(firebase_path.read_text(encoding='utf-8-sig'))
if firebase.get('type') != 'service_account' or not firebase.get('private_key'):
    raise ValueError('Expected a Firebase service account JSON')
values = {
    'NOTIFY_ENV_FILE': (root / 'home.env').as_posix(),
    'FIREBASE_CREDENTIALS_FILE': firebase_path.as_posix(),
    'ASPNETCORE_ENVIRONMENT': 'Production',
    'Notify__PublicUrl': 'https://notify.alxprgs.tech',
    'Notify__SsoIssuer': 'https://auth.alxprgs.tech',
    'Notify__OwnerSub': owner,
    'Notify__WebClientId': web['client_id'],
    'Notify__WebClientSecret': web['client_secret'],
    'Notify__AndroidClientId': args.android_client_id,
    'Notify__AndroidSigningFingerprints__0': certificate['sha256'],
    'NOTIFY_MEMORY_LIMIT': '192m',
    'NOTIFY_CPU_LIMIT': '0.5',
}
if any(not isinstance(v, str) or not v or '\n' in v or '\r' in v or '$' in v for v in values.values()):
    raise ValueError('Invalid dotenv input')
(root / 'home.env').write_text(''.join(f'{key}={json.dumps(value)}\n' for key, value in values.items()), encoding='utf-8')
print('Private home.env prepared; values withheld')
