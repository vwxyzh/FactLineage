FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/FactLineage.Cloud.Api/FactLineage.Cloud.Api.csproj src/FactLineage.Cloud.Api/
RUN dotnet restore src/FactLineage.Cloud.Api/FactLineage.Cloud.Api.csproj
COPY src/FactLineage.Cloud.Api/ src/FactLineage.Cloud.Api/
COPY docs/ docs/
RUN dotnet publish src/FactLineage.Cloud.Api/FactLineage.Cloud.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "FactLineage.Cloud.Api.dll"]