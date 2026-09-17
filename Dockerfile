FROM mcr.microsoft.com/dotnet/sdk:9.0 AS restore
WORKDIR /src
COPY src/JapaneseLearning.User.Api/JapaneseLearning.User.Api.csproj src/JapaneseLearning.User.Api/
COPY src/JapaneseLearning.User.Application/JapaneseLearning.User.Application.csproj src/JapaneseLearning.User.Application/
COPY src/JapaneseLearning.User.Domain/JapaneseLearning.User.Domain.csproj src/JapaneseLearning.User.Domain/
COPY src/JapaneseLearning.User.Infrastructure/JapaneseLearning.User.Infrastructure.csproj src/JapaneseLearning.User.Infrastructure/
RUN dotnet restore src/JapaneseLearning.User.Api/JapaneseLearning.User.Api.csproj

FROM restore AS build
COPY src/ src/
RUN dotnet build src/JapaneseLearning.User.Api/JapaneseLearning.User.Api.csproj -c Release --no-restore -p:UseAppHost=false

FROM build AS publish
RUN dotnet publish src/JapaneseLearning.User.Api/JapaneseLearning.User.Api.csproj -c Release --no-build --no-restore -p:UseAppHost=false -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Http__RedirectToHttps=false
EXPOSE 8080
COPY --from=publish /app/publish/ ./
USER $APP_UID
ENTRYPOINT ["dotnet", "JapaneseLearning.User.Api.dll"]
