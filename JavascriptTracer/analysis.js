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

// Compressed lines from the trace prefix can be reused in all other traces
let nextCompressedLineIndex = 0;
let compressedLines = {};
let prefixNextCompressedLineIndex = 0;
let prefixCompressedLines = {};

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

function appendTraceData(data)
{
    if(traceData.length >= traceDataSizeLimit)
    {
        persistTrace();
        traceData = [];
    }

    traceData?.push(getCompressedLine(data));
}

function getCompressedLine(line)
{
    if(line in compressedLines)
        return compressedLines[line];
    else
    {
        let compressed = nextCompressedLineIndex.toString();
        ++nextCompressedLineIndex;

        compressedLines[line] = compressed;
        traceData?.push(`L|${compressed}|${line}`);

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
                compressedLines = prefixCompressedLines;
                nextCompressedLineIndex = prefixNextCompressedLineIndex;
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

            appendTraceData(`c;${formatIid(iid)};${functionInfo};${functionName}`);

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

            appendTraceData(`R;${functionInfo};${formatIid(iid)}`);

            if(f && f.name === testcaseEndFunctionName)
            {
                // Close trace
                persistTrace();
                traceData = [];
            }

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

                appendTraceData(`g;${formatIid(iid)};${shadowObject["owner"]["*J$O*"]};${formattedOffset}`);
            }

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

                appendTraceData(`p;${formatIid(iid)};${shadowObject["owner"]["*J$O*"]};${formattedOffset}`);
            }

            // If val is an anonymous function, use the property as its name
            if(val && typeof val === "function" && val.name === "")
                J$.smemory.getShadowObjectOfObject(val).functionName = offset;

            return {base: base, offset: offset, val: val, skip: false};
        };

        this._return = function(iid, val)
        {
            appendTraceData(`r;${formatIid(iid)}`);

            return {result: val};
        };

        this.conditional = function(iid, result)
        {
            appendTraceData(`C;${formatIid(iid)}`);

            return {result: result};
        };

        this.endExpression = function(iid)
        {
            appendTraceData(`e;${formatIid(iid)}`);
        };

        this.onReady = function(cb)
        {
            cb();
        };
    }

    sandbox.analysis = new MicrowalkTraceGenerator();
})(J$);



