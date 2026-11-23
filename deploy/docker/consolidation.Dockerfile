# Build context: repository root.  docker build -f deploy/docker/consolidation.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY src/Shared/ src/Shared/
COPY src/Consolidation/ src/Consolidation/
RUN dotnet publish src/Consolidation/CashFlow.Consolidation.Api/CashFlow.Consolidation.Api.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "CashFlow.Consolidation.Api.dll"]
