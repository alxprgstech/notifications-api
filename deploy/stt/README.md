# Private Russian STT for Notify

`stt` is an internal-only HTTP service used by the server for speech recognition of
the owner's Russian voice. It never receives raw call frames directly: the Notify
process assembles a bounded utterance and posts a PCM16LE mono 16 kHz WAV. The
service processes it in memory and returns text; it does not persist audio or text.

## Model choice

`vosk-model-small-ru-0.22` (Alpha Cephei, Vosk) was selected for the desktop
Docker profile because it is small enough to run alongside Silero in Docker
Desktop, starts quickly on CPU, supports streaming PCM, and has published Russian
benchmarks (11.8–32% WER depending on the corpus in its `README`). A larger Vosk
model was rejected for the current single-host memory budget; a cloud STT or a
paid API is out of scope. The model is used unchanged.

- Model source: <https://github.com/kercre123/vosk-models> (upstream mirror;
  original distribution: <https://alphacephei.com/vosk/models>).
- Archive SHA-256: `961d5ff98a17f4aa6de69864d0aa71fa5bac682301d2b5d17a3f24c5c99a46d4`.
- Library and model family: Vosk / Apache-2.0; see
  <https://github.com/alphacep/vosk-api>.
- Every unpacked file is checked against `model.sha256` before `vosk.Model` is
  constructed. The setup script checks the archive hash too.

## Install

```powershell
pwsh -File scripts/setup-stt-model.ps1
$env:STT_MODEL_DIR = Join-Path $env:LOCALAPPDATA 'ALXPRGS\Notify\models\vosk-model-small-ru-0.22'
```

The model directory is mounted read-only at `/model`. The service has no published
port, only `speech` network access, a 384 MiB memory limit, 0.5 CPU, a read-only
root filesystem, and a bounded request body. `Notify__SttUrl` enables conversation
mode; without it the API advertises no live dialogue and keeps text-only features.

## API

- `GET /health/ready` — model is loaded and verified.
- `POST /recognize` — body is a WAV (`audio/wav`, at most 10 MiB, at most 5
  minutes); response is `{"text":"..."}`. Empty speech returns 422.

No audio or recognition result is written to a log or disk.
