FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src
COPY RecipeCost.Pricing.csproj ./
RUN dotnet restore RecipeCost.Pricing.csproj
COPY . ./
RUN dotnet publish RecipeCost.Pricing.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "RecipeCost.Pricing.dll"]
