// JALANGI DO NOT INSTRUMENT

const fs = require('fs');

const globalPathPrefix = process.env.JS_PATH_PREFIX;

const testcaseBeginFunctionName = "__MICROWALK_testcaseBegin";
const testcaseEndFunctionName = "__MICROWALK_testcaseEnd";

const traceDirectory = process.env.JS_TRACE_DIRECTORY;

let currentTestcaseId = -1;
let traceData = []; // Prefix mode
let scriptsFile = fs.openSync(`${traceDirectory}/scripts.txt`, "w");

let knownCodeFiles = {};

function storeTrace()
{
	let traceFilePath = currentTestcaseId === -1 ? `${traceDirectory}/prefix.trace` : `${traceDirectory}/t${currentTestcaseId}.trace`;
	
	console.log(`Open ${traceFilePath}`);
	
	let traceFile = fs.openSync(traceFilePath, "w");
	
	console.log(`Store`);
	fs.writeSync(traceFile, traceData.join('\n'));
	
	console.log(`Close`);
	fs.closeSync(traceFile);
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

        let codeFileName = sdata.originalCodeFileName;
        if(knownCodeFiles[sdata.originalCodeFileName] !== undefined)
            codeFileName = knownCodeFiles[sdata.originalCodeFileName];
        else
        {
            if(codeFileName.startsWith(globalPathPrefix))
                codeFileName = codeFileName.slice(globalPathPrefix.length);

            knownCodeFiles[sdata.originalCodeFileName] = codeFileName;
            fs.writeSync(scriptsFile, `${sdata.originalCodeFileName}\t${codeFileName}\n`);
        }

        return `${codeFileName}:${idata[0]}:${idata[1]}:${idata[2]}:${idata[3]}`;
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
                // Ensure that old trace has been written (prefix mode)
                if(traceData !== null)
                    storeTrace();

                // Enter new testcase
                ++currentTestcaseId;
				traceData = [];
            }

            // Get function information
            let functionInfo = formatIidWithSid(functionSid, functionIid);
            if(functionInfo == null)
            {
                if(f && f.name !== undefined)
                    functionInfo = `[extern]:${f.name}`;
                else
                    functionInfo = "[extern]:?";
            }

            functionInfo += (isConstructor ? ":c" : ":");

            traceData?.push(`Call;${formatIid(iid)};${functionInfo};${functionName}`);

            return {f: f, base: base, args: args, skip: false};
        };

        this.invokeFun = function(iid, f, base, args, result, isConstructor, isMethod, functionIid, functionSid)
        {
            // Get function information
            let functionInfo = formatIidWithSid(functionSid, functionIid);
            if(functionInfo == null)
            {
                if(f && f.name !== undefined)
                    functionInfo = `[extern]:${f.name}`;
                else
                    functionInfo = "[extern]:?";
            }

            functionInfo += (isConstructor ? ":c" : ":");

            traceData?.push(`Ret2;${functionInfo};${formatIid(iid)}`);

            if(f && f.name === testcaseEndFunctionName)
            {
                // Close trace
                storeTrace();
                traceFile = null;
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

                traceData?.push(`Get;${formatIid(iid)};${shadowObject["owner"]["*J$O*"]};${formattedOffset}`);
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

                traceData?.push(`Put;${formatIid(iid)};${shadowObject["owner"]["*J$O*"]};${formattedOffset}`);
            }

            // If val is an anonymous function, use the property as its name
            if(val && typeof val === "function" && val.name === "")
                J$.smemory.getShadowObjectOfObject(val).functionName = offset;

            return {base: base, offset: offset, val: val, skip: false};
        };

        this._return = function(iid, val)
        {
            traceData?.push(`Ret1;${formatIid(iid)}`);

            return {result: val};
        };

        this.conditional = function(iid, result)
        {
            traceData?.push(`Cond;${formatIid(iid)}`);
			
            return {result: result};
        };

        this.endExpression = function(iid)
        {
            traceData?.push(`Expr;${formatIid(iid)}`);
        };

        this.onReady = function(cb)
        {
            cb();
        };
    }

    sandbox.analysis = new MicrowalkTraceGenerator();
})(J$);


