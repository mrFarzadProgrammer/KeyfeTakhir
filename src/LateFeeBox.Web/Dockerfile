# The project has no external NuGet packages. Restore and publish run with
# network disabled after Docker has the two official .NET base images.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_XMLDOC_MODE=skip
WORKDIR /src
COPY NuGet.Config ./NuGet.Config
COPY src/LateFeeBox.Web/LateFeeBox.Web.csproj src/LateFeeBox.Web/
RUN dotnet restore src/LateFeeBox.Web/LateFeeBox.Web.csproj \
    --configfile /src/NuGet.Config \
    --ignore-failed-sources
COPY . .
WORKDIR /src/src/LateFeeBox.Web
RUN dotnet publish LateFeeBox.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN mkdir -p /app/data /app/data/keys && chown -R app:app /app/data
USER app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "LateFeeBox.Web.dll"]
