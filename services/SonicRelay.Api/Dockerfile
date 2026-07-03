FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY ./SonicRelay.sln ./
COPY ./services/SonicRelay.Api/SonicRelay.Api.csproj ./services/SonicRelay.Api/
COPY ./src/SonicRelay.Application/SonicRelay.Application.csproj ./src/SonicRelay.Application/
COPY ./src/SonicRelay.Domain/SonicRelay.Domain.csproj ./src/SonicRelay.Domain/
COPY ./src/SonicRelay.Infrastructure/SonicRelay.Infrastructure.csproj ./src/SonicRelay.Infrastructure/
RUN dotnet restore ./services/SonicRelay.Api/SonicRelay.Api.csproj

COPY . .
RUN dotnet publish ./services/SonicRelay.Api/SonicRelay.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
RUN addgroup -S sonicrelay && adduser -S sonicrelay -G sonicrelay
USER sonicrelay
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "SonicRelay.Api.dll"]
