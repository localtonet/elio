# Build from the repository root:
#   docker build -f server/Elio.SyncServer/Dockerfile -t elio-sync .
# Run:
#   docker run -d -p 8080:8080 -v elio-sync:/data -e Sync__PublicUrl=https://sync.example.com elio-sync
# The container serves plain HTTP and is meant to sit behind a proxy that does TLS. Tell it so, or every caller
# looks like the proxy and they all share one request limit:
#   -e Sync__BehindProxy=true -e Sync__TrustedProxies=<the proxy's address or network, such as 172.17.0.1>
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY server/Elio.SyncServer/ server/Elio.SyncServer/
RUN dotnet publish server/Elio.SyncServer/Elio.SyncServer.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 Sync__Database=/data/sync.db
VOLUME /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Elio.SyncServer.dll"]
