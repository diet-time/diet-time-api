FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY DietTime.sln ./
COPY src/Apis/DietTime.Meal.Api/DietTime.Meal.Api.csproj src/Apis/DietTime.Meal.Api/
COPY src/BuildingBlocks/DietTime.Application/DietTime.Application.csproj src/BuildingBlocks/DietTime.Application/
COPY src/BuildingBlocks/DietTime.Contracts/DietTime.Contracts.csproj src/BuildingBlocks/DietTime.Contracts/
COPY src/BuildingBlocks/DietTime.Domain/DietTime.Domain.csproj src/BuildingBlocks/DietTime.Domain/
COPY src/BuildingBlocks/DietTime.Infrastructure/DietTime.Infrastructure.csproj src/BuildingBlocks/DietTime.Infrastructure/
COPY src/BuildingBlocks/DietTime.Persistence/DietTime.Persistence.csproj src/BuildingBlocks/DietTime.Persistence/
RUN dotnet restore src/Apis/DietTime.Meal.Api/DietTime.Meal.Api.csproj
COPY src/ src/
RUN dotnet publish src/Apis/DietTime.Meal.Api/DietTime.Meal.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production PORT=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "DietTime.Meal.Api.dll"]
