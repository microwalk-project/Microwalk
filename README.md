# Microwalk

Microwalk is a microarchitectural leakage detection framework, which combines dynamic instrumentation and statistical methods in order to identify and quantify side-channel leakages. For the scientific background, consult the corresponding [paper](https://arxiv.org/abs/1808.05575).


## Compiling

For Windows, it is recommended to install Visual Studio, as it brings almost all dependencies and compilers, as well as debugging support. The solution can then be built directly in the IDE.

The following guide is mostly for Linux systems and command line builds on Windows.

### Main application

The main application is based on [.NET 5.0](https://dotnet.microsoft.com/download/dotnet/5.0), so the .NET 5.0 SDK is required for compiling.

Compile (optional):
```
cd Microwalk
dotnet build -c Release
```

Run (compiles and executes; if you compile manually, you can suppress compiliation with `--no-build`):
```
cd Microwalk
dotnet run -c Release <args>
```

The command line arguments `<args>` are documented in Section "[Configuration](#configuration)"

### Pin tool

Microwalk comes with a Pin tool for instrumenting and tracing x86 binaries. Building the Pin tool requires the [full Pin kit](https://software.intel.com/content/www/us/en/develop/articles/pin-a-binary-instrumentation-tool-downloads.html), preferably the latest version. It is assumed that Pin's directory path is contained in the variable `$pinDir`.

**When building through Visual Studio**: Edit [Settings.props](PinTracer/Settings.props) to point to the Pin directory.

Compile:
```
cd PinTracer
make PIN_ROOT="$pinDir" obj-intel64/PinTracer.so
```

Run (assuming the `pin` executable is in the system's `PATH`):
```
pin -t PinTracer/obj-intel64/PinTracer.so -o /path/to/output/file -- /path/to/wrapper/executable
```

Note that the above run command is needed for testing/debugging only, since `Microwalk` calls the Pin tool itself.

### Pin wrapper executable

In order to efficiently generate Pin-based trace data, Microwalk needs a special wrapper executable which interactively loads and executes test cases. The `PinTracerWrapper` project contains a skeleton program with further instructions ("`/*** TODO ***/`").

The wrapper skeleton is C++-compatible and needs to be linked against the target library. It works on both Windows and Linux (GCC).

Alternatively, it is also possible to use an own wrapper implementation, as long as it exports the Pin notification functions and correctly handles `stdin`.

## Running Microwalk

The general steps for analyzing a library with Microwalk are:

1. Copy and adjust the `PinTracerWrapper` program to load the investigated library, and read and execute test case files. It is advised to test the wrapper with a few dummy test cases, and use debug outputs to verify its correctness. Make to sure to remove these debug outputs afterwards, else they may clutter the I/O pipe which Microwalk uses for communication with the dynamic instrumentation framework, and lead to errors.

2. Create a custom test case generator module, or check whether the built-in ones are able to yield the expected input formats. Guidelines for adding custom framework modules can be found in the section "[Creating own framework modules](#creating-own-framework-modules)".

3. Compose a configuration file which describes the steps to be executed by Microwalk.

### Configuration

Microwalk takes the following command line arguments:

- `-p <plugin directory>` (optional)<br>
  A directory containing plugin binaries. This needs to be specified when the configuration references a plugin that is not in Microwalk's main build directory.
  
- `<configuration file>` (mandatory)<br>
  The path to a [YAML-based configuration file](docs/config.md).

## Creating own framework modules

Follow these steps to create a custom framework plugin with a new module:
1. Create a new project `MyPlugin` and add a reference to the `Microwalk.FrameworkBase` project.

2. Create a class `PluginMain` which derives from `Microwalk.FrameworkBase.PluginBase`. In this class, you need to override the `Register()` function (see step 5).

3. Create a class `MyModule` for your new module, which inherits from `XXXStage` and has a `[FrameworkModule(<name>, <description>)]` attribute. `XXXStage` here corresponds to one of the framework's pipeline stages:
    - `TestcaseStage`: Produces a new testcase on each call.
    - `TraceStage`: Takes a testcases and generates raw trace data.
    - `PreprocessorStage`: Takes raw trace data and preprocesses it.
    - `AnalysisStage`: Takes preprocessed trace data and updates its internal state for each trace. Yields an analysis result once the finish function is called.
    
4. Implement the module logic.

5. Register the module by calling `XXXStage.Factory.Register<MyModule>()` in `PluginMain.Register()`.

6. Compile the plugin project.

7. Run Microwalk and pass the plugin's build folder via the `-p` command line switch.

Look into the `Microwalk.Plugins.PinTracer` project for some examples.

## Contributing

Contributions are appreciated! Feel free to submit issues and pull requests.

## License

The entire system is licensed under the MIT license. For further information refer to the [LICENSE](LICENSE) file.
