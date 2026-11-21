# Payment Gateway Adapter - Multi-stage Docker Build
# Library package, not a runnable service - but can be used as base image

# ============================================
# BUILD STAGE
# ============================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies (cached layer)
COPY ["PaymentGatewayAdapter.csproj", "."]
RUN dotnet restore "PaymentGatewayAdapter.csproj"

# Copy source and build
COPY . .
RUN dotnet build "PaymentGatewayAdapter.csproj" -c Release -o /app/build

# Run tests
RUN dotnet test "PaymentGatewayAdapter.csproj" -c Release --no-build --logger "trx;LogFileName=test_results.trx"

# ============================================
# PACKAGE STAGE
# ============================================
FROM build AS package
RUN dotnet pack "PaymentGatewayAdapter.csproj" -c Release -o /app/package /p:PackageVersion=1.0.0

# ============================================
# TEST RUNNER STAGE (for CI)
# ============================================
FROM build AS test
ENTRYPOINT ["dotnet", "test", "PaymentGatewayAdapter.csproj", "-c", "Release", "--no-build", "--logger", "trx;LogFileName=test_results.trx"]

# ============================================
# RUNTIME BASE (for consumers)
# ============================================
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# This image serves as a base for applications using this library
# The library itself is a DLL, not an executable
COPY --from=build /app/build/. ./

# Metadata
LABEL org.opencontainers.image.title="PaymentGatewayAdapter"
LABEL org.opencontainers.image.description="Unified payment gateway adapter for Monobank, LiqPay, Portmone, WayForPay, Stripe, PayPal"
LABEL org.opencontainers.image.version="1.0.0"
LABEL org.opencontainers.image.source="https://github.com/yourusername/payment-gateway-adapter"
LABEL org.opencontainers.image.licenses="MIT"