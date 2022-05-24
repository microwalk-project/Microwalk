# C

This minimal template is based on the [Pin docker image](../../docker/pin) and uses the Pin tracer backend to generate traces for compiled code. It then runs the control flow analysis module and produces a leakage report.

## Structure

```
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

See [documentation](/docs/usage.md).