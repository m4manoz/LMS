# Build context is the repository root:  docker build -f src/Lms.Api/Dockerfile -t lms-api .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Lms.Api/Lms.Api.csproj src/Lms.Api/
RUN dotnet restore src/Lms.Api/Lms.Api.csproj
COPY src/Lms.Api src/Lms.Api
RUN dotnet publish src/Lms.Api/Lms.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Durable state (Data Protection keys, uploaded content) lives on a volume mounted at /data.
RUN mkdir -p /data && chown app:app /data
USER app
ENV ASPNETCORE_ENVIRONMENT=Production \
    Kestrel__Endpoints__Http__Url=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Lms.Api.dll"]
