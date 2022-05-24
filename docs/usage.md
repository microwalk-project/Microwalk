# Usage

Microwalk can be used both directly and in a Docker container. We recommend using our Docker images, which feature a preconfigured environment that brings all necessary components; see [Packages](https://github.com/microwalk-project/Microwalk/pkgs/container/microwalk) and [docker/README.md](/docker/README.md) for details.

If you just want to setup Microwalk with your CI using our predefined packages, refer to the [templates](/templates) directory.

This documentation focuses on analyzing compiled code locally with our Pin tracer backend. However, JavaScript analysis is quite similar. A good starting point are our JavaScript GitHub/GitLab templates, which only need slight modifications to run them locally.


## Preparation

While there are little restrictions with setting up Microwalk with a custom directory structure, we recommend storing the configuration in the library source within a `microwalk` directory. Please check out the [templates/c](/templates/c) directory for the full example.

Microwalk works as follows: It first executes a given _target_ (e.g., a cryptographic primitive) with a number of input files (test cases). For each test case, it collects the resulting execution trace. Then, these traces are preprocessed, i.e., brought into a generic format that is suitable for analysis. Finally, the preprocessed traces are fed into a number of analysis modules.

Thus, for each primitive that should be analyzed, you need to create a (small) source file that reads a test case from a file and then calls that primitive. In addition, you need a _wrapper_ that communicates both with the framework's main process and the Pin tool that collects the traces. This wrapper is located in the `microwalk/main.c` file in our template; you typically don't need to change it.

### Creating targets

Let's assume you want to analyze a primitive `example`, that consists of the function `some_function`. Each target source file must implement two functions:
- `void InitTarget(FILE* input)`: Takes a test case file and does all necessary initialization, such that the library behaves the same for all inputs (no late/lazy initialization). Called once in the beginning.
- `void RunTarget(FILE* input)`: Takes a test case file and calls the function of interest. Called for each test case.

Refer to the comments in the template for more details.

Finally, compile the targets into individual executables, e.g., `target-example`.

### Compiling the library

Compile the library using its usual build procedure. Ensure adding the `-g` flag for including debug information, else Microwalk may not be able to discover all symbols.

Finally, you should have one or multiple `*.so` files (shared libraries).

### Creating a Microwalk configuration

Microwalk takes a single [YAML configuration file](/docs/config.md), which specifies the respective analysis steps.
In most cases, the configuration file in the template should work just fine: It takes a directory with test cases (environment variable `TESTCASE_DIRECTORY`) and a target binary (`TARGET_NAME`), and runs the Pin-based toolchain and the control flow analysis for that target. The leakage analysis results end up in `$WORK_DIR/persist/$TARGET_NAME/results`.

If using Microwalk outside of a Docker environment, you may need to adjust the used environment variables and paths.

### Within the Docker container
After making the appropriate preparations, run the Microwalk Docker container with
```
mkdir /path/to/analysis/data
docker run -it -v /path/to/library/tree:/mw/library -v /path/to/analysis/results:/mw/work/persist ghcr.io/microwalk-project/microwalk:pin /bin/bash
```

Your library tree is now mounted at `/mw/library`, and results will be automatically written to `/path/to/analysis/data`.

#### Generating MAP files
Microwalk can parse a custom [MAP file format](/docs/mapfile.md), which allows it to map back addresses to function names. You can generate a MAP file for a binary by running
```
cd $MAP_GENERATOR_PATH
dotnet MapFileGenerator.dll libexample.so microwalk/libexample.map
```
As noted above, best results are achieved when the binary contains debug information.

There need to be MAP files for all relevant binaries, i.e., the library itself, and all targets.

#### Running Microwalk
Finally, you can run Microwalk for the desired target. For that, run the following inside the container:
```
export TESTCASE_DIRECTORY=/mw/library/microwalk/testcases/target-example
export TARGET_NAME=target-example

mkdir -p $WORK_DIR/work/$TARGET_NAME
mkdir -p $WORK_DIR/persist/$TARGET_NAME

cd $MICROWALK_PATH
dotnet Microwalk.dll /mw/library/microwalk/config.yml
```

## Troubleshooting

### Debug output
Generally, the first measure after getting an unclear error message is enabling debug logging. This can be easily achieved by setting `general.logger.log-level` to `debug` in the configuration file. This will also forward Pin messages to the Microwalk log, as most problems occur when running the Pin tracer.

### Dry-run targets
Test your targets without running Microwalk. The predefined wrapper has the following `stdin` interface for running a test case:
```
t <test case ID> <test case path>
```

The wrapper can be exited by writing `e`.