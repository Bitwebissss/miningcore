FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS BUILDER
WORKDIR /app
RUN apt-get update && \
    apt-get -y install cmake ninja-build build-essential libssl-dev pkg-config \
    libzmq5-dev libgmp-dev && \
    apt-get clean
COPY . .
WORKDIR /app/src/Miningcore
RUN dotnet publish -c Release --framework net10.0 -o ../../build

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
RUN apt-get update && \
    apt-get install -y libzmq5 curl && \
    apt-get clean
EXPOSE 4000-4090
COPY --from=BUILDER /app/build ./
CMD ["./Miningcore", "-c", "config.json"]
