# JavaScript + GitHub

This template is based on the [Jalangi2 docker image](../../docker/jalangi2) and uses the Jalangi2 tracer backend to generate traces for JavaScript implementations. It then runs the control flow analysis module and produces a SARIF report that can be displayed in GitHub.

## Structure

```
index.js                  # Trace generation entrypoint script
package.json              # Analysis dependencies
.github/
  workflows/
    microwalk.yml         # GitHub CI configuration
microwalk/
  analyze.sh              # Analysis script
  config-preprocess.yml   # Microwalk configuration for trace preprocessing
  config-analyze.yml      # Microwalk configuration for analysis
  target-example.js       # Example target
  testcases/
    target-example/
	  ...                 # Test cases
```

## Usage

Using the template with a JS library generally only requires a few simple steps.

1. Copy the entire template into the library source tree. Merge and adjust the package.json, if necessary.
2. Update the CI configuration to include a build step, if necessary.
3. Create `target-<NAME>.js` files in the `microwalk/` sub directory, where `<NAME>` corresponds to the respective analyzed primitive (e.g., `aes-ecb`). Each script should export a function `processTestcase(testcaseBuffer)`, which receives a buffer containing a test case and calls the respective primitive. The target code should be as minimal as possible and only focus on the functions of interest, to reduce trace length.
4. Create a number of static test cases in `microwalk/testcases/target-<NAME>/`. 16 is usually a good number.

When triggering the CI job, it calls the `analyze.sh` script, which in turn iterates through the targets present in the `microwalk/` directory and runs the analysis for all of them. Finally, it produces a number of result artifacts, which are passed back to GitHub:
- `call-stacks-target-<NAME>.txt`: The human-readable [control flow leakage](../../docs/control-flow-leakage.md) analysis reports for the respective target.
- `report-target-<NAME>.sarif`: SARIF report for the respective target.
- `report.sarif`: Merged SARIF reports. This file is shown by GitHub.