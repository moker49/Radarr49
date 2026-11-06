# syntax=docker/dockerfile:1
ARG DOTNET_SDK_VERSION=8.0.421

FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS frontend
WORKDIR /src
COPY package.json yarn.lock .yarnrc ./
RUN yarn install --frozen-lockfile --network-timeout 120000
COPY tsconfig.json ./
COPY frontend/ frontend/
ENV NODE_OPTIONS=--max-old-space-size=4096
RUN yarn build --env production

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}-bookworm-slim AS backend
ARG TARGETARCH
WORKDIR /src
COPY global.json .editorconfig ./
COPY src/ src/
COPY Logo/64.png Logo/64.png
# Publish the dynamically loaded POSIX platform assembly as well as the host.
# Keep the existing advisory warnings visible without making restore fail.
RUN case "$TARGETARCH" in amd64) rid=linux-x64 ;; arm64) rid=linux-arm64 ;; *) exit 1 ;; esac; \
    dotnet publish src/NzbDrone.Mono/Radarr.Mono.csproj \
      -c Release -f net8.0 -r "$rid" --self-contained false -o /publish \
      -p:Platform=Posix -p:RuntimeIdentifiers="$rid" -p:SolutionDir=/src/src/ \
      '-p:WarningsNotAsErrors=NU1902%3BNU1903' \
    && dotnet publish src/NzbDrone.Console/Radarr.Console.csproj \
      -c Release -f net8.0 -r "$rid" --self-contained false -o /publish \
      -p:Platform=Posix -p:RuntimeIdentifiers="$rid" -p:SolutionDir=/src/src/ \
      '-p:WarningsNotAsErrors=NU1902%3BNU1903' \
    && test -f /publish/Radarr.dll \
    && test -f /publish/Radarr.Mono.dll

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg libsqlite3-0 tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /config /app/bin \
    && chown app:app /config
WORKDIR /app/bin
COPY --from=backend /publish/ ./
COPY --from=frontend /src/_output/UI/ ./UI/
COPY LICENSE /app/LICENSE
# Radarr searches its application directory for ffprobe.
RUN ln -sf /usr/bin/ffprobe /app/bin/ffprobe \
    && printf '%s\n' \
      'PackageAuthor=Radarr composite-profile fork' \
      'UpdateMethod=Docker' \
      'UpdateMethodMessage=Rebuild and recreate this container to update the fork.' \
      > /app/package_info
ENV Radarr__Server__BindAddress=* \
    Radarr__Server__Port=7878 \
    Radarr__Server__EnableSsl=false \
    Radarr__Server__UrlBase="" \
    Radarr__Update__Mechanism=Docker \
    Radarr__Update__Automatically=false \
    HOME=/config
USER app
VOLUME ["/config"]
EXPOSE 7878
HEALTHCHECK --interval=30s --timeout=5s --start-period=90s --retries=3 \
    CMD curl --fail --silent --show-error "http://127.0.0.1:7878${Radarr__Server__UrlBase}/ping" || exit 1
ENTRYPOINT ["dotnet", "/app/bin/Radarr.dll", "/nobrowser", "/data=/config"]
