# Microwalk with features

## Introduction

Microwalk "with features" is a tool based on the [Microwalk](https://github.com/microwalk-project/Microwalk.git) gitlab-CI template for JavaScript libraries which is a microarchitectural leakage detection framework that combines dynamic instrumentation and statistical methods to locate and quantify side-channel leaks in libraries. Microwalk needs to be run in a continuous integration (CI) pipeline such as GitHub Actions or GitLab-CI.

The goal of this tool is to encourage developers to analyze and fix their code at development time by improving both the readability of Microwalk report and the usability of the framework itself.

In the different parts below, we will focus on:

* [Installation](#installation)
* [Difference with Microwalk](#Difference with Microwalk)
* [Usage JavaScript + GitLab](#usage JavaScript + GitLab)

## Installation

### Structure of the RAW version of Microwalk "with features" for JavaScript libraries on GitLab

```
.gitlab-ci.yml                     # GitLab CI configuration
microwalk-index.js                 # Trace generation entrypoint script
Dockerfile                         # Dockerfile of the jalangi3 image
lib/                               # Code Source from the library to analyze
microwalk/
  analyze.sh                       # Analysis script from Microwalk with code coverage
  config-preprocess.yml            # Configuration for trace preprocessing from Microwalk
  config-analyze.yml               # Configuration for analysis from Microwalk
features/
  test_generation.js               # Semi-automatic target-<name>.js generation   
  testcase_generation.js           # Automatic test cases associated with target-<name>.js generation
  code_quality_html_generation.mjs # Display coverage and code quality report
  function_name_in_source_code.mjs # Generate a JSON file containing names and codes of function in files from lib/ folder
  function_wanted.mjs              # Display functions that are not covered in the report by the target-<name>.js
```

1. Create a new repository on your GitLab account for an empty project.

2. Go to your GitLab *Settings*, then *General*, expand *Visibility, project features, permissions*. Enable the *CI/CD* option, and then *Save changes*. Then, if you have available runners, go to your GitLab *Settings*, then *CI/CD*, expand *Runners* and enable shared runners for this project.  Otherwise, feel free to set up your project runners.

3. Copy the entire contents of the cryptographic library you want to analyze into your empty project.

4. Copy all the contents of the [RAW-version](./RAW-version/) into the library source tree of your project. **If a conflict between different file or folder name occurs do not hesitate to rename those files or folders**.

5. Merge and adjust the CI configuration as describe below:

```
default:
  image:
    name: ghcr.io/easywalk-ci-project/easywalk-ci/jalangi3:latest
    entrypoint: [""]
  tags: 
    - ## Write the tags of the runner you use.

stages:
    - test

leak-detection-and-coverage:
    stage: test
    variables:
        NUMBER_OF_TESTCASES: '16' ## Feel free to change '16' with another integer greater than 1
        SIZE_OF_TESTCASES: '16' ## Feel free to change with another integer greater than 1
        SCRIPT: '' # Can add script for testcases generation into the format '[["RSA","keygen-rsa.sh"],["ED25519","gen-ED25519.sh"],...]' for instance

    script:

        # - npm install library-name 
        
        ## Install other libraries if needed

        - cd features
        - node test_generation.js
        - node testcase_generation.js $NUMBER_OF_TESTCASES $SIZE_OF_TESTCASES $SCRIPT

        - cd ../microwalk
        - bash analyze.sh

        - cd ../features
        - node code_quality_html_generation.mjs
        - node function_name_in_source_code.mjs '../lib'
        - node function_wanted.mjs

    artifacts:
        when: always
        paths:
            - microwalk/results/
            - microwalk/ct_reports/
            - microwalk/coverage/
            - microwalk/testcases/

```

6. Commit and push to GitLab.

**Note**: The Structure and the installation is equivalent for analyzing c libraries except from the image used (with PIN and not Jalangi). 

## Difference with Microwalk

- `index.js` from Microwalk has been renamed into [microwalk-index.js](./microwalk-index.js) to avoid confusion with the potential `index.js` file from other libraries.
- [analyze.sh](./microwalk/analyze.sh) has been updated to include code coverage with c8 and the name change of *index.js*.
- The [features folder](./features) has been added to the template with the following files:
    1. [code_quality_html_generation.mjs](./features/code_quality_html_generation.mjs): display a html report with the source code of the library, the `report.json` from Microwalk and the code coverage of the target tests. 
    2. [function_name_in_source_code.mjs](./features/function_name_in_source_code.mjs): search all the function names in the JavaScript files of the library (downloaded into the [lib folder](./lib/)) to save them into a JSON file `List_Of_Functions_In_Lib.json`.
    3. [function_wanted.mjs](./features/function_wanted.mjs): use the `List_Of_Functions_In_Lib.json` file with the html report to display most of the function name that were not covered by the target tests.
    4. [testcase_generation.js](./features/testcase_generation.js): a test cases generator, which use the interface [crypto](https://developer.mozilla.org/en-US/docs/Web/API/Crypto) to have access to a cryptographically strong random number generator, and can also execute script to generate test cases.
    5. [test_generation.js](./features/test_generation.js): a template proposal that generate tests (in C or JavaScript) needed to execute Microwalk. 
- A docker image *jalangi3* has been created to update the version of Node to 20.10.0 (to correctly execute [function_wanted.mjs](./features/function_wanted.mjs)) and to pre-install useful libraries such as:
    1. [c8](https://www.npmjs.com/package/c8) for code coverage used in [code_quality_html_generation.mjs](./features/code_quality_html_generation.mjs).
    2. [mustache](https://www.npmjs.com/package/mustache) to create a template in [test_generation.js](./features/test_generation.js).
    3. [acorn](https://www.npmjs.com/package/acorn) to use AST in [function_name_in_source_code.mjs](./features/function_name_in_source_code.mjs).
    4. [chalk](https://www.npmjs.com/package/chalk) to display function names not covered with colors in the terminal with [function_wanted.mjs](./features/function_wanted.mjs).
    
**Note**: The files [test_generation.js](./features/test_generation.js) and [testcase_generation.js](./features/testcase_generation.js) can generate both C and JavaScript files, the only requirement is that the user must specify the extension of the generated files ('.c' or '.js').

## Usage JavaScript + GitLab

In most cases, using the template with a JS library requires the following steps:

1. Copy the entire template into the library source tree or download the library to be analyzed with the `npm install` command in the file [.gitlab-ci.yml](./.gitlab-ci.yml). Merge and adjust the CI configuration and package.json, if necessary. It is also recommended to copy the library to be analyzed (or at least the interesting files) into the [lib folder](./lib/).

2. In the file [test_generation.js](./features/test_generation.js), adapt the template to call the primitive of the library to be analyzed. This will create `target-<NAME>.js` files in the [microwalk/](./microwalk) directory. `<NAME>` corresponds to the respective analyzed primitive (e.g., `AES-ECB`). Please note that the target code should be as minimal as possible, focusing only on the functions of interest to reduce trace length.

3. In the file [.gitlab-ci.yml](./.gitlab-ci.yml) it is possible to adjust the number and size of each test cases, which are set up to 16  by default, to find a good compromize between the number of leak found and the performance. It is also possible to add an environment variable SCRIPT in the format '[["PRIMITIVE_NAME","primitive.sh"],...]' to add at least one script for the test cases generation if those scripts already exist in the microwalk folder. 

4. Commit and push to GitLab.

5. Download the artifacts and take notes from the job terminal about any functions not covered by your tests before customizing [test_generation.js](./features/test_generation.js) a second time if necessary.

When the CI job is triggered, it calls the [test_generation.js](./features/test_generation.js) and [testcase_generation.js](./features/testcase_generation.js) programs which generate target tests and associated test cases. It then calls the [analyze.sh](./microwalk/analyze.sh) script, which in turn iterates through the targets present in the [microwalk/](./microwalk) directory and performs analysis on all of the tests and generates the following reports:
- `call-stacks-target-<NAME>.txt`: The human-readable [control flow leakage](../../docs/control-flow-leakage.md) analysis reports for the given target.
- `report-target-<NAME>.json`: Code quality report for the given target.
- `report.json`: Merged code quality reports. This file is displayed by GitLab.
- `coverage/target-<NAME>.json`: Code coverage report for the given target.

Afterwards, the `report.json` and each `coverage/target-<NAME>.json` are used in the [code_quality_html_generation.mjs](./features/code_quality_html_generation.mjs) to build html reports that display the code quality and coverage into the source code of the library. Last but not least, [function_name_in_source_code.mjs](./features/function_name_in_source_code.mjs) searches all the function names in the library's JavaScript files (downloaded into the [lib folder](./lib/)) to store them into a JSON file `List_Of_Functions_In_Lib.json` and [function_wanted.mjs](./features/function_wanted.mjs) use the `List_Of_Functions_In_Lib.json` file with the html report to display most of the function names that were not covered by the targeted tests in the job terminal.
 Finally, the job produces a number of folders as artifacts that are passed back to GitLab:
- `results/`: Folder containing the reports generated by the [analyze.sh](./microwalk/analyze.sh) script.
- `ct_reports/`: Folder containing the html reports generated by the [code_quality_html_generation.mjs](./features/code_quality_html_generation.mjs) program.
- `coverage/`: Folder containing the code coverage reports for all the targeted tests.
- `testcases/`: Folder containing all the test cases generated by the [testcase_generation.js](./features/testcase_generation.js) program.

