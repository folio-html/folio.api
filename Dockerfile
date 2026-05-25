# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore as a separate layer so it's cached unless the project file changes.
COPY folio.api.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish folio.api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app ./

# Journals live on a mounted volume, never inside the image.
ENV Folio__DataDir=/data
VOLUME ["/data"]

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "folio.api.dll"]
