import template from "@babel/template";
import * as constants from "./constants.cjs";

/**
 * Generates a string formatting relevant source information given a SourceLocation object
 * @param {t.SourceLocation} loc 
 * @returns {string} formatted string with location information
 */
export function genLocString(loc) {
    if(!loc)
        throw new Error("Undefined source location");
    
    return `${loc.start.line}:${loc.start.column}:${loc.end.line}:${loc.end.column}`;
}

/**
 * Generates an ast for injecting logging of memory access
 * @param {String} objId - identifier name of the object that is being accessed
 * @param {String} offset - offset that is being accessed -- i.e. the property for objects or the index for arrays. 
 * Defaults to null if none is given
 * @param {t.SourceLocation} loc - location object of the memory access 
 * @param {boolean} isWrite - boolean whether the memory access is a write operation 
 * @returns {ast} an ast that can be inserted
 */
export function genMemoryAccessAst(objId, offset = null, loc, isWrite) {

    let offsetStr = '';
    if (offset) {
        if (offset == constants.COMPUTED_OFFSET_INDICATOR) {
            offsetStr = constants.COMPUTED_OFFSET_INDICATOR;
        }
        else if(offset == constants.COMPUTEDVARNAME) {
            offsetStr = `${constants.COMPUTED_OFFSET_INDICATOR}`;
        }
        else {
            offsetStr = `${offset}`;
        }
    }

    let objIdVal = objId;
    if (objId == constants.PRIMITIVE_INDICATOR) {
        objIdVal = `'${constants.PRIMITIVE_INDICATOR}'`;
    }

    // TODO handle optional chains
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.writeMemoryAccess(${constants.FILE_ID_VAR_NAME}, '${genLocString(loc)}', ${objIdVal}, '${offsetStr}', ${isWrite}, ${constants.COMPUTEDVARNAME});
    `);
}

/**
 * Generates an ast for injecting logging of calls
 * @param {string} fnObj - callee
 * @param {Object} source - location object of the source of the call (caller)
 * @returns 
 */
export function genCallAst(fnObj, source) {
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.startCall(${constants.FILE_ID_VAR_NAME}, '${genLocString(source)}', ${fnObj});  
    `);
}

/**
 * Generates an ast for injection logging of function information ()
 * @param {Object} location - location object of the function definition
 * @returns 
 */
export function genFuncInfoAst(location) {
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.endCall(${constants.FILE_ID_VAR_NAME}, '${genLocString(location)}');
    `);
}

/**
 * Generates an ast for injecting logging of returns
 * @param {boolean} isReturn1 - whether this is the first return (return statement inside a function) or not (immediately after call)
 * @param {Object} location - location object of return statement 
 * @returns 
 */
export function genReturnAst(isReturn1, location) {
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.writeReturn(${constants.FILE_ID_VAR_NAME}, '${genLocString(location)}', ${isReturn1});
    `);
}

/**
 * Generates an ast for injecting logging of yield expressions
 * @param {boolean} isResume - whether the generator is being resumed or paused
 * @param {object} location - location object of yield expression
 * @returns 
 */
export function genYieldAst(isResume, location) {
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.writeYield(${constants.FILE_ID_VAR_NAME}, '${genLocString(location)}', ${isResume});
    `);
}

/**
 * Generates an ast for injecting logging of branches
 * @param {t.SourceLocation} source - location object of the branching statement
 * @param {t.SourceLocation} bodyLocation - location object that is being branched to (body of the branching statement)
 * @returns 
 */
export function genBranchAst(source, bodyLocation) {
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.writeBranch(${constants.FILE_ID_VAR_NAME}, '${genLocString(source)}', '${genLocString(bodyLocation)}');
    `);
}

export function genJumpAst(source, dest) {
    return template.default.ast(`
        ${constants.INSTR_MODULE_NAME}.writeJump(${constants.FILE_ID_VAR_NAME}, '${genLocString(source)}', '${genLocString(dest)}');
    `);
}