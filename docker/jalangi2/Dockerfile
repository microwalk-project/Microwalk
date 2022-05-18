
## BUILD ##
FROM mcr.microsoft.com/dotnet/sdk:6.0-alpine AS build

WORKDIR /src

COPY *.props .
COPY *.sln* .
COPY *.cs .
COPY Microwalk Microwalk
COPY Microwalk.FrameworkBase Microwalk.FrameworkBase
COPY Microwalk.Plugins.JavascriptTracer Microwalk.Plugins.JavascriptTracer
COPY Tools/MapFileGenerator Tools/MapFileGenerator
COPY Tools/CiReportGenerator Tools/CiReportGenerator

RUN dotnet publish Microwalk/Microwalk.csproj -c Release -o /publish/microwalk
RUN dotnet publish Microwalk.Plugins.JavascriptTracer/Microwalk.Plugins.JavascriptTracer.csproj -c Release -o /publish/microwalk
RUN dotnet publish Tools/MapFileGenerator/MapFileGenerator.csproj -c Release -o /publish/mapfilegenerator
RUN dotnet publish Tools/CiReportGenerator/CiReportGenerator.csproj -c Release -o /publish/CiReportGenerator


## RUNTIME ##

FROM mcr.microsoft.com/dotnet/runtime:6.0-focal

# Get some dependencies
RUN apt-get update -y && apt-get install -y \
  wget \
  git \
  jq
RUN wget -q -O - https://deb.nodesource.com/setup_17.x | bash -
RUN apt-get install -y nodejs

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

# Get Jalangi2 code
WORKDIR /mw
RUN git clone --depth 1 https://github.com/Samsung/jalangi2.git
WORKDIR /mw/jalangi2
RUN npm install
ENV JALANGI2_PATH=/mw/jalangi2

# Copy analysis script
WORKDIR /mw/jstracer
COPY JavascriptTracer .
ENV TRACE_TOOL=/mw/jstracer/analysis.js

# Prepare working directory
RUN mkdir -p /mw/work
ENV WORK_DIR=/mw/work