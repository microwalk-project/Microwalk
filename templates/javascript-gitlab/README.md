# JavaScript + GitLab

This template is based on the [Jalangi2 docker image](../../docker/jalangi2) and uses the Jalangi2 tracer backend to generate traces for JavaScript implementations. It then runs the control flow analysis module and produces a code quality report that can be displayed in GitLab.

## Structure

```
.gitlab-ci.yml            # GitLab CI configuration
index.js                  # Trace generation entrypoint script
package.json              # Analysis dependencies
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

1. Copy the entire template into the library source tree. Merge and adjust the CI configuration and package.json, if necessary.
2. Create `target-<NAME>.js` files in the `microwalk/` sub directory, where `<NAME>` corresponds to the respective analyzed primitive (e.g., `aes-ecb`). Each script should export a function `processTestcase(testcaseBuffer)`, which receives a buffer containing a test case and calls the respective primitive. The target code should be as minimal as possible and only focus on the functions of interest, to reduce trace length.
3. Create a number of static test cases in `microwalk/testcases/target-<NAME>/`. 16 is usually a good number.

When triggering the CI job, it calls the `analyze.sh` script, which in turn iterates through the targets present in the `microwalk/` directory and runs the analysis for all of them. Finally, it produces a number of result artifacts, which are passed back to GitLab:
- `call-stacks-target-<NAME>.txt`: The human-readable [control flow leakage](../../docs/control-flow-leakage.md) analysis reports for the respective target.
- `report-target-<NAME>.json`: Code quality report for the respective target.
- `report.json`: Merged code quality reports. This file is shown by GitLab.