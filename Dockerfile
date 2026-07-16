FROM node:24-alpine AS web-build
WORKDIR /source/apps/web
COPY apps/web/package*.json ./
RUN npm ci
COPY apps/web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS api-build
WORKDIR /source
COPY apps/api/CaseLedger.Api.csproj apps/api/
RUN dotnet restore apps/api/CaseLedger.Api.csproj
COPY apps/api/ apps/api/
COPY --from=web-build /source/apps/web/dist apps/api/wwwroot/
RUN dotnet publish apps/api/CaseLedger.Api.csproj --configuration Release --no-restore --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
LABEL org.opencontainers.image.source="https://github.com/MarvelousJade/CaseLedger" \
      org.opencontainers.image.description="CaseLedger full-stack case management demo" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=api-build /out ./
RUN mkdir /data && chown "$APP_UID" /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__CaseLedger="Data Source=/data/caseledger.db"
EXPOSE 8080
VOLUME ["/data"]
USER $APP_UID
ENTRYPOINT ["dotnet", "CaseLedger.Api.dll"]
