# Packwright in a browser: the unchanged Linux desktop app, shown through noVNC.
# Build from the repository root:
#   docker build -f docker/Dockerfile -t packwright .
# Web UI on port 5800, VNC on 5900. See docker/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Packwright.App/Packwright.App.csproj -c Release -r linux-x64 --self-contained true -o /out \
 && cp LICENSE THIRD_PARTY_NOTICES.md /out/ \
 && mkdir -p /out/licenses \
 && cp src/Packwright.Ufs2/LICENSE /out/licenses/UFS2Tool-LICENSE \
 && cp third-party/licenses/* /out/licenses/

FROM jlesage/baseimage-gui:debian-12-v4

# Libraries the Linux build needs (the same ones the .deb package depends on), plus fonts.
# libxi6 (XInput2) is on every desktop but not in the base image: without it Avalonia falls back to core X events,
# loses the right-button release and never opens context menus. libxrandr2 and libxcursor1 are the other X
# libraries Avalonia loads when they are present (screen layout, cursor theme).
RUN add-pkg libx11-6 libice6 libsm6 libfontconfig1 libicu72 libssl3 ca-certificates fonts-dejavu-core wmctrl \
            libxi6 libxrandr2 libxcursor1

COPY --from=build /out /opt/packwright
COPY src/Packwright.App/Assets/icon.png /tmp/packwright-icon.png
COPY docker/rootfs/ /

RUN chmod 755 /startapp.sh /etc/cont-init.d/55-packwright.sh \
 && set-cont-env APP_NAME "Packwright" \
 && install_app_icon.sh "file:///tmp/packwright-icon.png" \
 && rm -f /tmp/packwright-icon.png

ARG PACKWRIGHT_VERSION=dev
RUN set-cont-env APP_VERSION "$PACKWRIGHT_VERSION"

# Sign-in is on by default; it needs HTTPS (a self-signed certificate is made in /config/certs on first start).
ENV SECURE_CONNECTION=1 \
    WEB_AUTHENTICATION=1 \
    DARK_MODE=1 \
    KEEP_APP_RUNNING=1

# /config: settings, library cache, logs, certificates and the sign-in file.
# /games:  your games (mount as many folders under it as you like).
# /temp:   work files for conversions; point it at a fast disk with room to spare.
VOLUME ["/config", "/temp"]

LABEL org.opencontainers.image.title="Packwright" \
      org.opencontainers.image.description="Manage PS5 dump, image and package collections from a browser" \
      org.opencontainers.image.source="https://github.com/itsmemac/Packwright" \
      org.opencontainers.image.licenses="GPL-3.0"
