# MicroWalk
*MicroWalk* is a microarchitectural leakage detection framework, that uses dynamic instrumentation to compare a given program's behaviour for a random set of test cases; if these execution traces differ, it tries to quantify the amount of leaked information. For a deeper explanation of its functionality, please consult the corresponding scientific paper. TODO link

## Structure
The software consists of the following modules:

- `Trace`: A so-called *Pintool* that uses Intel's dynamic instrumentation framework *Pin* to generate raw execution traces for a given executable. The resulting raw trace files are divided per test case.
- `FuzzingWrapper`: A template for the analyzed binary. This program receives the input test cases from the main executable `LeakageDetector`, and calls the target binary/functions to process each test case, while notifying the Pintool on each test case start and end
- `SampleLibrary`: A dummy target to test some of *MicroWalk*'s leakage detection capabilities. Only used for debugging.
- `LeakageDetector`: *MicroWalk*'s main program. It implements the leakage detection pipeline: Generate test cases, pass them to the `FuzzingWrapper` for execution trace generation, collect raw traces from Pintool `Trace`, preprocess these traces to remove unnecessary information and free disk space, and finally analyze the preprocessed traces for leakages.
- `Diff`: Auxiliary .NET wrapper for the C++ `dtl` diff library.
- `Visualizer`: Experimental tool for graphical trace comparison. Should only be used for small programs.

## Usage steps
Let's say we want to test the function `Encrypt(uint8_t *plain, int plainLength)` of a cryptographic library `crypto.dll`.

#### Preparing the environment
Before we can begin analyzing the library, we should select a working directory. In our tests we often used a RAM disk for this, so we pick `R:\` here.

In this working directory we create a folder `in` with a single file `0.txt` (arbitrary name) with an initial test case:

```
abcd1234abcd1234
```

#### Creating a wrapper executable
Since we want to analyze a library function, we need to provide a program that calls it. For this reason, we create a fresh copy of the `FuzzingWrapper` template; we only need to focus on the `RunTarget` function, that receives handle to a file containing the current test case. The library should not keep any state that changes between test cases (e.g. by using static variable), else the results might be unreliable.

In our case, the implementation of `RunTarget` looks like this:

```c++
__declspec(noinline) void RunTarget(FILE *input)
{
    // Read the test case (here we have 16 bytes of plain text per test case)
    unsigned char plain[16];
    if(fread(plain, 1, sizeof(plain), input) <= 0)
        return;
    
    // Run analyzed function
    Encrypt(plain, sizeof(plain));
}
```

We compile the program (linking with `crypto.dll`) and store the resulting executable `FuzzingWrapper.exe` and the library `crypto.dll` in our working directory `R:\`.

We can now test the wrapper by executing `FuzzingWrapper.exe 2` and typing

```
t 0
in\0.txt
e
```

If the program does not crash, everything is working fine. If we did any outputs in `RunTarget`, these need to be removed before proceeding to the next step.

*Note*: There is also a mode `1` for using *WinAFL* as a test case generator; it is still experimental and thus not documented here.

#### Compiling the toolchain
Before we can compile the *MicroWalk* toolchain, we need to configure the *Pin* path. Assuming that *Pin* is installed at `C:\pin\pin-3.5-97503-gac534ca30-msvc-windows`, we modify `Trace\PinSettings.props` to contain the correct path:

```xml
...
    <PinPath>C:\pin\pin-3.5-97503-gac534ca30-msvc-windows</PinPath>
...
```

Setting this path enables the compiler to find libraries and headers for building the Pintool. Additionally we add `pin.exe` to the system's  `PATH` variable, such that it can be called from anywhere without explicitely specifying its path.

We can then build the `Trace` and `LeakageDetector` applications (make sure to choose the correct bit-ness for compilation; for 32-bit targets all non-C# programs should be compiled as 32-bit too).

#### Detecting leakages
After successfully building the toolchain we only need to call `LeakageDetector` with the desired parameters. The command line interface is structured as follows:

```
LeakageDetector.exe command [optional-parameters] mandatory-parameters
```

A documentation of the different modes can be found in the next section.

## Command line parameters
The program supports the following commands:

- `run`: Calls the leakage detection pipeline, including test case generation, tracing and analysis
  - Mandatory 1: The name of the wrapper executable, e.g. `FuzzingWrapper.exe`
  - Mandatory 2: The name of the library file, e.g. `crypto.dll` (do not add any path information)
  - Mandatory 3: Working directory path, e.g. `R:\`
  - Mandatory 4: Result directory path, e.g. `R:\results\`
  - `-d`: Disable test case generation, use a set of pre-generated test cases from a directory `[workdir]\testcases\` instead
  - `-l num`: Set length of randomly generated test cases to `num` bytes
  - `-a num`: Set amount of randomly generated test cases to `num` 
  - `-t dir`: Set directory where pre-processed traces should be loaded from. This disables trace generation completely, and instead uses traces from a previous execution.
  - `-c num`: Emulate CPU `num`. This modifies the `cpuid` instruction, nothing else. The list of supported CPU numbers can be found in `Trace\Trace.cpp`.
  - `-r num`: Run each test case `num` times to suppress false positives due to randomization techniques used in the target binary (e.g. blinding in RSA).
  - `-m mode`: Sets the desired analysis mode.
    - 0: None (only generate and pre process traces, sets `-k ` switch automatically)
    - 1: Compare traces directly (compare traces entry by entry, terminate when finding differences)
    - 2: Compute mutual information over whole traces
    - 3: Compute mutual information over every trace prefix
    - 4: Compute mutual information for every memory accessing instruction
  - `-k`: Keep pre processed trace files (else they are deleted automatically to save disk space)
  - `-g num`: Set the memory address analysis granularity to `num` bytes (e.g. 64 to analyze cache line level leakages)
  - `-x num`: Set the output of the `rdrand` instruction to `num`. Allows to suppress randomization that uses the `rdrand` instruction.
- `dump`: Convert a pre processed trace file into text format
  - Mandatory 1: The pre processed trace file to be dumped.
  - `-o file`: The name of the output text file (else the trace will be dumped to `stdin`)
  - `-c addr`: Dump all call traces for instruction `addr`
  - `-r`: Print relative addresses (e.g. `crypto.dll:Encrypt+3F2`) instead of absolute ones (e.g. `crypto.dll+4033F2`)
  - `-m file1 file2 ...`: Use the given linker MAP files to resolve function addresses to names.
  
## License
*MicroWalk* is provided under the MIT license; please consult the LICENSE file for further information.
  
#### 3rd party libraries
*MicroWalk* uses the following third party libraries (included as Git submodules):
- [dtl](https://github.com/cubicdaiya/dtl): C++ library for computing diffs
- [WpfPlus](https://github.com/MarcusWichelmann/WpfPlus): Provides simple flat designs for WPF user interfaces