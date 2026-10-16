# Jularr: the web host with every tool its features use; all state under /data, libraries under /media.

FROM node:24-bookworm-slim AS frontend
WORKDIR /src/src/Jularr.Frontend
COPY src/Jularr.Frontend/package.json src/Jularr.Frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Jularr.Frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
COPY --from=frontend /src/src/Jularr.Frontend/wwwroot/ src/Jularr.Frontend/wwwroot/
COPY --from=frontend /src/src/Jularr.Frontend/Assets/Generated/ src/Jularr.Frontend/Assets/Generated/
# Native libraries only for the image's own platform, not for every system ONNX Runtime and SkiaSharp support.
RUN dotnet publish src/Jularr.Frontend/Jularr.Frontend.csproj --configuration Release --output /app     --runtime linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG TARGETARCH
ARG FPCALC_VERSION=1.6.2
ARG FPCALC_SHA256_AMD64=c968ad4a0821592a79b158fb2e1f46cbe0a22a0a1dbc0743c67d68e75d15d2bb
ARG FPCALC_SHA256_ARM64=3aa1498126ed272d805bc9d081c77965c9e1e1a0891bb112c13b56ef2ad47bad
# Jellyfin's FFmpeg plays and converts, with Intel, AMD and NVIDIA hardware transcoding; Chromaprint's own static fpcalc
# fingerprints music, pinned by hash; 7-Zip and par2 unpack and repair downloads; the PostgreSQL 18 client backs up and restores.
RUN set -eux;     apt-get update;     apt-get install -y --no-install-recommends ca-certificates curl;     codename="$(. /etc/os-release && echo "$VERSION_CODENAME")";     curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key -o /usr/share/keyrings/jellyfin.asc;     echo "deb [signed-by=/usr/share/keyrings/jellyfin.asc] https://repo.jellyfin.org/ubuntu $codename main" > /etc/apt/sources.list.d/jellyfin.list;     install -d /usr/share/postgresql-common/pgdg;     curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc;     echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $codename-pgdg main" > /etc/apt/sources.list.d/pgdg.list;     apt-get update;     apt-get install -y --no-install-recommends jellyfin-ffmpeg7 7zip par2 postgresql-client-18;     ln -s /usr/lib/jellyfin-ffmpeg/ffmpeg /usr/local/bin/ffmpeg;     ln -s /usr/lib/jellyfin-ffmpeg/ffprobe /usr/local/bin/ffprobe;     case "${TARGETARCH:-amd64}" in         arm64) fpcalc_arch=arm64; fpcalc_sha256="$FPCALC_SHA256_ARM64" ;;         *) fpcalc_arch=x86_64; fpcalc_sha256="$FPCALC_SHA256_AMD64" ;;     esac;     curl -fsSL "https://github.com/acoustid/chromaprint/releases/download/v$FPCALC_VERSION/chromaprint-fpcalc-$FPCALC_VERSION-linux-$fpcalc_arch.tar.gz" -o /tmp/fpcalc.tar.gz;     echo "$fpcalc_sha256  /tmp/fpcalc.tar.gz" | sha256sum -c -;     tar -xzf /tmp/fpcalc.tar.gz -C /tmp;     install -m 0755 "/tmp/chromaprint-fpcalc-$FPCALC_VERSION-linux-$fpcalc_arch/fpcalc" /usr/local/bin/fpcalc;     rm -rf /tmp/fpcalc.tar.gz /tmp/chromaprint-fpcalc-*;     apt-get purge -y curl;     apt-get autoremove -y;     rm -rf /var/lib/apt/lists/*;     ffmpeg -version > /dev/null; ffprobe -version > /dev/null; fpcalc -version > /dev/null; { 7zz i || 7z i; } > /dev/null; par2 -V > /dev/null; pg_dump --version;     mkdir -p /data /media;     chown app:app /data

WORKDIR /app
COPY --from=build /app ./
ENV DataPath=/data \
    Storage__LocalPaths__0=/media \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Jularr.Frontend.dll"]
