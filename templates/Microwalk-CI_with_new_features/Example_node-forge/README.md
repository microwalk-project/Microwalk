# Microwalk-CI with features RAW version

This folder contains an example of how to use the new features of Microwalk-CI with the [node-forge library](https://github.com/digitalbazaar/forge). To reproduce this example and obtain the same results as in the [artifacts folder](./artifacts_node-forge/), follow the steps below:

* [.gitlab-ci.yml](#.gitlab-ci.yml)
* [test_generation.js](#test_generation.js)
* [artifacts_node-forge](#artifacts_node-forge)

## .gitlab-ci.yml

Below is what the .gitlab-ci.yml file should look like before adding the tags of the runner.

```
default:
  image:
    name: ghcr.io/easywalk-ci-project/easywalk-ci/jalangi3:latest
    entrypoint: [""]
  tags:
    - ## Write the tags of the runner.

stages:
    - test

leak-detection-and-coverage:
    stage: test
    variables:
        NUMBER_OF_TESTCASES: '16'
        SIZE_OF_TESTCASES: '16'
        SCRIPT: "[['target-RSASIGN','generate-rsa-private-keys.sh'],['target-ED25519','generate-ed25519-private-keys.sh']]"

    script:
        - npm install node-forge ## Here you need to install node-forge

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

**NOTE**: The variable SCRIPT indicates that two scripts to generate random keys as test cases, have been added to the [microwalk folder](./microwalk/) and are executed during the test cases generation process. 

## test_generation.js

Below is an example of how to write the template from the test_generation.js file. Feel free to adapt this file to your own needs. 

```
const Mustache = require('/usr/local/lib/node_modules/mustache')
const fs = require('fs/promises');
const path = require('node:path'); 

const DIR = '../microwalk/';
const EXT = '.js';

let text = `// Executes the given testcase.
// Parameters:
// - testcaseBuffer: Buffer object containing the bytes read from the testcase file.

// Import required libraries
var forge = require('node-forge');
var crypto = require('crypto');

// Create a function to process the testcase
function processTestcase(testcaseBuffer) {

  {{#AESECB}}

  var MODE = 'AES-' + {{{algo}}}.toUpperCase();
  var key = new forge.util.ByteBuffer(testcaseBuffer);

  // Create an instance of the {{{algo}}} mode with the random key
  var cipher = forge.cipher.createCipher(MODE, key);
  
  // Generate a random message (and iv sometimes) using CSPRNG
  var sizeOfTheMessage = 16;
  var message = new forge.util.ByteBuffer(crypto.getRandomValues(new Uint8Array(sizeOfTheMessage)));

  // Encrypt then decrypt the plaintext from the testcase
  cipher.start({ {{#IVTAG}}iv: iv{{/IVTAG}}});
  cipher.update(message);
  cipher.finish();

  var encrypted = cipher.output;
  encrypted.toHex();
  {{#IVTAG}}
  var tag = cipher.mode.tag;
  {{/IVTAG}}
  
  {{/AESECB}}
    
  {{#AESGCM}}  
  var message = forge.util.createBuffer();
  message.putString('aaaabbbbaaaabbbbaaaabbbbaaaabbbb');
  var iv = forge.util.createBuffer();
  iv.putString('aaaabbbbcccc')
  var key = forge.util.createBuffer(testcaseBuffer);
  var cipher = forge.cipher.createCipher('AES-GCM', key);
  cipher.start({iv: iv.bytes(12)});
  cipher.update(message);
  cipher.finish();
  var encrypted = cipher.output;
  var tag = cipher.mode.tag;
  {{/AESGCM}}
  
  {{#ENCODE}}
  var b64message = forge.util.encode64((forge.util.createBuffer(testcaseBuffer)).data);
  {{/ENCODE}}
  {{#DECODE}}
  var b64message = forge.util.encode64((forge.util.createBuffer(testcaseBuffer)).data);
  var binary = forge.util.decode64(b64message.toString());
  {{/DECODE}}
  
  {{#ED25519}}
  var message = forge.util.createBuffer();
  message.putString('aaaabbbbaaaabbbbaaaabbbbaaaabbbb');
  var privateKey = forge.pki.ed25519.privateKeyFromAsn1(forge.asn1.fromDer(forge.util.createBuffer(testcaseBuffer)));
  var md = forge.md.sha1.create();
  md.update(message.bytes(), 'raw');
  var signature = forge.pki.ed25519.sign({md: md, privateKey: privateKey.privateKeyBytes});
  {{/ED25519}}
  
  {{#RSA}}
  var drngBuffer = 'abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd' +
    'abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd' +
    'abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd' +
    'abcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcdabcd'
  var message = forge.util.createBuffer();
  message.putString('aaaabbbbaaaabbbbaaaabbbbaaaabbbb');
  var privateKey = forge.pki.privateKeyFromAsn1(forge.asn1.fromDer(forge.util.createBuffer(testcaseBuffer)));
  forge.random.getBytes = function(length, callback) {
    var rndBuffer = forge.util.createBuffer(drngBuffer);
    var retval;
    if (length < rndBuffer.length()) {
            retval = rndBuffer.getBytes(length);
    } else {
      rndBuffer.read = 0;
      if (length > rndBuffer.length()) {
        console.log("#### WARNING: Exceeding limits of deterministic prng buffer");
        console.log("Requested length: " + length);
        console.log("Buffer length: " + rndBuffer.length());
        retval = rndBuffer.getBytes(256);
      } else {
        retval = rndBuffer.getBytes(length);
      }
    }
    if(callback) {
      callback(retval)
    } else {
      return retval;
    }
  };

  var md = forge.md.sha1.create();
  md.update(message.bytes(), 'raw');
  var signature = privateKey.sign(md);
  console.log("Signature: " + forge.util.bytesToHex(signature))
  {{/RSA}}
}
// Export the function for external use
module.exports = { processTestcase };`

const algoList = [["'ecb'", true, false, false, false, false, false], 
                            ["'gcm'", false, true, false, false, false, false], 
                            ["'encode'", false, false, true, false, false, false], 
                            ["'decode'", false, false, false, true, false, false], 
                            ["'ed25519'", false, false, false, false, true, false],
                            ["'rsaSign'", false, false, false, false, false, true]
                            ];

async function exists(path) {
  try {
    await fs.access(path, fs.constants.F_OK);
    return true;
  } catch {
    return false;
  }
}

async function generateFile() {
    for (let i = 0; i < algoList.length; i++) {
        const algoName = algoList[i][0];
        const PATH = path.join(DIR, `/target-${algoName.replaceAll("'", "").toUpperCase()}`) + '.' + EXT.replace('.', '');
        if (!(await exists(PATH))) {
            const view = {
              "algo": algoName,
              "AESECB": algoList[i][1],
              "AESGCM": algoList[i][2],
              "ENCODE": algoList[i][3],
              "DECODE": algoList[i][4],
              "ED25519": algoList[i][5],
              "RSA": algoList[i][6]
            };
            const output = Mustache.render(text, view);
            fs.writeFile(PATH, output);
        }
    }
}

generateFile();
```

## artifacts_node-forge

Below is the structure of the artifacts from the NODE-FORGE example with the [test_generation.js file](#test_generation.js) above.

```
artifacts_node-forge/
  coverage/                     # Code coverage for each tested primitives
    target-DECODE.json
    target-ECB.json
    target-ED25519.json
    target-ENCODE.json
    target-GCM.json
    target-RSASIGN.json
  ct_reports/                   # Code quality+coverage report from EasyWalk-CI
    node-forge_lib_aes.js.html
    node-forge_lib_cipherModes.js.html
    node-forge_lib_jsbn.js.html
    node-forge_lib_rsa.js.html
    node-forge_lib_util.js.html
  results/                      # Code quality reports from Microwalk-CI
    call-stacks-target-DECODE.txt
    call-stacks-target-ECB.txt
    call-stacks-target-ED25519.txt
    call-stacks-target-ENCODE.txt
    call-stacks-target-GCM.txt
    call-stacks-target-RSASIGN.txt
    report.json
    report-target-DECODE.json
    report-target-ECB.json
    report-target-ED25519.json
    report-target-ENCODE.json
    report-target-GCM.json
    report-target-RSASIGN.json
  testcases/                    # test cases for each tested primitives
    target-DECODE/
    target-ECB/
    target-ED25519/
    target-ENCODE/
    target-GCM/
    target-RSASIGN/
```
