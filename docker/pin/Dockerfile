
## BUILD ##
FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build

WORKDIR /src

COPY *.props .
COPY *.sln* .
COPY *.cs .
COPY Microwalk Microwalk
COPY Microwalk.FrameworkBase Microwalk.FrameworkBase
COPY Microwalk.Plugins.PinTracer Microwalk.Plugins.PinTracer
COPY Tools/MapFileGenerator Tools/MapFileGenerator
COPY Tools/CiReportGenerator Tools/CiReportGenerator

RUN dotnet publish Microwalk/Microwalk.csproj -c Release -o /publish/microwalk
RUN dotnet publish Microwalk.Plugins.PinTracer/Microwalk.Plugins.PinTracer.csproj -c Release -o /publish/microwalk
RUN dotnet publish Tools/MapFileGenerator/MapFileGenerator.csproj -c Release -o /publish/mapfilegenerator
RUN dotnet publish Tools/CiReportGenerator/CiReportGenerator.csproj -c Release -o /publish/CiReportGenerator



## RUNTIME ##

FROM mcr.microsoft.com/dotnet/runtime:6.0-focal

# Get some dependencies
RUN apt-get update -y && apt-get install -y \
  wget \
  make \
  g++ \
  dwarfdump \
  jq

# Copy Microwalk binaries
WORKDIR /mw/microwalk
COPY --from=build /publish/microwalk .
ENV MICROWALK_PATH=/mw/microwalk

# Copy MAP file generator binaries
WORKDIR /mw/mapfilegenerator
COPY --from=build /publish/mapfilegenerator .
ENV MAP_GENERATOR_PATH=/mw/mapfilegenerator

# Copy code quality report generator binaries
WORKDIR /mw/CiReportGenerator
COPY --from=build /publish/CiReportGenerator .
ENV CQR_GENERATOR_PATH=/mw/CiReportGenerator

# Get Pin SDK
WORKDIR /mw
RUN wget https://software.intel.com/sites/landingpage/pintool/downloads/pin-3.21-98484-ge7cd811fd-gcc-linux.tar.gz
RUN mkdir -p pin && tar -xf pin-3* -C pin --strip-components 1 && rm pin-3*
ENV PIN_PATH=/mw/pin

# Build Pin tool
WORKDIR /mw/pintool
COPY PinTracer .
RUN mkdir -p obj-intel64 && make PIN_ROOT=/mw/pin obj-intel64/PinTracer.so
ENV PINTOOL=/mw/pintool/obj-intel64/PinTracer.so

# Prepare working directory
RUN mkdir -p /mw/work
ENV WORK_DIR=/mw/work