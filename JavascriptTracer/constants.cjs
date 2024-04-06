/**
 * Common constants used for instrumentation and runtime.
 * CommonJS module to support inclusion by the CommonJS runtime module.
 */

const VALID_SUFFIXES = [".js", ".ts", ".cjs", ".mjs"]
const PLUGIN_SUFFIX = ".mw";
const PLUGIN_SUFFIX_REGEX = /\.mw\.(js|ts|cjs|mjs)$/i;
const NODE_MODULES_DIR_NAME = "node_modules"

const COMPUTED_OFFSET_INDICATOR = "___computed___";
const PRIMITIVE_INDICATOR = "___primitive___";

const INSTR_MODULE_NAME = "$$instr";
const FILE_ID_VAR_NAME = "$$fileId";
const INSTR_VAR_PREFIX = "$$";

const CHAINVARNAME = `${INSTR_VAR_PREFIX}vChain`;
const CALLVARNAME = `${INSTR_VAR_PREFIX}vCall`;
const THISVARNAME = `${INSTR_VAR_PREFIX}vThis`;
const ARGSVARNAME = `${INSTR_VAR_PREFIX}vArgs`;
const SWITCHLABELNAME = `${INSTR_VAR_PREFIX}vSwitchLabel`;
const SWITCHFALLTHROUGHVARNAME = `${INSTR_VAR_PREFIX}vSwitchFallthrough`;
const COMPUTEDVARNAME = `${INSTR_VAR_PREFIX}vComputed`;
const TERNARYIDNAME = `${INSTR_VAR_PREFIX}vTernaryId`;

module.exports = {
    VALID_SUFFIXES,
    PLUGIN_SUFFIX,
    PLUGIN_SUFFIX_REGEX,
    NODE_MODULES_DIR_NAME,

    COMPUTED_OFFSET_INDICATOR,
    PRIMITIVE_INDICATOR,

    INSTR_MODULE_NAME,
    FILE_ID_VAR_NAME,
    INSTR_VAR_PREFIX,

    CHAINVARNAME,
    CALLVARNAME,
    THISVARNAME,
    ARGSVARNAME,
    SWITCHLABELNAME,
    SWITCHFALLTHROUGHVARNAME,
    COMPUTEDVARNAME,
    TERNARYIDNAME
};