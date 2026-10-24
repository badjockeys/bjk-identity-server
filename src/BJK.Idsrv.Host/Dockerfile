# syntax=docker/dockerfile:1
# The identity server's container image. Build from the repo root:
#   docker build -f src/BJK.Idsrv.Host/Dockerfile --secret id=nuget,env=NuGetPackageSourceCredentials_badjockeys -t bjk-identity-server .
# The secret is the "Username=...;Password=<token with read:packages>" value for the BJK.Modules.* feed
# (docs/local-development.md). It's only mounted for the restore step and never stored in the image.
# CI pushes it to ghcr.io/badjockeys/bjk-identity-server, tagged with the commit SHA and latest.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BJK.Idsrv.Host/BJK.Idsrv.Host.csproj src/BJK.Idsrv.Host/
RUN --mount=type=secret,id=nuget,env=NuGetPackageSourceCredentials_badjockeys \
    dotnet restore src/BJK.Idsrv.Host/BJK.Idsrv.Host.csproj
COPY src/BJK.Idsrv.Host/ src/BJK.Idsrv.Host/
RUN dotnet publish src/BJK.Idsrv.Host/BJK.Idsrv.Host.csproj --configuration Release --no-restore --output /app
# The age check's liveness model: OpenCV Zoo's YuNet face detector (MIT), pinned to one commit and SHA-256 checked.
# The same file and checksum as bjk-core's scripts/setup-local.mjs.
RUN mkdir -p /opt/bjk/models \
    && curl -fsSL --retry 3 -o /opt/bjk/models/face_detection_yunet_2023mar.onnx \
       https://media.githubusercontent.com/media/opencv/opencv_zoo/47534e27c9851bb1128ccc0102f1145e27f23f98/models/face_detection_yunet/face_detection_yunet_2023mar.onnx \
    && echo "8f2383e4dd3cfbb4553ea8718107fc0423210dc964f9f4280604804ed2552fa4  /opt/bjk/models/face_detection_yunet_2023mar.onnx" | sha256sum -c -

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ffmpeg reads the frames of the age check's selfie video for the liveness check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY --from=build /opt/bjk/models /opt/bjk/models
# Settings come from environment variables (Container Apps secrets in Azure), never from files in the image. These
# two are where this image keeps its own tools, so they're set here and win over any user secrets.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    AgeAssurance__Liveness__ModelPath=/opt/bjk/models/face_detection_yunet_2023mar.onnx \
    AgeAssurance__Liveness__FfmpegPath=ffmpeg
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "BJK.Idsrv.Host.dll"]
