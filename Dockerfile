# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SlotSmith.Api.csproj", "./"]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish .
# wwwroot/uploads (staff photos) needs to be a mounted volume in production — see README
# "Known simplifications" — otherwise uploaded photos vanish on the next deploy/rebuild.
ENTRYPOINT ["dotnet", "SlotSmith.Api.dll"]
