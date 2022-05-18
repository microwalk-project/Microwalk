# Microwalk Docker Images

In order to ease integration of Microwalk into active development, there is now a number of Docker images that contain all necessary dependencies for a given workflow.

## Compiling

To build an image from one of the sub directories (`<NAME>`), run the following commands from Microwalk's root directory:
```
# Pull most recent compile base image
docker pull mcr.microsoft.com/dotnet/sdk:6.0-alpine

# Pull most recent runtime base image
docker pull mcr.microsoft.com/dotnet/runtime:6.0-focal

# Build image
docker build -f docker/<NAME>/Dockerfile -t microwalk/microwalk-<NAME>:latest .
```

## File structure

The base structure of each image is as follows:
```
/mw/                            # Base folder
  microwalk/                    # Microwalk binaries (including relevant plugins)
  mapfilegenerator/             # Tool for generating MAP files from binaries
  CiReportGenerator/   # Tool for generating GitLab-compatible code
                                    quality reports from the analysis result
  work/                         # Working directory (for traces, results etc.)
```

Depending on the tracing backend, the individual images have a few more folders.

### Jalangi2 (JavaScript)

```
/mw/
  jalangi2/                     # Jalangi2 runtime
  jstracer/
    analysis.js                 # Analysis script
```

### Pin (x86-64 binaries)

```
/mw/
  pin/                          # Pin SDK
    pin
  pintool/                      # Pin tool
    obj-intel64/                # Pin tool binaries
	  PinTracer.so
```

## Usage

In general, one can create a container from a given image by running
```
docker run microwalk/microwalk-<NAME>
```

One may then copy/pull the relevant implementation and run the various tools present in the container.

In [templates/](../template) there are several templates for generic analysis tasks, which can be adapted to the specific use cases.