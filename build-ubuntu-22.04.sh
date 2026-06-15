#!/bin/bash

sudo apt-get update
sudo apt-get -y install software-properties-common

sudo add-apt-repository -y ppa:dotnet/backports
sudo apt-get update

sudo apt-get -y install \
    dotnet-sdk-10.0 \
    git cmake ninja-build build-essential \
    libssl-dev pkg-config \
    libzmq5-dev libgmp-dev

(cd src/Miningcore && \
BUILDIR=${1:-../../build} && \
echo "Building into $BUILDIR" && \
dotnet publish -c Release --framework net10.0 -o $BUILDIR)
