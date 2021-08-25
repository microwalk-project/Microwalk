# Configuration

Microwalk reads its entire run configuration from a single YAML file. This file specifies the loaded modules and their settings.

A configuration file generally looks as follows:
```yaml
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


## `general`

### `logger`

Controls the logger.

- `log-level` (optional)<br>
  Sets the minimal log level messages need in order to be printed to stdout.
  
  Allowed values:
  - `debug`
  - `warning`
  - `error` (default)
  
  It is recommended to test the config file with `warning` level enabled, as some modules may report possible misconfiguration as warnings.

- `file` (optional)<br>
  Output file for the log. This file will receive the same log messages as stdout.


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
  Size of trace stage input buffer. This controls the test case generator output speed, as new test cases can only be queued when there is room in the buffer.
  
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

### Module: `pin`

Generates traces using a Pin tool.

Options:
- `pin-tool-path`<br>
  Path to the compiled Pin tool (`PinTracer` binary).
  
- `wrapper-path`<br>
  Path to the wrapper executable (based on `PinTracerWrapper`).
  
- `output-directory`<br>
  Output directory for raw traces.
  
- `images`<br>
  List of interesting images. Only instructions from these images are traced; all others are ignored.

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


## `preprocess`

General options:
- `input-buffer-size` (optional)<br>
  Size of preprocessor stage input buffer. This throttles the trace generator: Raw traces can be quite large and take up a lot of disk space.
  
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

### Module: `pin-dump`

Dumps raw Pin trace files in a human-readable form. Primarily intended for debugging.

Options:
- `output-directory`<br>
  Output directory for trace text files.

### Module: `pin`

Preprocesses traces generated with the Pin tool.

Options:
- `store-traces` (optional)<br>
  Controls whether preprocessed traces are written to the file system. If set to `false`, preprocessed traces are kept in memory, until the analysis has finished, and are then discarded.
  
  Default: `false`
  
- `output-directory` (optional)<br>
  Output directory for preprocessed traces. Must be set when `store-traces` is `true`.

- `keep-raw-traces` (optional)<br>
  Controls whether raw traces are kept after preprocessing has completed. Deleting raw traces may significantly free up disk space.
  
  Default: `false`

### Module: `passthrough`

Passes through the test cases and raw traces without preprocessing. This module should only be used with an empty (`passthrough`) analysis stage, since no preprocessed traces are inserted into the pipeline.

## `analysis`

General options:
- `input-buffer-size` (optional)<br>
  Size of analysis stage input buffer.
  
  Default: 1

- `max-parallel-threads` (optional)<br>
  Amount of concurrent analysis threads. This is only applied when the selected analysis module supports parallelism.

  Default: 1

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

### Module: `instruction-memory-access-trace-leakage`

Calculates several trace leakage measures for each memory accessing instruction.

Options:
- `output-directory`<br>
  Output directory for analysis results.
  
- `output-format` (optional)<br>
  The output format of analysis results.
  
  Supported formats:
  - `txt`: Formats each analysis result as a line in a text file.
  - `csv` (default): Creates a CSV file, where lines are instructions and columns are the results of the different measures.

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

Options: *See `instruction-memory-access-trace-leakage` module`

### Module: `passthrough`

Ignores all passed traces. Primarily intended for debugging.