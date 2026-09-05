# syntax=docker/dockerfile:1

# NOTE: unlike a typical ASP.NET Core app, this platform's own BuildTool/TestTool
# shell out to the real `dotnet` CLI against a mounted repository at runtime — so
# the final image uses the SDK image, not the slimmer ASP.NET runtime image. This
# is a deliberate, documented trade-off (larger image) rather than an oversight.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
RUN dotnet publish src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends git \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Workspace root that repositories are expected to be mounted into — matches
# what a person registers as Repository.LocalPath from inside the container.
RUN mkdir -p /workspace
VOLUME ["/workspace"]

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "LocalAgentPlatform.Web.dll"]
