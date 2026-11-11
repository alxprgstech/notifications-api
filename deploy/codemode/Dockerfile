FROM node:24.20.0-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN chown node:node /app
COPY package*.json ./
RUN npm ci --ignore-scripts
RUN mkdir /app/.wrangler && chown node:node /app/.wrangler
COPY worker.js wrangler.jsonc ./
ENV XDG_CACHE_HOME=/tmp/notify-worker-cache
USER node
CMD ["sh", "-c", "exec ./node_modules/.bin/wrangler dev --local --ip 0.0.0.0 --var \"NOTIFY_ORIGIN:$NOTIFY_ORIGIN\" --var \"NOTIFY_LOCAL:$NOTIFY_LOCAL\""]
