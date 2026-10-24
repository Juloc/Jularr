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
# Project manifests change less often than source. Restore stays cached between code-only builds.
COPY src/Jularr.Integrations/Jularr.Integrations.csproj src/Jularr.Integrations/
COPY src/Jularr.Backend/Jularr.Backend.csproj src/Jularr.Backend/
COPY src/Jularr.Workers/Jularr.Workers.csproj src/Jularr.Workers/
COPY src/Jularr.Frontend/Jularr.Frontend.csproj src/Jularr.Frontend/
# Native libraries only for the image's own platform, not for every system ONNX Runtime and SkiaSharp support.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/Jularr.Frontend/Jularr.Frontend.csproj \
      --runtime linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)

COPY . .
COPY --from=frontend /src/src/Jularr.Frontend/wwwroot/ src/Jularr.Frontend/wwwroot/
COPY --from=frontend /src/src/Jularr.Frontend/Assets/Generated/ src/Jularr.Frontend/Assets/Generated/
# The release workflow passes the tag's version; the Admin System page shows it.
ARG JULARR_VERSION=0.0.0-dev
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Jularr.Frontend/Jularr.Frontend.csproj \
      --configuration Release --output /app -p:Version=$JULARR_VERSION \
      --runtime linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) --self-contained false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG TARGETARCH
ARG FPCALC_VERSION=1.6.2
ARG FPCALC_SHA256_AMD64=c968ad4a0821592a79b158fb2e1f46cbe0a22a0a1dbc0743c67d68e75d15d2bb
ARG FPCALC_SHA256_ARM64=3aa1498126ed272d805bc9d081c77965c9e1e1a0891bb112c13b56ef2ad47bad
# Jellyfin's FFmpeg plays and converts, with Intel, AMD and NVIDIA hardware transcoding; Chromaprint's own static fpcalc
# fingerprints music, pinned by hash; 7-Zip and par2 unpack and repair downloads; libjxl's cjxl encodes the cover cache as JPEG XL; the PostgreSQL 18 client backs up and restores.
RUN set -eux;     apt-get update;     apt-get install -y --no-install-recommends ca-certificates curl;     codename="$(. /etc/os-release && echo "$VERSION_CODENAME")";     curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key -o /usr/share/keyrings/jellyfin.asc;     echo "deb [signed-by=/usr/share/keyrings/jellyfin.asc] https://repo.jellyfin.org/ubuntu $codename main" > /etc/apt/sources.list.d/jellyfin.list;     install -d /usr/share/postgresql-common/pgdg;     curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc;     echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $codename-pgdg main" > /etc/apt/sources.list.d/pgdg.list;     apt-get update;     apt-get install -y --no-install-recommends jellyfin-ffmpeg7 7zip par2 libjxl-tools postgresql-client-18;     browser_packages="";     for pkg in libasound2t64 libasound2 libatk-bridge2.0-0 libatk1.0-0 libnss3 libnspr4 libgbm1 libdrm2 libglib2.0-0 libx11-6 libxcb1 libxcomposite1 libxdamage1 libxext6 libxfixes3 libxkbcommon0 libxrandr2 libxshmfence1 libpango-1.0-0 fonts-liberation; do if apt-cache policy "$pkg" | grep -Eq "Candidate: [0-9]"; then browser_packages="$browser_packages $pkg"; fi; done;     if [ -n "$browser_packages" ]; then apt-get install -y --no-install-recommends $browser_packages; fi;     ln -s /usr/lib/jellyfin-ffmpeg/ffmpeg /usr/local/bin/ffmpeg;     ln -s /usr/lib/jellyfin-ffmpeg/ffprobe /usr/local/bin/ffprobe;     case "${TARGETARCH:-amd64}" in         arm64) fpcalc_arch=arm64; fpcalc_sha256="$FPCALC_SHA256_ARM64" ;;         *) fpcalc_arch=x86_64; fpcalc_sha256="$FPCALC_SHA256_AMD64" ;;     esac;     curl -fsSL "https://github.com/acoustid/chromaprint/releases/download/v$FPCALC_VERSION/chromaprint-fpcalc-$FPCALC_VERSION-linux-$fpcalc_arch.tar.gz" -o /tmp/fpcalc.tar.gz;     echo "$fpcalc_sha256  /tmp/fpcalc.tar.gz" | sha256sum -c -;     tar -xzf /tmp/fpcalc.tar.gz -C /tmp;     install -m 0755 "/tmp/chromaprint-fpcalc-$FPCALC_VERSION-linux-$fpcalc_arch/fpcalc" /usr/local/bin/fpcalc;     rm -rf /tmp/fpcalc.tar.gz /tmp/chromaprint-fpcalc-*;     apt-get purge -y curl;     apt-get autoremove -y;     rm -rf /var/lib/apt/lists/*;     ffmpeg -version > /dev/null; ffprobe -version > /dev/null; fpcalc -version > /dev/null; { 7zz i || 7z i; } > /dev/null; par2 -V > /dev/null; cjxl --version > /dev/null; pg_dump --version;     mkdir -p /data /media;     chown app:app /data

WORKDIR /app
COPY --from=build /app ./
# The browser for sources that put a browser check in front of their pages is added later, not in the image.
COPY --from=build /src/deploy/browser/browser.sh /usr/local/bin/jularr-browser
RUN chmod +x /usr/local/bin/jularr-browser
ENV DataPath=/data \
    Storage__LocalPaths__0=/media \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Jularr.Frontend.dll"]
