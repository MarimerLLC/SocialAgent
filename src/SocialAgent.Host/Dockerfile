FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "."]
COPY ["Directory.Packages.props", "."]
COPY ["global.json", "."]
# Every project in the Host's reference graph must be listed here, otherwise restore
# silently skips it and the later `publish --no-restore` fails with NETSDK1004.
COPY ["src/SocialAgent.Core/SocialAgent.Core.csproj", "src/SocialAgent.Core/"]
COPY ["src/SocialAgent.Data/SocialAgent.Data.csproj", "src/SocialAgent.Data/"]
COPY ["src/SocialAgent.Data.Migrations.Sqlite/SocialAgent.Data.Migrations.Sqlite.csproj", "src/SocialAgent.Data.Migrations.Sqlite/"]
COPY ["src/SocialAgent.Data.Migrations.Npgsql/SocialAgent.Data.Migrations.Npgsql.csproj", "src/SocialAgent.Data.Migrations.Npgsql/"]
COPY ["src/SocialAgent.Analytics/SocialAgent.Analytics.csproj", "src/SocialAgent.Analytics/"]
COPY ["src/SocialAgent.Providers.Mastodon/SocialAgent.Providers.Mastodon.csproj", "src/SocialAgent.Providers.Mastodon/"]
COPY ["src/SocialAgent.Providers.Bluesky/SocialAgent.Providers.Bluesky.csproj", "src/SocialAgent.Providers.Bluesky/"]
COPY ["src/SocialAgent.Providers.Threads/SocialAgent.Providers.Threads.csproj", "src/SocialAgent.Providers.Threads/"]
COPY ["src/SocialAgent.Host/SocialAgent.Host.csproj", "src/SocialAgent.Host/"]
RUN dotnet restore "src/SocialAgent.Host/SocialAgent.Host.csproj"
COPY . .
WORKDIR "/src/src/SocialAgent.Host"
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SocialAgent.Host.dll"]
