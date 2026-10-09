FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# Só os binários nativos da plataforma do container (ex.: libvips), não os de todas as plataformas.
ARG TARGETARCH
RUN dotnet publish src/Worker/Ecommerce.Worker.csproj -c Release -o /app --self-contained false \
    -r linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ecommerce.Worker.dll"]
