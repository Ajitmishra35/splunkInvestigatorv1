FROM mcr.microsoft.com/dotnet/sdk:10.0.201 AS build
WORKDIR /src

COPY SplunkInvestigator.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish SplunkInvestigator.csproj -c Release -o /app/publish --no-restore -p:UseVendoredBlazorAssets=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.5
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SplunkInvestigator.dll"]
