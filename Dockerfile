FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=server/agent-host/Lges.Ama.AgentHost/Lges.Ama.AgentHost.csproj
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY server/ server/
COPY contracts/ contracts/
RUN dotnet publish "$PROJECT" -c Release -o /app/publish /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production DOTNET_EnableDiagnostics=0
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Lges.Ama.AgentHost.dll"]
