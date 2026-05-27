# ── Stage 1: Build frontend ──────────────────────────
FROM node:22-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package.json frontend/package-lock.json* ./
RUN npm install
COPY frontend/ ./
RUN npm run build

# ── Stage 2: Build backend ───────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /app
COPY src/PUCFinance.AssetManagement/*.csproj ./src/PUCFinance.AssetManagement/
RUN dotnet restore src/PUCFinance.AssetManagement/PUCFinance.AssetManagement.csproj
COPY src/ ./src/
RUN dotnet publish src/PUCFinance.AssetManagement/PUCFinance.AssetManagement.csproj \
    -c Release -o /app/publish

# ── Stage 3: Runtime ─────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Copia o backend publicado
COPY --from=backend-build /app/publish ./

# Copia o frontend buildado pra servir como arquivos estáticos
COPY --from=frontend-build /app/frontend/dist ./wwwroot

# Copia o schema e seed
COPY database/ ./database/

# Cria diretório pro banco com permissão
RUN mkdir -p /data && chmod 777 /data

# Variáveis de ambiente
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
# Railway injeta PORT automaticamente
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT:-5000}

EXPOSE 5000

ENTRYPOINT ["dotnet", "PUCFinance.AssetManagement.dll"]
