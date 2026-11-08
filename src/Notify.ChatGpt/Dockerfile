FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Notify.ChatGpt/Notify.ChatGpt.csproj src/Notify.ChatGpt/packages.lock.json src/Notify.ChatGpt/
RUN dotnet restore src/Notify.ChatGpt --locked-mode
COPY src/Notify.ChatGpt/ src/Notify.ChatGpt/
COPY src/Notify.Server/WebLoginState.cs src/Notify.Server/
RUN dotnet publish src/Notify.ChatGpt -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12
WORKDIR /app
RUN mkdir /state && chown ${APP_UID} /state && chmod 700 /state
COPY --from=build /app ./
USER ${APP_UID}
EXPOSE 8080
ENTRYPOINT ["dotnet", "Notify.ChatGpt.dll"]
