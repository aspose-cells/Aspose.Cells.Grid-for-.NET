FROM node:24-alpine AS frontend-build
WORKDIR /src

COPY package.json package-lock.json ./
COPY ClientApp/react-gridjs/package.json ClientApp/react-gridjs/package.json
COPY ClientApp/vue-gridjs/package.json ClientApp/vue-gridjs/package.json
COPY ClientApp/angular-gridjs/package.json ClientApp/angular-gridjs/package.json
COPY ClientApp/vanilla-gridjs-npm/package.json ClientApp/vanilla-gridjs-npm/package.json
RUN npm ci

COPY ClientApp ./ClientApp
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:6.0-jammy AS dotnet-build
WORKDIR /src

COPY GridJsDemo.csproj ./
RUN dotnet restore GridJsDemo.csproj

COPY . .
COPY --from=frontend-build /src/wwwroot/demos ./wwwroot/demos
RUN dotnet publish GridJsDemo.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:6.0-jammy AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates \
        curl \
        fonts-dejavu-core \
        fonts-liberation \
        libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --gid 1654 gridjs \
    && useradd --uid 1654 --gid gridjs --create-home gridjs \
    && mkdir -p /app/App_Data/GridJsCache \
    && chown -R gridjs:gridjs /app

COPY --from=dotnet-build --chown=gridjs:gridjs /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    HttpsRedirection__Enabled=false \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080
VOLUME ["/app/App_Data/GridJsCache"]

USER gridjs

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 \
    CMD curl --fail --silent --show-error http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "GridJsDemo.dll"]
