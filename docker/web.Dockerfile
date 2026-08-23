# Multi-stage Dockerfile for HnH Mapper Web Service (Blazor Server)
# Uses .NET 10.0 SDK for building and .NET 10.0 ASP.NET runtime for production

# ============================================================================
# Build Stage: Compile and publish the Web project
# ============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Build arguments for version information (passed from docker build --build-arg)
ARG BUILD_VERSION="dev"
ARG BUILD_COMMIT="dev"
ARG BUILD_DATE="1970-01-01T00:00:00Z"

WORKDIR /src

# Copy project files for dependency restoration
# This layer is cached unless project files change, speeding up rebuilds
COPY src/HnHMapperServer.Core/HnHMapperServer.Core.csproj ./src/HnHMapperServer.Core/
COPY src/HnHMapperServer.Infrastructure/HnHMapperServer.Infrastructure.csproj ./src/HnHMapperServer.Infrastructure/
COPY src/HnHMapperServer.Services/HnHMapperServer.Services.csproj ./src/HnHMapperServer.Services/
COPY src/HnHMapperServer.Web/HnHMapperServer.Web.csproj ./src/HnHMapperServer.Web/

# Restore NuGet packages for Web project and its dependencies
WORKDIR /src/src/HnHMapperServer.Web
RUN dotnet restore

# Copy all source code (this layer invalidates when code changes)
WORKDIR /src
COPY src/ ./src/

# Build and publish the Web project in Release configuration
# Pass build version as InformationalVersion for assembly metadata fallback
WORKDIR /src/src/HnHMapperServer.Web
RUN dotnet publish -c Release -o /app/publish -p:InformationalVersion="${BUILD_VERSION}"

# ============================================================================
# Runtime Stage: Minimal production image with only the compiled application
# ============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0

# Build arguments need to be re-declared in runtime stage to be accessible
ARG BUILD_VERSION="dev"
ARG BUILD_COMMIT="dev"
ARG BUILD_DATE="1970-01-01T00:00:00Z"

WORKDIR /app

# Copy the published application from the build stage
COPY --from=build /app/publish .

# Configure ASP.NET Core to listen on port 8080 (non-privileged port)
ENV ASPNETCORE_URLS=http://+:8080

# Set the data storage path (will be mounted as a volume in production)
ENV GridStorage=/data

# Set build version environment variables (read by BuildInfoProvider)
ENV BUILD_VERSION=${BUILD_VERSION}
ENV BUILD_COMMIT=${BUILD_COMMIT}
ENV BUILD_DATE=${BUILD_DATE}

# Expose port 8080 for the Web service
EXPOSE 8080

# Run the Web application (Blazor Server)
ENTRYPOINT ["dotnet", "HnHMapperServer.Web.dll"]

