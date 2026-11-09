FROM python:3.12-slim-bookworm@sha256:935c3c5b12357a7e10caa9c1e6ca5692344d97cf229832ef584be2cec9fc03df
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && python -m pip install --no-cache-dir --upgrade setuptools==80.9.0 wheel==0.46.2 jaraco.context==6.1.0 \
    && python -m pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch==2.10.0 \
    && python -m pip uninstall -y setuptools wheel jaraco.context
WORKDIR /app
COPY server.py ./
USER 10001:10001
EXPOSE 8080
CMD ["python", "-B", "server.py"]
