// JALANGI DO NOT INSTRUMENT

const fs = require('fs');

const globalPathPrefix = process.env.JS_PATH_PREFIX;

const testcaseBeginFunctionName = "__MICROWALK_testcaseBegin";
const testcaseEndFunctionName = "__MICROWALK_testcaseEnd";

const traceDirectory = process.env.JS_TRACE_DIRECTORY;

let currentTestcaseId = -1; // Prefix mode
let traceData = [];
const traceDataSizeLimit = 1000000;
let previousTraceFilePath = ""
let scriptsFile = fs.openSync(`${traceDirectory}/scripts.txt`, "w");

let nextCodeFileIndex = 0;
let knownCodeFiles = {};

// If set to true, trace compression is disabled.
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

// Stores whether the last trace entry was a conditional.
let pendingConditionalState = 0; // 0: No conditional; 1: Pending target instruction; 2: Skip very next expression

function persistTrace()
{
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
        console.log(`Creating ${traceFilePath}`);
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

function writeTraceLine(line)
{
    if(traceData.length >= traceDataSizeLimit)
    {
        persistTrace();
        traceData = [];
    }

    if(disableTraceCompression)
    {
        traceData.push(line);
        return;
    }

    let encodedLine = "";

    let lineIndex = getCompressedLine(line);
    let distance = lineIndex - lastCompressedLineIndex;
    let encodeRelatively = (distance >= -9 && distance <= 9);
    if(encodeRelatively)
        encodedLine = String.fromCharCode(106 + distance); // 'j' + distance => a ... s
    else
        encodedLine = lineIndex.toString();

    if(lastLineWasEncodedRelatively && traceData.length > 0)
        traceData[traceData.length - 1] += encodedLine;
    else
        traceData.push(encodedLine);

    lastLineWasEncodedRelatively = encodeRelatively;
    lastCompressedLineIndex = lineIndex;
}

function writePrefixedTraceLine(prefix, line)
{
    if(traceData.length >= traceDataSizeLimit)
    {
        persistTrace();
        traceData = [];
    }

    if(disableTraceCompression)
    {
        traceData.push(`${prefix}${line}`);
        return;
    }

    let encodedLine = "";

    let lineIndex = getCompressedLine(prefix);
    let distance = lineIndex - lastCompressedLineIndex;
    let encodeRelatively = (distance >= -9 && distance <= 9);
    if(encodeRelatively)
        encodedLine = `${String.fromCharCode(106 + distance)}|${line}`; // 'j' + distance => a ... s
    else
        encodedLine = `${lineIndex}|${line}`;

    if(lastLineWasEncodedRelatively && traceData.length > 0)
        traceData[traceData.length - 1] += encodedLine;
    else
        traceData.push(encodedLine);

    lastLineWasEncodedRelatively = false;
    lastCompressedLineIndex = lineIndex;
}

function getCompressedLine(line)
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

(function(sandbox)
{
    function formatIidWithSid(sid, iid)
    {
        const sdata = J$.smap[sid];
        if(sdata === undefined)
            return null;
        const idata = sdata[iid];
        if(idata === undefined)
            return null;

        let fileIndex = "";
        if(sdata.originalCodeFileName in knownCodeFiles)
            fileIndex = knownCodeFiles[sdata.originalCodeFileName];
        else
        {
            let codeFileName = sdata.originalCodeFileName;
            if(codeFileName.startsWith(globalPathPrefix))
                codeFileName = codeFileName.slice(globalPathPrefix.length);

            fileIndex = nextCodeFileIndex.toString();
            ++nextCodeFileIndex;

            knownCodeFiles[sdata.originalCodeFileName] = fileIndex;
            fs.writeSync(scriptsFile, `${fileIndex}\t${sdata.originalCodeFileName}\t${codeFileName}\n`);
        }

        return `${fileIndex}:${idata[0]}:${idata[1]}:${idata[2]}:${idata[3]}`;
    }

    function formatIid(iid)
    {
        return formatIidWithSid(J$.sid, iid);
    }

    function MicrowalkTraceGenerator()
    {
        this.invokeFunPre = function(iid, f, base, args, isConstructor, isMethod, functionIid, functionSid)
        {
            let functionName = "<anonymous>";
            if(f && f.name)
                functionName = f.name;
            else
            {
                let functionShadowObject = J$.smemory.getShadowObjectOfObject(f);
                if(functionShadowObject && functionShadowObject.functionName)
                    functionName = functionShadowObject.functionName;
            }

            // Handle special testcase begin marker function
            if(functionName === testcaseBeginFunctionName)
            {
                // Ensure that previous trace has been fully written (prefix mode)
                if(traceData.length > 0)
                    persistTrace();
                traceData = [];

                // If we were in prefix mode, store compression dictionaries
                if(currentTestcaseId === -1)
                {
                    prefixNextCompressedLineIndex = nextCompressedLineIndex;
                    prefixCompressedLines = compressedLines;
                }

                // Enter new testcase
                ++currentTestcaseId;
                compressedLines = Object.assign({}, prefixCompressedLines);
                nextCompressedLineIndex = prefixNextCompressedLineIndex;
                lastCompressedLineIndex = -1000;
                lastLineWasEncodedRelatively = false;
            }

            // Get function information
            let functionInfo = formatIidWithSid(functionSid, functionIid);
            if(functionInfo == null)
            {
                if(f && f.name !== undefined)
                    functionInfo = `E:${f.name}`;
                else
                    functionInfo = "E:?";
            }

            functionInfo += (isConstructor ? ":c" : ":");

            writeTraceLine(`c;${formatIid(iid)};${functionInfo};${functionName}`);

            pendingConditionalState = 0;

            return {f: f, base: base, args: args, skip: false};
        };

        this.invokeFun = function(iid, f, base, args, result, isConstructor, isMethod, functionIid, functionSid)
        {
            // Get function information
            let functionInfo = formatIidWithSid(functionSid, functionIid);
            if(functionInfo == null)
            {
                if(f && f.name !== undefined)
                    functionInfo = `E:${f.name}`;
                else
                    functionInfo = "E:?";
            }

            functionInfo += (isConstructor ? ":c" : ":");

            writeTraceLine(`R;${functionInfo};${formatIid(iid)}`);

            if(f && f.name === testcaseEndFunctionName)
            {
                // Close trace
                persistTrace();
                traceData = [];
            }

            pendingConditionalState = 0;

            return {result: result};
        };

        this.getFieldPre = function(iid, base, offset, isComputed, isOpAssign, isMethodCall)
        {
            // Ignore writes (handled in putFieldPre)
            if(isOpAssign)
                return {base: base, offset: offset, skip: false};

            // Retrieve shadow object
            let shadowObject = J$.smemory.getShadowObject(base, offset);

            if(shadowObject)
            {
                let formattedOffset = offset;
                if(typeof formattedOffset === "string")
                    formattedOffset = formattedOffset.replace(';', '_');

                writePrefixedTraceLine(`g;${formatIid(iid)};${shadowObject["owner"]["*J$O*"]};`, formattedOffset);
            }

            pendingConditionalState = 0;

            return {base: base, offset: offset, skip: false};
        };

        this.putFieldPre = function(iid, base, offset, val, isComputed, isOpAssign)
        {
            // Retrieve shadow object
            let shadowObject = J$.smemory.getShadowObject(base, offset);

            if(shadowObject)
            {
                let formattedOffset = offset;
                if(typeof formattedOffset === "string")
                    formattedOffset = formattedOffset.replace(';', '_');

                writePrefixedTraceLine(`p;${formatIid(iid)};${shadowObject["owner"]["*J$O*"]};`, formattedOffset);
            }

            // If val is an anonymous function, use the property as its name
            if(val && typeof val === "function" && val.name === "")
                J$.smemory.getShadowObjectOfObject(val).functionName = offset;

            pendingConditionalState = 0;

            return {base: base, offset: offset, val: val, skip: false};
        };

        this._return = function(iid, val)
        {
            writeTraceLine(`r;${formatIid(iid)}`);

            pendingConditionalState = 0;

            return {result: val};
        };

        this.conditional = function(iid, result)
        {
            writeTraceLine(`C;${formatIid(iid)}`);

            pendingConditionalState = 2;

            return {result: result};
        };

        this.endExpression = function(iid)
        {
            // Only record expressions when there is a pending conditional
            if(pendingConditionalState === 0)
                return;

            // Always skip expressions immediately following a conditional, as those simply span the entire conditional statement
            if(pendingConditionalState === 2)
            {
                pendingConditionalState = 1;
                return;
            }

            writeTraceLine(`e;${formatIid(iid)}`);

            pendingConditionalState = 0;
        };

        this.onReady = function(cb)
        {
            cb();
        };
    }

    sandbox.analysis = new MicrowalkTraceGenerator();
})(J$);



