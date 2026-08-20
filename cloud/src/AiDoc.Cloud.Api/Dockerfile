FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/AiDoc.Cloud.Api/AiDoc.Cloud.Api.csproj src/AiDoc.Cloud.Api/
RUN dotnet restore src/AiDoc.Cloud.Api/AiDoc.Cloud.Api.csproj
COPY src/AiDoc.Cloud.Api/ src/AiDoc.Cloud.Api/
COPY docs/ docs/
RUN dotnet publish src/AiDoc.Cloud.Api/AiDoc.Cloud.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "AiDoc.Cloud.Api.dll"]