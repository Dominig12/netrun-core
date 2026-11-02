# syntax=docker/dockerfile:1
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0.403
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /source
COPY global.json Directory.Build.props Directory.Build.targets ./
COPY src/ ./src/
RUN dotnet restore src/Netrun.Host/Netrun.Host.csproj --locked-mode \
    && dotnet publish src/Netrun.Host/Netrun.Host.csproj -c Release --no-restore \
       --output /publish /p:UseAppHost=false

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
WORKDIR /app
COPY --from=build /publish/ ./
ENV NETRUN_PORT=18462 \
    NETRUN_SESSION_TOKEN_FILE=/run/secrets/netrun_session_token
USER app
EXPOSE 18462
ENTRYPOINT ["dotnet", "Netrun.Host.dll", "serve"]
