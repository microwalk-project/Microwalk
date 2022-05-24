# C + GitHub

This template is based on the [Pin docker image](../../docker/pin) and uses the Pin tracer backend to generate traces for compiled code. It then runs the control flow analysis module and produces a SARIF report that can be displayed in GitHub.

## Structure

```
.github/
  workflows/
    microwalk.yml         # GitHub CI configuration
src/                      # Library source code
microwalk/
  analyze.sh              # Analysis script
  build.sh                # Build script for target code
  config.yml              # Microwalk configuration
  main.c                  # Analysis entrypoint
  target-example.c        # Example target
  testcases/
    target-example/
	  ...                 # Test cases
```

## Usage

Using the template with a C library generally requires the following steps:

1. Copy the entire template into the library source tree.
2. Update the CI configuration to include a build/install step, if necessary; also, update the `microwalk/build.sh` script accordingly.
3. Adjust the Microwalk configuration in `microwalk/config.yml` to use the correct library name(s) (grep for `libexample`). You may also supply additional environment variables or tweak the Pin trace configuration. For more information, refer to the [configuration file documentation](/docs/config.md).
4. Create `target-<NAME>.c` files in the `microwalk/` sub directory, where `<NAME>` corresponds to the respective analyzed primitive (e.g., `aes-ecb`). Each target should export two functions `void InitTarget(FILE* input)` and `void RunTarget(FILE* input)`, which each receive a buffer containing a test case and should call the respective library functionality. The target code should be as minimal as possible and only focus on the functions of interest, to reduce trace length and increase analysis accuracy. More details are in the comments in the `target-example.c` file.
5. Create a number of static test cases in `microwalk/testcases/target-<NAME>/`. 16 is usually a good number.

When triggering the CI job, it first calls the `build.sh` script to compile the targets. Afterwards, it runs the `analyze.sh` script, which iterates through the targets present in the `microwalk/` directory and runs the analysis for each of them. Finally, the analysis script produces a number of result artifacts, which are passed back to GitHub:
- `call-stacks-target-<NAME>.txt`: The human-readable [control flow leakage](../../docs/control-flow-leakage.md) analysis reports for the respective target.
- `report-target-<NAME>.sarif`: SARIF report for the respective target.
- `report.sarif`: Merged SARIF reports. This file is shown by GitHub.

## Example

See [example-c](https://github.com/microwalk-project/example-c) repository.