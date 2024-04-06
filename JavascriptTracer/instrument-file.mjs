/**
 * Instruments the given file and its children.
 * 
 * @param {string} path The file to instrument.
 */

import * as path from "node:path";
import process from "node:process";

import { instrumentFileTree, getInstrumentedName } from "./instrument.mjs";

const cliArgs = process.argv;

if (cliArgs.length < 3) {
    console.error('No source file provided');
    console.log(`Usage: node ${cliArgs[1]} FILEPATH`)
    process.exit(1);
}

let filePath = cliArgs[2];

// Make path absolute
if (!path.isAbsolute(filePath)) {
    filePath = path.resolve(process.cwd(), filePath);
}

// Ensure everything is instrumented
instrumentFileTree(filePath);

export const instrumentedName = getInstrumentedName(filePath);

// Remove the instrumentation script from the process arguments
cliArgs.splice(1, 1);