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
  CiReportGenerator/            # Tool for generating GitLab-compatible code
                                #   quality reports from the analysis result
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

## Environment variables

### All images

#### `WORK_DIR` (value: `/mw/work`)
Path to a working directory. This is where intermediate and result files should be stored. The path and this variable are never changed, so they can be safely used for mounting Docker volumes.

#### `MICROWALK_PATH`
Path to Microwalk binaries (both main application and plugins).

#### `MAP_GENERATOR_PATH`
Path to MAP file generator binaries.

#### `CQR_GENERATOR_PATH`
Path to CI report generator binaries.


### Jalangi2

#### `JALANGI2_PATH`
Path to Jalangi2 repository, with all dependencies installed.

#### `TRACE_TOOL`
Path to analysis tool which is used with Jalangi2.


### Pin

#### `PIN_PATH`
Path to Pin tree. The Pin executable is at `$PIN_PATH/pin`.

#### `PINTOOL`
Path to the Pintool binary (`PinTracer.so`).


## Usage

In general, one can create a container from a given image by running
```
docker run microwalk/microwalk-<NAME>
```

One may then copy/pull the relevant implementation and run the various tools present in the container.

In [templates/](../templates) there are several templates for generic analysis tasks, which can be adapted to the specific use cases. See also [docs/usage.md](/docs/usage.md) for a tutorial for locally analyzing a C library with Microwalk.