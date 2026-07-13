# ================================
#  Nhom2Service (Attendance) - Dockerfile cho Render
#  ASP.NET Core .NET 8
# ================================

# ---- Build stage ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj trước để tận dụng cache khi restore
COPY Nhom2Service.csproj ./
RUN dotnet restore Nhom2Service.csproj

# Copy toàn bộ source rồi publish bản Release
COPY . ./
RUN dotnet publish Nhom2Service.csproj -c Release -o /app/publish /p:UseAppHost=false

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production

# Render cấp cổng động qua biến môi trường PORT (mặc định 10000).
# App phải lắng nghe trên 0.0.0.0:$PORT thì Render mới định tuyến được.
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://+:${PORT:-8080} dotnet Nhom2Service.dll"]
