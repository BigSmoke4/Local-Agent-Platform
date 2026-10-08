# syntax=docker/dockerfile:1

# The web app's BuildTool/TestTool invoke the real `dotnet` CLI at runtime, so the
# final image intentionally retains the SDK. The web process is non-root; this is
# defense in depth, not a replacement for isolating untrusted tools in a separate OS
# sandbox/container.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj
RUN dotnet publish src/LocalAgentPlatform.Web/LocalAgentPlatform.Web.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS runtime
ARG APP_UID=1000
ARG APP_GID=1000
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends git passwd gosu \
    && rm -rf /var/lib/apt/lists/* \
    && test "${APP_UID}" -ne 0 && test "${APP_GID}" -ne 0 \
    && if getent group app >/dev/null; then groupmod --gid "${APP_GID}" app; else groupadd --gid "${APP_GID}" app; fi \
    && if id -u app >/dev/null 2>&1; then usermod --uid "${APP_UID}" --gid app app; else useradd --uid "${APP_UID}" --gid app --create-home --home-dir /home/app --shell /usr/sbin/nologin app; fi \
    && mkdir -p /workspace /var/lib/local-agent-platform/keyring /home/app \
    && chown -R "${APP_UID}:${APP_GID}" /workspace /var/lib/local-agent-platform /home/app

COPY --from=build --chown=app:app /app/publish .
COPY src/LocalAgentPlatform.Web/docker-entrypoint.sh /usr/local/bin/docker-entrypoint.sh
RUN chmod 0755 /usr/local/bin/docker-entrypoint.sh

# Workspace root expected by the app. Bind-mounted repos must be writable by APP_UID.
VOLUME ["/workspace"]
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080 HOME=/home/app APP_UID=${APP_UID} APP_GID=${APP_GID}
USER root
ENTRYPOINT ["/usr/local/bin/docker-entrypoint.sh", "dotnet", "LocalAgentPlatform.Web.dll"]
