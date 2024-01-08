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
