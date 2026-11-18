FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish DerevenkaBot/DerevenkaBot.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:11.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data/image-generation && chown -R $APP_UID /data
USER $APP_UID
ENTRYPOINT ["dotnet", "DerevenkaBot.dll"]
