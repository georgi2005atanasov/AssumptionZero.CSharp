FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY . .
RUN dotnet restore AssumptionZero.sln \
    && dotnet build AssumptionZero.sln -c Release --no-restore \
    && dotnet publish AssumptionZero.App/AssumptionZero.App.csproj -c Release --no-build -o /publish/app \
    && dotnet publish AssumptionZero.Simulator/AssumptionZero.Simulator.csproj -c Release --no-build -o /publish/simulator

FROM build AS tests
CMD ["dotnet", "test", "AssumptionZero.Tests/AssumptionZero.Tests.csproj", "-c", "Release", "--no-build", "--no-restore"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish/app ./trader
COPY --from=build /publish/simulator ./simulator
USER $APP_UID
CMD ["dotnet", "/app/trader/AssumptionZero.App.dll"]
