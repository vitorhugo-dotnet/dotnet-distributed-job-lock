FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/BillingWorker/BillingWorker.csproj src/BillingWorker/
RUN dotnet restore src/BillingWorker/BillingWorker.csproj

COPY src/ src/
RUN dotnet publish src/BillingWorker/BillingWorker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "BillingWorker.dll"]
