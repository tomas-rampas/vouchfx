FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source
COPY ComposeOrders.csproj ./
RUN dotnet restore ComposeOrders.csproj
COPY Program.cs ./
RUN dotnet publish ComposeOrders.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ComposeOrders.dll"]
