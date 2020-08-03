Note: Microwalk is currently being reimplemented to achieve better portability (up to now it worked on Windows only). The former (paper) version can be found in the [old/ sub directory](old/).

# Microwalk

## Compiling

For Windows, it is recommended to install Visual Studio, as it brings almost all dependencies and compilers, as well as debugging support.

The following guide is for Linux systems and command line builds on Windows.

### Main application

The main application is based on [.NET Core 3.1](https://dotnet.microsoft.com/download/dotnet-core/3.1), so the .NET Core 3.1 SDK is required for compiling.

Compile command:
```
cd Microwalk
dotnet build -c Release
```

Run command:
```
cd Microwalk
dotnet run <args>
```

The command line arguments `<args>` are documented below.

### Pin tool

Microwalk comes with a Pin tool for instrumenting and tracing x86 binaries. Building the Pin tool requires the [full Pin kit](https://software.intel.com/content/www/us/en/develop/articles/pin-a-binary-instrumentation-tool-downloads.html), preferably the latest version. It is assumed that Pin's directory path is contained in the variable `$pinDir`.

Compile command:
```
cd PinTracer
make PIN_ROOT="$pinDir" obj-intel64/PinTracer.so
```

Run command (assuming the `pin` executable is in the system's `PATH`):
```
pin -t PinTracer/obj-intel64/PinTracer.so -o /path/to/output/file -- /path/to/wrapper/executable
```

Note that the above run command is needed for testing/debugging only, since `Microwalk` calls the Pin tool itself.

### Pin wrapper executable

In order to efficiently generate Pin-based trace data, Microwalk needs a special wrapper executable which interactively loads and executes test cases. The `PinTracerWrapper` project contains a skeleton program with further instructions ("`/*** TODO ***/`").

The wrapper skeleton is C++-compatible and needs to be linked against the target library. It works on both Windows and Linux (GCC).

Alternatively, it is also possible to use an own wrapper implementation, as long as it exports the Pin notification functions and correctly handles `stdin`.