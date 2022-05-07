# Configuration

Microwalk reads its entire run configuration from a single YAML file. The file specifies the loaded pipeline modules and their settings.

The pipeline is structured as follows: First, the `testcase` stage produces/loads a number of test case files (= inputs), which are passed to the `trace` stage.
The `trace` stage uses these test cases to generate raw execution traces using a language-specific backend (e.g., Pin for x86 binaries or Jalangi2 for JavaScript).
Then, the raw traces are passed to the `preprocess` stage, that parses the raw traces and converts them into Microwalk's generic trace format. The resulting generic
traces can finally be analyzed by various `analysis` modules.

A configuration file generally looks as follows:
```yaml
# Configuration preprocessor section (optional)
base-file: base-file.yml
constants:
  CONSTANT_NAME: some value
  
---

# Actual configuration
general:
  logger:
    # Logger settings

testcase:
  module: # Test case generator module name
  module-options:
    # Test case generator module options

trace:
  module: # Trace module name
  module-options:
    # Trace module options
  options:
    # General trace stage options

preprocess:
  module: # Preprocessor module name
  module-options:
    # Preprocessor module options
  options:
    # General preprocessor stage options

analysis:
  modules: # List of analysis module settings
    - module: # 1st analysis module name
      module-options:
        # 1st analysis module options

    - module: # 2nd analysis module name
      module-options:
        # 2nd analysis module options

    # ...
  options:
    # General analysis stage options
```
A valid configuration file must specify at least one module for each stage.


## Preprocessor

Microwalk has a simple configuration preprocessor that supports environment variables, constants and loading other configuration files/parts. 
The preprocessor is executed before handling the configuration itself.

### `base-file`
Base configuration file which should be loaded and parsed before the current one (nesting is supported).
Keys existing in both the base file and the current file are always superseded by those of the current file.

### `constants`
A number of arbitrary string constants, which can be referenced by `$$CONSTANT_NAME$$` in other parts of the file.

Predefined constants:
- `CONFIG_PATH`<br>
  Absolute path of the directory where the main configuration file resides.

- `CONFIG_FILENAME`<br>
  Name of the main configuration file, without extension (e.g., `target-aes` for `target-aes.yml`).

### Enviroment variables
All environment variables of the current process environment are available as `$$$VARIABLE_NAME$$$` and are treated just like constants.


## `general`

### `logger`

Controls the logger.

- `log-level` (optional)<br>
  Sets the minimal log level messages need in order to be printed to stdout.
  
  Allowed values:
  - `debug`
  - `warning`
  - `error` (default)
  
  It is recommended to test the configuration file with `warning` level enabled, as some modules may report possible misconfiguration as warnings.

- `file` (optional)<br>
  Output file for the log. This file will receive the same log messages as stdout.

### `monitor` (optional)

Configures process monitoring.

Currently, this does only track total memory usage.

- `enable` (optional)<br>
  If set to `true`, enables process monitoring. If this is `false`, all other monitoring options are ignored.
  
  Default: `false`

- `sample-rate` (optional)<br>
  Sets the sample rate (milliseconds) for querying process statistics.
  
  Default: 500


## `testcase`

### Module: `load`

Loads existing test case files (`*.testcase`) from a given directory.

Options:
- `input-directory`<br>
  Input directory containing test case files.

### Module: `random`

Generates random byte arrays of a given length and stores them as test cases.

Options:
- `length`<br>
  Amount of bytes per test case.
  
- `amount`<br>
  Number of test cases.
  
- `output-directory`<br>
  Output directory for generated test cases.

### Module: `command`

Calls an external application to generate test cases.
The given command is executed for each test case.

The working directory is set to `output-directory`.

Options:
- `amount`<br>
  Number of test cases.
  
- `output-directory`<br>
  Output directory for generated test cases.

- `exe`<br>
  Path/name of the program to be called.

  Example: `openssl`

- `args`<br>
  Format string for the program argument. The following placeholders are available:
  - `{0}`: Integer test case ID
  - `{1}`: Name of the test case file
  - `{2}`: Full path and name of the test case file

  Example: `genrsa -out {1} 2048`
  
  The above examples would yield the following command line: `openssl genrsa -out 0.testcase 2048`

## `trace`

General options:
- `input-buffer-size` (optional)<br>
  Size of trace stage input buffer. This controls the number of active pending test cases which are not currently processed by the next stage.
  Since test case objects are quite small, buffering a few does not cause much harm; especially, if test case generation takes a bit (e.g., random asymmetric keys).
  
  Default: 1

- `max-parallel-threads` (optional)<br>
  Amount of concurrent trace threads. This is only applied when the selected trace module supports parallelism.

  Default: 1


### Module: `load`

Loads existing raw traces from a given directory. This module tries to compute the trace file names from test case IDs, and makes the following assumptions:
- The testcases are loaded using the `load` module;
- The trace files have not been renamed, i.e. their names follow the `t<ID>.trace` format.

Options:
- `input-directory`<br>
  Input directory containing trace files.

### Module: `passthrough`

Passes through the test cases without generating traces. This module is designed to be used in conjunction with the preprocessed trace loader, where raw traces are not needed.

### Module: `pin` [PinTracer]

Generates traces using a Pin tool.

Options:
- `pin-tool-path`<br>
  Path to the compiled Pin tool (`PinTracer` binary).
  
- `wrapper-path`<br>
  Path to the wrapper executable (based on `PinTracerWrapper`).
  
- `output-directory`<br>
  Output directory for raw traces.
  
- `images`<br>
  List of names of interesting images (= binaries). Only instructions from these images are traced; all others are ignored.

  Example:
  ```yaml
  images:
    - wrapper.exe
    - mylibrary.dll
  ```
  
- `pin-path` (optional)<br>
  Path to the `pin` executable.
  
  Default: `pin`
  
- `stack-tracking` (optional)<br>
  Enable tracking of stack allocations and deallocations. Enabling this setting allows the preprocessor to assign memory accesses to specific stack frames.

  Default: `false`

- `rdrand` (optional)<br>
  Constant value to use as output of the x86 `rdrand` instruction.
  
  Expects a 64-bit hex number, e.g. `0x0000ffff0000ffff`.

- `cpu` (optional)<br>
  Simulated CPU model. This can be used to trigger specific code paths in a library, which depend on the availability of certain CPU features.
  
  Currently supported processors and microarchitectures:
  - `0` (default): CPU as used in the host system.
  - `1`: [(Intel) Pentium III](https://en.wikipedia.org/wiki/Pentium_III)
  - `2`: [(Intel) Merom](https://en.wikipedia.org/wiki/Merom_(microprocessor))
  - `3`: [(Intel) Westmere](https://en.wikipedia.org/wiki/Westmere_(microarchitecture))
  - `4`: [(Intel) Ivy Brigde](https://en.wikipedia.org/wiki/Ivy_Bridge_(microarchitecture))

- `stack-tracking` (optional)<br>
  Enable stack tracking. This is an experimental feature for tracking individual stack frames, instead of referencing the stack as a whole.
  
  Default: `false`
  
- `environment` (optional)<br>
  A list of enviroment variables which should be passed to the process.
  

## `preprocess`

General options:
- `input-buffer-size` (optional)<br>
  Size of preprocessor stage input buffer. This throttles the trace generator: Raw traces can be quite large and take up a lot of disk space.
  If only existing raw traces are loaded from disk, this setting does not significantly influence execution.
  
  Default: 1

- `max-parallel-threads` (optional)<br>
  Amount of concurrent trace threads. This is only applied when the selected preprocessor module supports parallelism.

  Default: 1

### Module: `load`

Loads existing preprocessed traces from a given directory. This module tries to compute the trace file names from test case IDs, and makes the following assumptions:
- The testcases are loaded using the `load` module;
- The preprocessed trace files have not been renamed, i.e. their names follow the `t<ID>.trace.preprocessed` format.

Raw traces are ignored, thus it is recommended to use the `passthrough` module for the trace stage.

Options:
- `input-directory`<br>
  Input directory containing preprocessed trace files.

### Module: `passthrough`

Passes through the test cases and raw traces without preprocessing. This module should only be used with an empty (`passthrough`) analysis stage, since no preprocessed traces are inserted into the pipeline.


### Module: `pin` [PinTracer]

Preprocesses raw traces generated with the Microwalk Pin tracer backend.

Options:
- `store-traces` (optional)<br>
  Controls whether preprocessed traces are written to the file system. If set to `false`, preprocessed traces are only kept in memory and are discarded after the analysis has finished.
  
  Default: `false`
  
- `output-directory` (optional)<br>
  Output directory for preprocessed traces. Must be set when `store-traces` is `true`.

- `keep-raw-traces` (optional)<br>
  Controls whether raw traces are kept after preprocessing has completed. Deleting raw traces may free up disk space.
  
  Default: `false`

### Module: `pin-dump` [PinTracer]

Dumps raw Pin trace files in a human-readable form. Primarily intended for debugging.

Options:
- `output-directory`<br>
  Output directory for trace text files.

### Module: `js` [JavascriptTracer]

Preprocesses raw traces generated with the Microwalk Jalangi2 tracer backend.

Options:
- `store-traces` (optional)<br>
  Controls whether preprocessed traces are written to the file system. If set to `false`, preprocessed traces are only kept in memory and are discarded after the analysis has finished.
  If set to `true`, preprocessed traces are streamed to binary files and are not kept in memory -- thus, analysis needs to load them again.
  
  Default: `false`
  
- `output-directory` (optional)<br>
  Output directory for preprocessed traces. Must be set when `store-traces` is `true`.
  
- `map-directory` (optional)<br>
  Output directory for MAP files.

- `columns-bits` (optional)<br>
  Number of bits used for encoding a column number in a 32-bit integer. The remaining bits are used for encoding a line number.
  
  The default value should work for reasonable settings; however, when dealing with extreme cases (e.g., a minified library residing in a single, very long line),
  make sure to adjust this in order to avoid erroneous traces.

  Default: 13
  

## `analysis`

General options:
- `input-buffer-size` (optional)<br>
  Size of analysis stage input buffer. Note that the buffer only contains _pending_ preprocessed traces, i.e., in addition to the ones that are already processed by the analysis stage(s).
  
  Default: 1

- `max-parallel-threads` (optional)<br>
  Amount of concurrent analysis threads. This is only applied when the selected analysis module supports parallelism.

  Default: 1
  
### Module: `passthrough`

Ignores all passed traces. Intended for "offline" trace generation/preprocessing without immediate analysis.


### Module: `dump`

Dumps preprocessed trace files in a human-readable form.

Options:
- `output-directory`<br>
  Output directory for trace text files.

- `include-prefix` (optional)<br>
  Controls whether the trace prefix is added to each text file. This will yield complete trace dumps, but will also take up more disk space.
  
  Default: `false`

- `map-files` (optional)<br>
  A list of [MAP files](docs/mapfile.md) which contain a mapping of image offsets to symbol names.

  Example:
  ```yaml
  map-files:
    - wrapper.exe.map
    - mylibrary.dll.map
  ```

- `map-directory` (optional)<br>
  Path to a directory containing [MAP files](docs/mapfile.md). This loads all files that end with `.map` from the given directory, in addition to the ones specified manually through the
  `map-files` key.

- `skip-memory-accesses` (optional)<br>
  Controls whether memory accesses should be skipped when writing the trace dump.
  
  Default: `false`

- `skip-jumps` (optional)<br>
  Controls whether "jump" branches should be skipped when writing the trace dump.
  
  Default: `false`

- `skip-returns` (optional)<br>
  Controls whether "return" branches should be skipped when writing the trace dump.
  
  Default: `false`

### Module: `instruction-memory-access-trace-leakage`

Calculates several trace leakage measures for each memory accessing instruction.

*This is a legacy module. Use the `call-stack-memory-access-trace-leakage`, which is better optimized and yields more detailed results.*

Options:
- `output-directory`<br>
  Output directory for analysis results.
  
- `output-format` (optional)<br>
  The output format of analysis results.
  
  Supported formats:
  - `txt`: Formats each analysis result as a line in a text file.
  - `csv` (default): Creates a CSV file, where lines are instructions and columns are the results of the different measures.
  
  In addition, a 

- `dump-full-data` (optional)<br>
  Writes the entire final state of the analysis module into a separate output file. This may be useful for identifying the specific test cases which generated a particular trace and analysis result.
  
  Default: `false`

- `map-files` (optional)<br>
  A list of [MAP files](docs/mapfile.md) which contain a mapping of image offsets to symbol names.

  Example:
  ```yaml
  map-files:
    - wrapper.exe.map
    - mylibrary.dll.map
  ```

### Module: `call-stack-memory-access-trace-leakage`

Calculates several trace leakage measures for each memory accessing instruction and call stack.

This module yields more accurate results than the `instruction-memory-access-trace-leakage` module:
The leakage measures are computed over each unique combination of instruction and call stack, instead of only considering instructions.

Additionally, the `dump-full-data` mode outputs detailed information over the encountered call stacks and their respective hit counts.

Note that a leakage shown for a given memory access does not imply that this access is non-constant-time: The leakage may have also been caused by a control flow variation higher up in the call chain.
Thus, while this module is quite fast due to its focus on memory access traces, it fails at accurately localizing and attributing leakages. If possible, we recommend using the `control-flow-leakage`
module, which needs a bit more resources, but yields very accurate leakage assessments.

Options:
- `output-directory`<br>
  Output directory for analysis results.
  
- `output-format` (optional)<br>
  Output format of the leakage report.
  
  Supported formats:
  - `txt`: Formats each analysis result as a line in a text file.
  - `csv` (default): Creates a CSV file, where lines are instructions and columns are the results of the different measures.
  
  In addition, text/CSV files containing information about the detected call stacks are generated.

- `dump-full-data` (optional)<br>
  Writes the entire final state of the analysis module into a separate output file. This may be useful for identifying the specific test cases which generated a particular trace and analysis result.
  
  Default: `false`

- `map-files` (optional)<br>
  A list of [MAP files](docs/mapfile.md) which contain a mapping of image offsets to symbol names.

  Example:
  ```yaml
  map-files:
    - wrapper.exe.map
    - mylibrary.dll.map
  ```

### Module: `control-flow-leakage`

Merges all preprocessed traces into a call tree, while encoding trace divergences as "splits" in the tree structure.

This encoding enables an accurate analysis that manages to attribute leakages to individual branch/memory access instructions. In addition, it yields a rough assessment of the leakage severity
through several measures.

The implementation of this module is highly optimized, so it should work on a typical machine for reasonable workloads.

A guide for interpreting the generated reports can be found [here](control-flow-leakage.md).

Options:
- `output-directory`<br>
  Output directory for analysis results.
  
- `map-files` (optional)<br>
  A list of [MAP files](docs/mapfile.md) which contain a mapping of image offsets to symbol names.

  Example:
  ```yaml
  map-files:
    - wrapper.exe.map
    - mylibrary.dll.map
  ```

- `map-directory` (optional)<br>
  Path to a directory containing [MAP files](docs/mapfile.md). This loads all files that end with `.map` from the given directory, in addition to the ones specified manually through the
  `map-files` key.

- `include-testcases-in-call-stacks` (optional)<br>
  Controls whether test case ID trees should be included in the analysis result. While those are not strictly necessary for interpreting a reported leakages, they may help by specifying
  which input led to which behavior.
  
  Default: `true`

- `dump-call-tree` (optional)<br>
  If enabled, this dumps the entire call tree to a text file in the output directory.
  
  This feature is mostly intended for debugging: The call tree dump is quite verbose and thus very large.
  
  Default: `false`

- `include-memory-accesses-in-dump` (optional)<br>
  Controls whether memory accesses should be included in the call tree dump. If this is set to `false`, only branches are written to the dump.
  
  Default: `true`