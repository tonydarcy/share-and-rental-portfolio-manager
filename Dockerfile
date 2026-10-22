# The website in a container. Used by docker-compose.yml (see SETUP.md, "Run it with Docker").
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY PortfolioManager.slnx ./
COPY src/ src/
RUN dotnet publish src/PortfolioManager.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "PortfolioManager.Web.dll"]
