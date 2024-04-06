/**
 * Contains runtime functions for the instrumented code.
 * CommonJS module to support inclusion in both ES and CommonJS modules.
 */

const constants = require("./constants.cjs");
const uidUtil = require("./uid.cjs");
const fs = require("fs");
const { execSync } = require("child_process");
const pathModule = require("path");

// Names of the testcase begin/end marker functions.
const testcaseBeginFunctionName = "__MICROWALK_testcaseBegin";
const testcaseEndFunctionName = "__MICROWALK_testcaseEnd";

// Ensure trace directory exists
const traceDirectory = process.env.MW_TRACE_DIRECTORY;
if(!traceDirectory)
    throw new Error("MW_TRACE_DIRECTORY not set!");
if (!fs.existsSync(traceDirectory)) {
    fs.mkdirSync(traceDirectory, { recursive: true });
}

// Tracing state
let currentTestcaseId = -1; // Prefix mode
let isTracing = true;
let traceData = [];
const traceDataSizeLimit = 1000000;
let previousTraceFilePath = "";

// (debugging only) If set to true, trace compression is disabled.
// WARNING: This may lead to huge files, and is incompatible to Microwalk's preprocessor module!
let disableTraceCompression = false;

// Compressed lines from the trace prefix can be reused in all other traces
let nextCompressedLineIndex = 0;
let compressedLines = {};
let prefixNextCompressedLineIndex = 0;
let prefixCompressedLines = {};

// Used for computing relative distance between subsequent compressed line IDs.
// If the distance is small, we only print the offset (+1, ...), not the entire line ID.
let lastCompressedLineIndex = -1000;

// If the last line used a one-character relative encoding, we omit the line break and append the next one directly.
let lastLineWasEncodedRelatively = false;

// Path prefix to remove from script file paths (so they are relative to the project root)
const scriptPathPrefix = process.env.MW_PATH_PREFIX;
if(!scriptPathPrefix)
    throw new Error("MW_PATH_PREFIX not set!");

// Mapping of known script file paths to their IDs.
const scriptNameToIdMap = new Map();

// File handle of the script information file.
let scriptsFile = fs.openSync(`${traceDirectory}/scripts.txt`, "w");


/**
 * Registers the given script in the trace writer and returns an ID that can be used with the trace writing functions.
 * @param {string} filename - Full path of the script file
 * @returns {number} ID of the script
 */
function registerScript(filename)
{
    // Remove path prefix
    if(!filename.startsWith(scriptPathPrefix))
        throw new Error(`Script path "${filename}" does not start with prefix "${scriptPathPrefix}"`);
    filename = filename.substring(scriptPathPrefix.length);
    if(filename.startsWith("/"))
        filename = filename.substring(1);

    // Check whether script is already known
    if(scriptNameToIdMap.has(filename))
        return scriptNameToIdMap.get(filename);

    // No, generate new ID
    const id = scriptNameToIdMap.size;
    scriptNameToIdMap.set(filename, id);

    fs.writeSync(scriptsFile, `${id}\t${filename}\n`);

    return id;
}

/**
 * Writes the pending trace entries.
 */
function _persistTrace()
{
    if(!isTracing)
        return;

    let traceFilePath = currentTestcaseId === -1 ? `${traceDirectory}/prefix.trace` : `${traceDirectory}/t${currentTestcaseId}.trace`;

    let writingToNewTrace = false;
    if(traceFilePath !== previousTraceFilePath)
    {
        writingToNewTrace = true;
        previousTraceFilePath = traceFilePath;
    }

    let traceFile;
    if(writingToNewTrace)
    {
        console.log(`  creating ${traceFilePath}`);
        traceFile = fs.openSync(traceFilePath, "w");
    }
    else
    {
        traceFile = fs.openSync(traceFilePath, "a+");
    }

    fs.writeSync(traceFile, traceData.join('\n'));
    fs.writeSync(traceFile, '\n');

    fs.closeSync(traceFile);
}

/**
 * Checks whether we already have a compressed representation of the given line.
 * If not, a new one is created.
 * @param {string} line - Line to compress
 * @returns A compressed representation of the given line.
 */
function _getCompressedLine(line)
{
    if(line in compressedLines)
        return compressedLines[line];
    else
    {
        let compressed = nextCompressedLineIndex;
        ++nextCompressedLineIndex;

        compressedLines[line] = compressed;
        traceData.push(`L|${compressed}|${line}`);

        lastLineWasEncodedRelatively = false;
        return compressed;
    }
}

/**
 * Writes a line into the trace file.
 * @param {string} line - line to write
 */
function _writeTraceLine(line)
{
    if(traceData.length >= traceDataSizeLimit)
    {
        _persistTrace();
        traceData = [];
    }

    if(disableTraceCompression)
    {
        traceData.push(line);
        return;
    }

    let encodedLine = "";

    // Ensure that compressed line exists, and then output its index (either absolute or relative)
    let lineIndex = _getCompressedLine(line);
    let distance = lineIndex - lastCompressedLineIndex;
    let encodeRelatively = (distance >= -9 && distance <= 9);
    if(encodeRelatively)
        encodedLine = String.fromCharCode(106 + distance); // 'j' + distance => a ... s
    else
        encodedLine = lineIndex.toString();

    // If we are in relative encoding mode, we omit the line break and append the distance marker directly
    if(lastLineWasEncodedRelatively && traceData.length > 0)
        traceData[traceData.length - 1] += encodedLine;
    else
        traceData.push(encodedLine);

    lastLineWasEncodedRelatively = encodeRelatively;
    lastCompressedLineIndex = lineIndex;
}

// Tracks a pending call.
// Calls are written in two steps: In the call instruction itself, and when entering the callee.
let callInfo = null;

function startCall(fileId, sourceLoc, fnObj)
{
    // Extract callee name, fallback if we can not resolve it
    let fnName = fnObj?.name;
    if (!fnName)
        fnName = "<anonymous>";

    // Handle special testcase begin marker function
    if(fnName === testcaseBeginFunctionName)
    {
        // Ensure that previous trace has been fully written (prefix mode)
        if(isTracing && traceData.length > 0)
            _persistTrace();
        traceData = [];

        // If we were in prefix mode, store compression dictionaries
        if(currentTestcaseId === -1)
        {
            prefixNextCompressedLineIndex = nextCompressedLineIndex;
            prefixCompressedLines = compressedLines;
        }

        // Initialize compression dictionaries
        compressedLines = Object.assign({}, prefixCompressedLines);
        nextCompressedLineIndex = prefixNextCompressedLineIndex;
        lastCompressedLineIndex = -1000;
        lastLineWasEncodedRelatively = false;

        // Enter new testcase
        ++currentTestcaseId;
        isTracing = true;
    }

    // Handle special testcase end marker function
    if(fnName === testcaseEndFunctionName)
    {
        // Close trace
        _persistTrace();
        traceData = [];
        isTracing = false;
    }

    // Store info for later write when we know the callee location
    callInfo = {
        sourceFileId: fileId,
        sourceLocation: sourceLoc,
        destinationFileId: null,
        destinationLocation: null,
        functionName: fnName
    };
}

function endCall(fileId, destLoc)
{
    if(!callInfo) {
        // We did not observe the call itself; this can happen for callbacks when the caller is an external function.
        // Do not produce a call entry in this case, as we will also miss the Return1 entry, so the call tree stays balanced.
        return;
    }

    callInfo.destinationFileId = fileId;
    callInfo.destinationLocation = destLoc;
    
    writeCall();
}

function writeCall()
{
    if(!callInfo)
        return;
   
    let srcFileId = callInfo.sourceFileId;
    let srcLoc = callInfo.sourceLocation;
    let destFileId = callInfo.destinationFileId ?? "E";
    let destLoc = callInfo.destinationLocation ?? callInfo.functionName;
    let fnName = callInfo.functionName;
    _writeTraceLine(`c;${srcFileId};${srcLoc};${destFileId};${destLoc};${fnName}`);
    
    callInfo = null;
}

function writeReturn(fileId, location, isReturn1)
{
    if(callInfo)
        writeCall();

    const ret = isReturn1 ? 'r' : 'R';
    _writeTraceLine(`${ret};${fileId};${location}`);
}

function writeYield(fileId, location, isResume)
{
    const res = isResume ? 'Y' : 'Y';
    _writeTraceLine(`${res};${fileId};${location}`);
}

function writeBranch(fileId, sourceLoc, bodyLocation)
{
    //_writeTraceLine(`b;${fileId};${sourceLoc};${bodyLocation}`);

    // Handle like a jump
    // TODO remove separation of branch vs jump altogether?
    writeJump(fileId, sourceLoc, bodyLocation);
}

function writeJump(fileId, sourceLoc, destLoc)
{
    _writeTraceLine(`j;${fileId};${sourceLoc};${destLoc}`);
}

function writeMemoryAccess(fileId, loc, objId, offset, isWrite, computedVar)
{
    let offsetStr = offset;
    if (offset == constants.COMPUTED_OFFSET_INDICATOR) {
        offsetStr = `${computedVar}`;
    }

    const memAccessType = isWrite ? "w" : "r";
    const objIdStr = uidUtil.getUid(objId);
    if (objIdStr && objIdStr != constants.PRIMITIVE_INDICATOR) {
        _writeTraceLine(`m;${memAccessType};${fileId};${loc};${objIdStr};${offsetStr}`);
    }
}

/**
 * Instruments the given dynamically imported file.
 * 
 * @param {string} path The file to instrument.
 */
function instrumentDynamic(path)
{
    console.log(`Instrumenting dynamic import: ${path}`);

    // Remove file:// if necessary
    if (path.startsWith("file://")) {
        path = path.substring(7);
    }

    // Check whether the file exists
    if (!fs.existsSync(path)) {
        console.log(`  File does not exist, skipping`);
        return path;
    }

    // We call an instrumentation script and wait for its execution. It would be preferable to just call the instrumentation
    // functions here, but they are implemented as an ES module and we are in a CommonJS context here. As soon as we drop
    // support for CommonJS, we can clean this up.

    // The script exists in the same folder as this one
    const instrumentFileScriptPath = pathModule.resolve(__dirname, 'instrument-file.mjs');

    // Check whether the file is already instrumented
    const suffix = constants.VALID_SUFFIXES.find((e) => path.endsWith(e));
    const instrumentedPath = path.replace(suffix, `${constants.PLUGIN_SUFFIX}${suffix}`);
    if (fs.existsSync(instrumentedPath)) {
        console.log(`  File is already instrumented, skipping`);
        return instrumentedPath;
    }

    // Run instrumentation process
    execSync(`node ${instrumentFileScriptPath} "${path}"`, { stdio: 'inherit' });

    // Return name of instrumented file
    return instrumentedPath;
}

module.exports = {
    registerScript,
    writeMemoryAccess,
    startCall,
    endCall,
    writeReturn,
    writeYield,
    writeBranch,
    writeJump,
    instrumentDynamic
};