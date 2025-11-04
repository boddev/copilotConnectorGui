FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["IngestionService.csproj", "."]
RUN dotnet restore "IngestionService.csproj"
COPY . .
WORKDIR "/src"
RUN dotnet build "IngestionService.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "IngestionService.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create a non-root user
RUN adduser --disabled-password --gecos '' --uid 1000 appuser && chown -R appuser /app
USER appuser

ENTRYPOINT ["dotnet", "IngestionService.dll"]