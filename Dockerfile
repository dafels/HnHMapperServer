# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files
COPY HnHMapperServer.sln ./
COPY src/HnHMapperServer.Core/HnHMapperServer.Core.csproj ./src/HnHMapperServer.Core/
COPY src/HnHMapperServer.Infrastructure/HnHMapperServer.Infrastructure.csproj ./src/HnHMapperServer.Infrastructure/
COPY src/HnHMapperServer.Services/HnHMapperServer.Services.csproj ./src/HnHMapperServer.Services/
COPY src/HnHMapperServer.Api/HnHMapperServer.Api.csproj ./src/HnHMapperServer.Api/

# Restore dependencies
RUN dotnet restore

# Copy source code
COPY src/ ./src/

# Build and publish
WORKDIR /src/src/HnHMapperServer.Api
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Copy published application
COPY --from=build /app/publish .

# Create volume for map data
VOLUME ["/map"]

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV GridStorage=/map

# Run the application
ENTRYPOINT ["dotnet", "HnHMapperServer.Api.dll"]
