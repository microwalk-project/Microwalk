import { open, readFile, readdir } from 'node:fs/promises';
import * as path from 'path';
import chalk from '/usr/local/lib/node_modules/chalk/source/index.js';

const reportHtmlDir = "../microwalk/ct_reports/";
const functionNamesInSourceCodeDir = '../microwalk/List_Of_Functions_In_Lib/';

let allFunctionName = [];
let functionNameAlreadyTreatedAndLine = [];
let functionNameAlreadyTreated = [];
let functionNameAlreadyTreatedOnly = [];
let displayLine = true;

function removeDuplicates(arr) {
    /* Remove all number line that repeat more than once for one function name.
     * Example:
     * [
         [ 'generate', [ '99' , '99'] ],
         [ '_reseed', [ '184' ] ]
       ]
       Becomes:

         [ 'generate', [ '99' ] ],
         [ '_reseed', [ '184' ] ]
       ]

    */
    let arrWithoutDuplicate = [];
    let partialArr = [];
    for (let i = 0; i < arr.length; i++) {
        if (arr[i][1].length > 1) {
            partialArr.push(arr[i][1][0]);
            let j = 1;
            while (j < arr[i][1].length) {
                if (partialArr.includes(arr[i][1][j])) {
                    j++;
                } else {
                    partialArr.push(arr[i][1][j]);
                }
            }
        } else {
            partialArr = arr[i][1];
        }
        arrWithoutDuplicate.push([arr[i][0], partialArr]);
        partialArr = [];
    }
    return arrWithoutDuplicate;
};

function removeUselessFunctions(arr) {
    /* If a functions name is include in another one (and appear on the same line number), remove the shorter function.
     * Example:
     *    [
     *     [ 'ctx.collectInt', [ '373' ] ],
     *     [ 'ctx.collect', [ '373' ] ]
     *    ]
     *  becomes:
     *    [
     *     [ 'ctx.collectInt', [ '373' ] ]
     *    ]
     */
    let finalArr = arr;
    let i = 0;
    while (i < finalArr.length) {
        let j = 0;
        if (i === j) {
            j++;
        }
        while ((j < finalArr.length) && (j !== i)) {
            if (finalArr[j][0].includes(finalArr[i][0])) {
                let k = 0;
                let number = finalArr[i][1].length;
                while (k < number) {
                    let l = 0;
                    let num = finalArr[j][1].length;
                    while (l < num) {
                        
                        if (finalArr[i][1][k] === finalArr[j][1][l]) {
                            finalArr[i][1].splice(k, 1);
                        };
                        if (finalArr[i][1].length === 0) {
                            finalArr.splice(i, 1)
                            j--;
                            if (j < 0) {
                                j++;
                            }
                        };
                        l++;
                    }
                    k++;
                }
            }
            j++;
            if (j === i) {
                j++;
            }
        }
        j = 0;
        if (i === j) {
            j++;
        }
        while ((j < finalArr.length) && (j !== i)) {
            let name = finalArr[i][0];
            if (name.includes(finalArr[j][0])) {
                let k = 0;
                let number = finalArr[i][1].length;
                while (k < number) {
                    let l = 0;
                    let num = finalArr[j][1].length;
                    while (l < num) {
                        if (arr[i][1][k] === arr[j][1][l]) {
                            finalArr[j][1].splice(l, 1);
                        } 
                        if (finalArr[j][1].length === 0) {
                            finalArr.splice(j, 1)
                            i--;
                            if (i < 0) {
                                i++;
                            }
                        };
                        l++;
                    }
                    k++;
                }
            }
            j++;
            if (j === i) {
                j++;
            }
        }
        i++;
    }
    return finalArr;
}

try {
    const sourceCodeFiles = await readdir(reportHtmlDir);

    outer: for (const file of sourceCodeFiles) {
        let DiplayFile = false;

        const filePathNameWithoutHTML = path.basename(file, '.html');

        console.log("-------------------------------------------\n");
        console.log("In the file: " + filePathNameWithoutHTML + "\n");
        console.log("-------------------------------------------\n");

        try {
            const functionNamesInSourceCode = await readdir(functionNamesInSourceCodeDir);
            var functionNamesInSourceCodeWithoutLib = [];

            for (const functionNamesFile of functionNamesInSourceCode) {
                if (path.basename(functionNamesFile).slice(0,4) === 'lib_') {
                    functionNamesInSourceCodeWithoutLib.push(path.basename(functionNamesFile).slice(4, -5));
                } else {
                    functionNamesInSourceCodeWithoutLib.push(path.basename(functionNamesFile).slice(-5));
                }
            }

            for (const functionNamesFile of functionNamesInSourceCode) {
                if (path.basename(functionNamesFile).slice(0,4) === 'lib_') {
                    var functionNamesFileWithoutLib = path.basename(functionNamesFile).slice(4);
                } else {
                    var functionNamesFileWithoutLib = functionNamesFile;
                }
                if (filePathNameWithoutHTML.includes(functionNamesFileWithoutLib.replace(/.json$/,''))) {

                    DiplayFile = true;
                    
                    const file_path = path.join(functionNamesInSourceCodeDir, functionNamesFile);
                    
                    var tableOfFunctionNamesInSourceCode = JSON.parse(await readFile(file_path, { encoding: 'utf8' }));

                    for (let i = 0; i < tableOfFunctionNamesInSourceCode.length; i++) {
                        const functionNameToPush = tableOfFunctionNamesInSourceCode[i].name
                        if (functionNameToPush) {
                            allFunctionName.push(functionNameToPush);
                        }
                    }
                }
            }

            const filePath = path.join(reportHtmlDir, file);

            const sourceCodeFile = await open(filePath);

            for await (const line of sourceCodeFile.readLines()) {
                if (line.includes('<span class="line-counter cline-no">！</span>')) {
                    if (line.includes("function") || line.includes("Function") || line.includes("=>")) {
                        for (const name of allFunctionName) {
                            if (line.includes(name)) {
                                for (let j = 0; j < functionNameAlreadyTreated.length; j++) {
                                    if (name.includes(functionNameAlreadyTreated[j])) {
                                        displayLine = false;
                                    }
                                }
                                if (displayLine) {
                                    const lineNumber = /<div class="source-line"><span class="line-number ">(?<number>\d*)<\/span>/;
                                    if (!(functionNameAlreadyTreatedOnly.includes(name))) {
                                        functionNameAlreadyTreatedAndLine.push([name , [ lineNumber.exec(line).groups.number ]] );
                                        functionNameAlreadyTreated.push(name);
                                        functionNameAlreadyTreatedOnly.push(name);
                                    } else {
                                        for (let i = 0; i < functionNameAlreadyTreatedAndLine.length; i++) {
                                            if (functionNameAlreadyTreatedAndLine[i][0] === name) {
                                                functionNameAlreadyTreatedAndLine[i][1].push(lineNumber.exec(line).groups.number);
                                            }
                                        }
                                    }
                                }
                            }
                            functionNameAlreadyTreated = [];
                            displayLine = true;
                        }
                    }
                }
            }
            if (functionNameAlreadyTreatedAndLine.length !== 0) {
                const functionNameAlreadyTreatedAndLineLessDuplicateAndUselessFunctions = removeUselessFunctions(removeDuplicates(functionNameAlreadyTreatedAndLine));
                for (let i = 0; i < functionNameAlreadyTreatedAndLineLessDuplicateAndUselessFunctions.length; i++) {
                    let lineNumber = functionNameAlreadyTreatedAndLineLessDuplicateAndUselessFunctions[i][1]; 
                    let allLine = "";
                    for (let j = 0; j < lineNumber.length; j++) {
                        if (lineNumber.length === 1) {
                            allLine += lineNumber[j];
                        } else if (j === lineNumber.length - 2) {
                            allLine += lineNumber[j] + " ";
                        } else if (j !== lineNumber.length - 1) {
                            allLine += lineNumber[j] + ", ";
                        } else {
                            allLine += "and " + lineNumber[j];
                        }
                    }
                    if (allLine.includes("and")) {
                        console.log("Function " + chalk.greenBright(functionNameAlreadyTreatedAndLineLessDuplicateAndUselessFunctions[i][0]) + " is not covered on lines " + chalk.cyanBright(allLine) + ".\n");
                    } else {
                        console.log("Function " + chalk.greenBright(functionNameAlreadyTreatedAndLineLessDuplicateAndUselessFunctions[i][0]) + " is not covered on line " + chalk.cyanBright(allLine) + ".\n");
                    }
                    
                }
            } else if (!DiplayFile){
                console.log(chalk.yellow( filePathNameWithoutHTML ) + " comes from a dependency.");
                console.log("If you want to know which functions are not covered inside,\nplease download its source code into the ./lib directory.\n");
            } else {
                console.log(chalk.greenBright("All functions are covered.\n"));
            }

            functionNameAlreadyTreatedOnly = [];
            functionNameAlreadyTreatedAndLine = [];
            allFunctionName = [];

        } catch(err) {
            console.log("There is no List_Of_Functions_In_Lib");
            break outer;
        }
    }
} catch (err) {
    console.log("There is no reports");
}
