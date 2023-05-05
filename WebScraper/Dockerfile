FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["WebScraper.csproj", "./"]
RUN dotnet restore "WebScraper.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "WebScraper.csproj" -c Release -o /app/build
COPY appsettings.json /app/build/
FROM build AS publish
RUN dotnet publish "WebScraper.csproj" -c Release -o /app/publish
COPY appsettings.json /app/publish/
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "WebScraper.dll"]

