# Build context: repository root.  docker build -f deploy/docker/assistant.Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props .editorconfig ./
COPY src/Shared/ src/Shared/
COPY src/Assistant/ src/Assistant/
RUN dotnet publish src/Assistant/CashFlow.Assistant.Api/CashFlow.Assistant.Api.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "CashFlow.Assistant.Api.dll"]
