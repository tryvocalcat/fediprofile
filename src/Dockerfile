# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY FediProfile/FediProfile.csproj FediProfile/
RUN dotnet restore FediProfile/FediProfile.csproj

COPY FediProfile/. FediProfile/
RUN dotnet publish FediProfile/FediProfile.csproj -c Release -o /app/out /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/out .

EXPOSE 8080
ENTRYPOINT ["dotnet", "FediProfile.dll"]