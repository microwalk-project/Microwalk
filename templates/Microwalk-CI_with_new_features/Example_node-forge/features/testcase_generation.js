var fs = require('fs');
var path = require('path');
var crypto = require('crypto');
var child_process = require('child_process');

const PATHEXTENSION = ['.js', '.c']
const DIR_TESTCASES = '../microwalk/testcases/'
const DIR_OLD_TESTCASES = '../microwalk/old_testcases/'
const DIR = '../microwalk/'

const cliArgs = process.argv.slice(2);

//////////////////////////////
/// To get the date & time ///
var dateTime = new Date().toUTCString();
console.log(dateTime);
//////////////////////////////

////////////////////////////////////
// To get a random Unicode string //
random = function(length) {
	var array = new Uint8Array(length);
    crypto.getRandomValues(array);
	return array;
}
////////////////////////////////////

if (!fs.existsSync(DIR_TESTCASES)) {
    fs.mkdirSync(DIR_TESTCASES);
}

const numberOfTestcasesToParse = cliArgs[0];
let numberOfTestcases = parseInt(numberOfTestcasesToParse, 10);

if ((typeof numberOfTestcases !== 'number') || isNaN(numberOfTestcases) || (numberOfTestcases <= 0)) {
    numberOfTestcases = 16;
}

const widthToParse = cliArgs[1];
let width = parseInt(widthToParse, 10);

if ((typeof width !== 'number') || (isNaN(width) || (width <= 0))) {
    width = 16;
}

const script = cliArgs[2];

// Check the format of input SCRIPT variable. Format: [["name1","script1"],["name2","script2"],...]
const regex = /\s*\[\s*\[\s*(\"([a-zA-Z0-9-_.]|\s)*\"\s*\,\s*\"([a-zA-Z0-9-_.]|\s)*\"|\'([a-zA-Z0-9-_.]|\s)*\'\s*\,\s*\'([a-zA-Z0-9-_.]|\s)*\')\s*\]\s*(\,\s*\[\s*\"([a-zA-Z0-9-_.]|\s)*\"\s*\,\s*\"([a-zA-Z0-9-_.]|\s)*\"\s*\]\s*|\,\s*\[\s*\'([a-zA-Z0-9-_.]|\s)*\'\s*\,\s*\'([a-zA-Z0-9-_.]|\s)*\'\s*\]\s*)*\]/; 

// TODO vérifier aussi les noms de fichier pour voir si j'ai pas un moyen de rendre ça non case sensitive puis aller mettre à jour le papier avec les nouvelles données
if (script && regex.test(script)) {
    // Remove the first [[ and last ]]
    const stringArray = script.replace(/\s*\[\s*\[\s*/, '').replace(/\s*\]\s*\]\s*/, '');

    // Convert a string into an array
    const arrayScript = stringArray.split(/\s*\]\s*,\s*\[\s*/).map((innerArrayStr) => {
        return innerArrayStr.split(/\s*,\s*/).map((value) => {
            return value.replace(/["']+/g, '').replace('[', '').replace(']', '');
        });
    });

    var targetNameScript = arrayScript.map((element) => { 
            return element[0];
    });

    var targetScript = arrayScript.map((element) => { 
        return element[1];
    });
}

fs.readdir(DIR, function (err, files) {
    if (err) {
        throw err;
    } if (files === '') {
        console.log('No target');
    } else {
        targetName = [];
        files.forEach(function (file) {
            const pathExt = path.extname(file);
            if (PATHEXTENSION.includes(pathExt) && file !== 'testcase_generation.js' && file !== 'test_generation.js') {
                targetName.push(file.replace(pathExt, ''));
            }
        });
        if (script && !targetNameScript.every(element => targetName.includes(element))) {  // All target name from the script array are in the array of target name
            const Err =  `At least one target name from the environment variable SCRIPT has no test file with the same target name:\n target-name: ${targetName} \n target-name-script: ${targetNameScript}`;
            throw new Error(Err);
        };
        if (!fs.existsSync(DIR_OLD_TESTCASES)) {
            fs.mkdirSync(DIR_OLD_TESTCASES);
        }
        var newPath = path.join(DIR_OLD_TESTCASES, dateTime);
        fs.mkdirSync(newPath);
        fs.cpSync(DIR_TESTCASES, newPath , { recursive: true } );
        fs.rmSync(DIR_TESTCASES, { recursive: true, force: true });
        fs.mkdirSync(DIR_TESTCASES);

        for (target of targetName) {
            var dir = path.join(DIR_TESTCASES, target);
            fs.mkdirSync(dir);
            if (script && targetNameScript.includes(target)) {
                const command = 'cd ' + DIR + ' && bash ' + targetScript[targetNameScript.indexOf(target)];
                try {
                    child_process.execSync(command);
                } catch(error) {
                    console.error(error);
                }
            } else {  
                for (i = 0; i < numberOfTestcases; i++) {
                    let test = Buffer.from(random(width), 'utf8');
                    if (width <= 256) { // To avoid spending to much time in the loop
                        while (test.toString().length !== width) {
                            test = Buffer.from(random(width), 'utf8');
                        }
                    }
                    var numberOfTest = '' + i;
                    fs.writeFileSync(dir + '/t' + numberOfTest + '.testcase', test);
                };
            };
        };
        console.log('Testcases generation is done!');
    };
});
