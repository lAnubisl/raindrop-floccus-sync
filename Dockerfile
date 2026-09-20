ARG IMAGE_PLATFORM=linux/amd64
ARG DOTNET_RUNTIME=linux-x64

FROM --platform=${IMAGE_PLATFORM} mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG DOTNET_RUNTIME
WORKDIR /source

COPY src/RaindropToFloccus/RaindropToFloccus.csproj src/RaindropToFloccus/
RUN dotnet restore src/RaindropToFloccus/RaindropToFloccus.csproj \
    --runtime "${DOTNET_RUNTIME}"

COPY src/RaindropToFloccus/ src/RaindropToFloccus/
RUN dotnet publish src/RaindropToFloccus/RaindropToFloccus.csproj \
    --configuration Release \
    --runtime "${DOTNET_RUNTIME}" \
    --self-contained false \
    --no-restore \
    --output /app

FROM --platform=${IMAGE_PLATFORM} mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl git openssh-client \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

RUN mkdir --parents /var/lib/raindrop-to-floccus \
    && chown --recursive $APP_UID:$APP_UID /var/lib/raindrop-to-floccus

ENV GIT_WORKING_DIRECTORY=/var/lib/raindrop-to-floccus/repository \
    HEALTH_PORT=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD curl --fail --silent http://localhost:${HEALTH_PORT}/health > /dev/null || exit 1

USER $APP_UID
ENTRYPOINT ["dotnet", "RaindropToFloccus.dll"]
