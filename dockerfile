FROM mcr.microsoft.com/dotnet/sdk:8.0

RUN apt-get update && apt-get install -y --no-install-recommends \
    clang zlib1g-dev libkrb5-dev \
    mingw-w64 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /stardust
COPY . .
WORKDIR /stardust/src/Bot

RUN dotnet publish -r linux-x64 -c Release -p:DebugType=None -p:DebugSymbols=false -p:PublishAot=true -o /stardust/_BOB_OUT/x86_64-linux
RUN dotnet publish -r win-x64 -c Release -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false --self-contained true -o /stardust/_BOB_OUT/x86_64-windows

CMD ["/bin/bash", "-c", "cd /stardust/_BOB_OUT && tar -cf - ./*"]
