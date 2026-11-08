FROM python:3.12-slim-bookworm
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && python -m pip install --no-cache-dir --index-url https://download.pytorch.org/whl/cpu torch==2.10.0
WORKDIR /app
COPY server.py ./
USER 10001:10001
EXPOSE 8080
CMD ["python", "-B", "server.py"]
