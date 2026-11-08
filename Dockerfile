# The image only carries the built plugin. Kubarr's Jellyfin chart runs it as
# an init container that copies /plugin into Jellyfin's plugin folder.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG VERSION=1.0.0
RUN dotnet test -c Release \
 && dotnet publish src/Jellyfin.Plugin.Kubarr -c Release -o /out -p:Version=${VERSION} \
 && mkdir /plugin \
 && cp /out/Jellyfin.Plugin.Kubarr.dll /plugin/ \
 && sed "s/\"version\": \"[^\"]*\"/\"version\": \"${VERSION}.0\"/" meta.json > /plugin/meta.json

FROM busybox:1.37
COPY --from=build /plugin /plugin
COPY install.sh /install.sh
USER 65534
ENTRYPOINT ["/bin/sh", "/install.sh"]
