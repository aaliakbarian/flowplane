FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props ./
COPY src/backend/FlowPlane.Api/FlowPlane.Api.csproj src/backend/FlowPlane.Api/
COPY src/backend/FlowPlane.Application/FlowPlane.Application.csproj src/backend/FlowPlane.Application/
COPY src/backend/FlowPlane.Domain/FlowPlane.Domain.csproj src/backend/FlowPlane.Domain/
COPY src/backend/FlowPlane.Infrastructure/FlowPlane.Infrastructure.csproj src/backend/FlowPlane.Infrastructure/
COPY src/backend/FlowPlane.Providers.Ssis/FlowPlane.Providers.Ssis.csproj src/backend/FlowPlane.Providers.Ssis/
RUN dotnet restore src/backend/FlowPlane.Api/FlowPlane.Api.csproj

COPY src/backend/FlowPlane.Api/ src/backend/FlowPlane.Api/
COPY src/backend/FlowPlane.Application/ src/backend/FlowPlane.Application/
COPY src/backend/FlowPlane.Domain/ src/backend/FlowPlane.Domain/
COPY src/backend/FlowPlane.Infrastructure/ src/backend/FlowPlane.Infrastructure/
COPY src/backend/FlowPlane.Providers.Ssis/ src/backend/FlowPlane.Providers.Ssis/
RUN dotnet publish src/backend/FlowPlane.Api/FlowPlane.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble AS final
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID

ENTRYPOINT ["dotnet", "FlowPlane.Api.dll"]
