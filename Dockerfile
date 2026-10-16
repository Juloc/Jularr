# Jularr: the web host with every tool its features use; all state under /data, libraries under /media.

FROM node:24-bookworm-slim AS frontend
WORKDIR /src/src/Jularr.Frontend
COPY src/Jularr.Frontend/package.json src/Jularr.Frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/Jularr.Frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=frontend /src/src/Jularr.Frontend/wwwroot/ src/Jularr.Frontend/wwwroot/
COPY --from=frontend /src/src/Jularr.Frontend/Assets/Generated/ src/Jularr.Frontend/Assets/Generated/
RUN dotnet publish src/Jularr.Frontend/Jularr.Frontend.csproj --configuration Release --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# FFmpeg plays and converts, fpcalc fingerprints music, 7-Zip and par2 unpack and repair downloads,
# the PostgreSQL 18 client backs up and restores; its packages come from the PostgreSQL project.
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl; \
    install -d /usr/share/postgresql-common/pgdg; \
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc; \
    echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(. /etc/os-release && echo "$VERSION_CODENAME")-pgdg main" > /etc/apt/sources.list.d/pgdg.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends ffmpeg libchromaprint-tools 7zip par2 postgresql-client-18; \
    apt-get purge -y curl; \
    apt-get autoremove -y; \
    rm -rf /var/lib/apt/lists/*; \
    ffmpeg -version > /dev/null; ffprobe -version > /dev/null; fpcalc -version > /dev/null; { 7zz i || 7z i; } > /dev/null; par2 -V > /dev/null; pg_dump --version; \
    mkdir -p /data /media; \
    chown app:app /data

WORKDIR /app
COPY --from=build /app ./
ENV DataPath=/data \
    Storage__LocalPaths__0=/media \
    ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "Jularr.Frontend.dll"]
