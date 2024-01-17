const Mustache = require('/usr/local/lib/node_modules/mustache')
const fs = require('fs/promises');
const path = require('node:path'); 

const DIR = '../microwalk/';
const EXT = ; // Extension of the target tests to specify. Can be a 'js' or a 'c' extension for instance;

let text = `// Executes the given testcase.
// Parameters:
// - testcaseBuffer: Buffer object containing the bytes read from the testcase file.

// Import required libraries
// TODO

// Create a function to process the testcase
function processTestcase(testcaseBuffer) {
        // TODO
}
// Export the function for external use
module.exports = { processTestcase };`

const algoList = []; // e.g. [["ecb", false], ["cbc", true]]; 

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
              //"algo": algoName,
              //"IV": algoList[i][1],
              };
            const output = Mustache.render(text, view);
            fs.writeFile(PATH, output);
        }
    }
}

generateFile();
