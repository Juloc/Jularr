FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/Jularr.Integrations/ src/Jularr.Integrations/
COPY src/Jularr.MountHelper/ src/Jularr.MountHelper/
RUN dotnet publish src/Jularr.MountHelper/Jularr.MountHelper.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends cifs-utils nfs-common \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /run/jularr-mount-helper /var/lib/jularr-mount-helper /mnt/jularr \
    && chown root:1000 /run/jularr-mount-helper \
    && chmod 2770 /run/jularr-mount-helper \
    && chmod 700 /var/lib/jularr-mount-helper
WORKDIR /app
COPY --from=build /publish/ .
ENTRYPOINT ["dotnet", "Jularr.MountHelper.dll"]
