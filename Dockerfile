FROM node:24.20.0-bookworm-slim@sha256:ba849c60be29959425b8734d57b8b4b7d56f98edd9504c9af091d5281095a71e AS web
WORKDIR /web
COPY web/package*.json ./
RUN npm ci --ignore-scripts
COPY web/ ./
COPY docs/ /docs/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /source
COPY global.json Directory.Build.props ./
COPY src/Notify.Server/Notify.Server.csproj src/Notify.Server/packages.lock.json src/Notify.Server/
RUN dotnet restore src/Notify.Server/Notify.Server.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/Notify.Server/Notify.Server.csproj --no-restore -c Release -o /out
COPY --from=web /web/dist/ /out/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build /out/ ./
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /data && chown 1654:1654 /data && chmod 700 /data
USER 1654:1654
ENV ASPNETCORE_HTTP_PORTS=8080 Notify__DataPath=/data DOTNET_gcServer=0
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["curl","-fsS","http://127.0.0.1:8080/health/live"]
ENTRYPOINT ["dotnet","Notify.Server.dll"]
