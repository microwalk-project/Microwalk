/**
 * This file contains the instrumentation logic.
 * The instrumentation is done in two steps: First, we have a setup phase that does a few
 * necessary tweaks to the AST, and then we do the actual instrumentation.
 */

import * as t from "@babel/types";
import parser from "@babel/parser";
import template from "@babel/template";
import traverse, { visitors } from "@babel/traverse";
import generate from "@babel/generator";
import * as templates from "./templates.mjs";
import * as setup from "./instrument-setup.mjs";
import * as util from "./instrument-utility.mjs";
import * as pathModule from "node:path";
import * as constants from "./constants.cjs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { createRequire } from "node:module";
import * as fs from "node:fs";
import * as path from "node:path";

import * as importMetaResolve from "import-meta-resolve";

// Path of runtime.
const runtimePath = pathModule.resolve(pathModule.dirname(fileURLToPath(import.meta.url)), 'runtime.cjs');

// Collects files pending instrumentation.
let filesToInstrument = new Set();

// Collects files that have already been instrumented.
let filesInstrumented = new Set();

// Directory of the currently instrumented file.
let currentDir = null;

// Full path to the currently instrumented file.
let currentFilePath = null;

// Utility require function to check for node_modules of the currently instrumented file.
let requireFunc = null;

// set of functions that are defined in the instrumented file
const functionDefs = new Set();

function genPreAst(filePath, isModule) {
    let importStatement;
    if(isModule)
        importStatement = `import * as ${constants.INSTR_MODULE_NAME} from "${runtimePath}";`;
    else
        importStatement = `const ${constants.INSTR_MODULE_NAME} = require("${runtimePath}");`;

    return template.default.ast(`
        ${importStatement}
        const ${constants.FILE_ID_VAR_NAME} = ${constants.INSTR_MODULE_NAME}.registerScript("${filePath}");

        // Temporary variables for various instrumentation operations
        let ${constants.INSTR_VAR_PREFIX}vChain = [];
        let ${constants.INSTR_VAR_PREFIX}vCall = [];
        let ${constants.INSTR_VAR_PREFIX}vThis = [];
        let ${constants.INSTR_VAR_PREFIX}vArgs = [];
        let ${constants.INSTR_VAR_PREFIX}vSwitchLabel = [];
        let ${constants.INSTR_VAR_PREFIX}vSwitchFallthrough = [];
        let ${constants.INSTR_VAR_PREFIX}vTernaryId = [];
        let ${constants.COMPUTEDVARNAME};
    `);
}

/**
 * Instruments a given node and corresponding path so memory access is logged
 * during runtime
 * @param {Node} node - the node of the object that is being accessed, 
 * e.g. left side of an assigment 
 * @param {NodePath} path - corresponding path of the node
 * @param {boolean} isWrite - whether the memory access is a write operation or not
 * @param {boolean} insertBefore - (optional) whether logging should be done before the node is executed or after 
 */
function logMemoryAccessOfNode(node, path, isWrite, insertBefore=true, insertInto=false) {
    const [ids, properties] = util.gatherIdsAndOffsets(node, !isWrite);

    // do injection for each id in the (potential) chain
    for (let len = ids.length, index = len - 1; index >= 0; index--) {
        let offset;
        // don't try to get properties that don't exist
        if (properties.length < index)
            offset = null;
        else 
            offset = t.isIdentifier(properties[index]) ? properties[index].name : properties[index];

        let loc = path.node.loc;
        // get location info of identifier for writes
        if (t.isIdentifier(ids[index])) {
            loc = ids[index].loc ?? loc;
        }

        let id = t.isIdentifier(ids[index]) ? ids[index].name : ids[index];

        // generate ast
        let templateAst = templates.genMemoryAccessAst(id, offset, loc, isWrite);
        // and inject
        util.injectAst(templateAst, path, insertBefore, undefined, insertInto);
    }
}

/**
 * Checks whether a given path reads a node or not
 * @param {NodePath} path - path of the node that is being checked 
 * @returns {boolean} whether the node of the path is being read or not
 */
function isReadAccess(path) {
    const parent = path.parent;
    const key = path.listKey ?? path.key;

    // negative list of cases that AREN'T a read access
    switch (true) {
        case (t.isFunction(parent) && key === 'params'):
        case (t.isCatchClause(parent) && key === 'param'):
        case ((t.isAssignmentExpression(parent) || t.isAssignmentPattern(parent)) && key === 'left'):
        case (util.isMemberOrOptExpression(parent) && key === 'property'):
        case (util.isCallOrOptExpression(parent) && key === 'callee'):
        case (t.isVariableDeclarator(parent) && parent.init == null):
        case (t.isVariableDeclarator(path.parentPath.parent) && path.parentPath.parent.init == null):
        case (t.isImportDeclaration(parent)):
        case (key === 'id'):
        case (key === 'key'):
        case (t.isObjectProperty(parent) && path.key == "key"):
        case (key === 'label'):
        case (t.isForXStatement(parent) && key == 'left'):
            return false;
        case (t.isIdentifier(path) || util.isMemberOrOptExpression(path)): {
            // we need to check all the parents to ensure we aren't on the lhs of an assignment
            let currentNode = path, isRead = true;
            while (isRead && currentNode.parentPath && !t.isAssignmentExpression(currentNode) && !t.isStatement(currentNode.parentPath)) {
                currentNode = currentNode.parentPath;
                isRead &&= isReadAccess(currentNode);
            }

            return isRead;
        }
        default:
            return true;
    }
}

/**
 * Traverses up a nodepath and checks for a given marker key. Returns upon hitting the program node or finding a parent with the marker.
 * @param {NodePath} path path for the node that is to be checked
 * @param {string} logPropertyName logging marker to check for
 * @returns {boolean} whether a parent of the starting node has the given marker
 */
function checkParentsForLogged(path, logPropertyName) {
    let p = path;

    while (!t.isStatement(p.node) && p.listKey != "arguments" && p.parentPath) {
        p = p.parentPath;
        
        if (Object.hasOwn(p.node, logPropertyName) || Object.hasOwn(p.node, "ignore"))
            return logPropertyName;
    }

    return false;
}

const generalVisitor = {
    // skip any injected nodepaths 
    enter(path) {
        if (path.node.new) {
            path.skip();
        }
    },
    
    // inject pre code
    Program(path) {
        const queueLengths = new Map();
        let contexts;

        // save current queue length
        contexts = path._getQueueContexts();
        for (let context of contexts) {
            queueLengths.set(context, context.queue.length);
        }

        console.log("    type:", path.node.sourceType);
        const preAst = genPreAst(currentFilePath, path.node.sourceType === 'module');
        preAst.forEach(e => e.new = true);
        path.unshiftContainer('body', preAst);

        // remove injected node path(s) from queue
        contexts = path._getQueueContexts();
        for (let context of contexts) {
            for (let x = context.queue.length, targetlen = queueLengths.get(context); x > targetlen; x--) {
                context.queue.pop();
            }
        }
    }
}

const readVisitor = {
    "AssignmentExpression|AssignmentPattern|Identifier|MemberExpression|OptionalMemberExpression|CallExpression|OptionalCallExpression|ThisExpression|VariableDeclarator|UnaryExpression"(path) {
        // skip visited nodes
        if (path.node.readLogged || path.node.ignore)
            return;

        const node = path.node;
        let readNode = null, insertInto = true;

        switch(true) {
            case t.isVariableDeclarator(node):
                // for destructuring assignment we need left + right 
                if (t.isObjectPattern(node.id) || t.isArrayPattern(node.id)) {
                    readNode = node;
                }
                break;
            
            case t.isAssignmentPattern(node):
            case t.isAssignmentExpression(node):
                // rhs is needed for object destructuring names
                if (t.isObjectPattern(node.left) || t.isArrayPattern(node.left)) {
                    readNode = node.right;
                    break;
                }

                // lhs is not read during simple assignment
                if (node.operator === "=") {
                    node.left.readLogged = true;
                    return;
                }
                // rhs will be handled by later traversal
                
                break;
                
            case t.isThisExpression(node):
                readNode = node;
                break;
                
            case (t.isIdentifier(node) || util.isMemberOrOptExpression(node) || util.isCallOrOptExpression(node)):
                // ignore sequence expression abomination calls -> handle lower nodes
                if (t.isSequenceExpression(node?.callee?.object))
                    return;

                // Ignore export nodes
                if(t.isExportSpecifier(path.parentPath) || t.isExportDefaultDeclaration(path.parentPath))
                    return;

                if (isReadAccess(path) && !checkParentsForLogged(path, 'readLogged')) {
                   readNode = node;

                   // check whether parent is a postfix operation -> doesn't allow sequence expressions
                   let parentPath = path.parentPath;
                   while (!t.isStatement(parentPath) && !t.isProgram(parentPath)) {
                    if (t.isUpdateExpression(parentPath) || 
                        t.isVariableDeclaration(parentPath)) {
                        insertInto = false;
                        break;
                    }
                    parentPath = parentPath.parentPath;
                   }
                }
                break;
            case t.isUnaryExpression(path):
                // ignore typeof
                if (node.operator === "typeof") {
                    node.readLogged = true;
                    return;
                }
                break;
        }
    
        if (readNode) {
            logMemoryAccessOfNode(readNode, path, false, undefined, insertInto);
            // mark node as done
            readNode.readLogged = true;
        }

    }
};

const writeVisitor = {
    "AssignmentExpression|VariableDeclarator|AssignmentPattern|UpdateExpression|ForXStatement" (path) {
        // skip visited nodes
        if (path.node.writeLogged || path.node.ignore)
            return;

        let leftNode, insertBefore = true;

        // identify lhs
        switch (true) {
            case (t.isVariableDeclarator(path)):
                leftNode = path.node.id;

                // variable declaration without assignment isn't a write
                if (!path.node.init) {
                    path.node.writeLogged = true;
                    return;
                }
                // for loop variable declaration
                if (t.isForXStatement(path.parentPath.parent) && path.parentPath.key == "left" 
                    || t.isForStatement(path.parentPath.parent) && path.parentPath.key == "init") 
                    // insert before so the injection is the first statement of the body
                    insertBefore = true;
                // other variable declarations
                else 
                    insertBefore = false;
                break;

            case (t.isForXStatement(path)):
                // left side is being written to
                leftNode = path.node.left;
                break;

            case t.isAssignmentExpression(path):
            case t.isAssignmentPattern(path):
                // ensure we're not in function parameters
                let currentPath = path;
                while (!t.isStatement(currentPath) && !t.isProgram(currentPath)) {
                    if (currentPath.listKey === 'params') {
                        // mark node as done
                        path.node.writeLogged = true;
                        return;
                    }

                    currentPath = currentPath.parentPath;
                }

                leftNode = path.node.left;
                insertBefore = false;

                // for destructuring assignment we need left + right 
                if (t.isObjectPattern(leftNode) || t.isArrayPattern(leftNode)) {
                    leftNode = path.node;
                }
                break;

            case t.isUpdateExpression(path):
                leftNode = path.node.argument;
                break;
        }

        void logMemoryAccessOfNode(leftNode, path, true, insertBefore);

        // mark node as done
        path.node.writeLogged = true;
    },
};

const callVisitor = {
    "FunctionDeclaration|FunctionExpression|ArrowFunctionExpression|ObjectMethod|ClassMethod"(path) {
        const node = path.node;

        if (node.fnlogged) 
            return;

        let body = path.get('body');

        const injAst = templates.genFuncInfoAst(node.loc);
        util.injectAst(injAst, body, undefined, undefined, true);

        // add to set of function definitions
        functionDefs.add(node.loc);

        // ensure non-generator function has a return statement
        if (!node.generator && !t.isReturnStatement(body.get('body').at(-1))) {
            let returnStat = t.returnStatement();
            returnStat.loc = {
                start: node.loc.end,
                end: node.loc.end,
                filename: node.loc.filename 
            };
            body.pushContainer('body', returnStat);
        }

        node.fnlogged = true;
    },

    CallExpression: {
        exit(path) {
            const node = path.node;

            // skip visited nodes
            if (node.callLogged || node.ignore)
                return;

            // ignore import "calls"
            if (t.isImport(node)){console.log("ignore import!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
                return;}

            let fnName = null, baseNode = node;

            // find the identifier relating to the callee, so we can extract its name at runtime
            // due to setup every call is associated with a primary expression as a callee
            let isImport = false;
            while (!fnName) {
                if (t.isIdentifier(baseNode))
                    fnName = baseNode.name;
                    
                else if (t.isCallExpression(baseNode))
                    baseNode = baseNode.callee;

                else if (t.isMemberExpression(baseNode))
                    // calls are only member expressions if they WERE of the form a.b() and are now of the form $$call.call(a)
                    // (since member expression calls have been squashed) so the OBJECT has the relevant info
                    baseNode = baseNode.object;

                else if (t.isFunctionExpression(baseNode) || t.isSequenceExpression(baseNode)) {
                    fnName = "undefined";
                }
                else if (t.isImport(baseNode)) {
                    fnName = "import";
                    isImport = true;
                }

                else 
                    throw new Error(`Unexpected parent node type ${baseNode.type} in call`);
            }

            // Handle `[await] import()`
            if (isImport && node.arguments.length === 1) {

                // If the import is known already, instrument it statically
                if(t.isStringLiteral(node.arguments[0])) {
                    path.node.arguments[0] = t.stringLiteral(enqueueForInstrumentation(node.arguments[0].value, true));
                }
                else {
                    // Call runtime.instrumentDynamic(await import.meta.resolve('...'))
                    const dynamicImportFuncAst = t.memberExpression(t.identifier(constants.INSTR_MODULE_NAME), t.identifier("instrumentDynamic"));
                    const resolveFuncAst = t.memberExpression(t.memberExpression(t.identifier("import"), t.identifier("meta")), t.identifier("resolve"));
                    const resolveModuleAst = t.callExpression(resolveFuncAst, [node.arguments[0]]);
                    const dynamicImportAst = t.callExpression(dynamicImportFuncAst, [resolveModuleAst]);
                    
                    // Rewrite required target
                    path.node.arguments[0] = dynamicImportAst;
                }

                // No further handling
                return;
            }

            // Handle calls to require()
            if(fnName === "require" && node.arguments.length === 1) {
                
                // If the import is known already, instrument it statically
                if(t.isStringLiteral(node.arguments[0])) {
                    path.node.arguments[0] = t.stringLiteral(enqueueForInstrumentation(node.arguments[0].value, false));
                }
                else {
                    // Call runtime.instrumentDynamic(require.resolve(moduleName))
                    const dynamicImportFuncAst = t.memberExpression(t.identifier(constants.INSTR_MODULE_NAME), t.identifier("instrumentDynamic"));
                    const resolveFuncAst = t.memberExpression(t.identifier("require"), t.identifier("resolve"));
                    const resolveModuleAst = t.callExpression(resolveFuncAst, [node.arguments[0]]);
                    const dynamicImportAst = t.callExpression(dynamicImportFuncAst, [resolveModuleAst]); 
                    
                    // Rewrite required target
                    path.node.arguments[0] = dynamicImportAst;
                }

                // No further handling
                return;
            }

            // generate ast for call trace
            const templateAst = templates.genCallAst(fnName, node.loc);
            // and inject
            util.injectAst(templateAst, path, undefined, undefined, true);
            
            node.callLogged = true;

            // skip ret2 for yield delegation 
            if (t.isYieldExpression(path.parent) && path.parent.delegate)
                return;

            // inject ret2 for returning from a call
            const ret2Ast = templates.genReturnAst(false, node.loc);

            // due to previous setup ALL calls have their result assigned to a var
            // implicit return should be directly after the call and before the call result variable

            // find the parent assignment and corresponding sequence expression
            let seqEx = path, foundId = false, callIdPath = seqEx.getNextSibling();
            while (!foundId && !t.isStatement(seqEx)) {
                if (t.isSequenceExpression(seqEx.parent)) {
                    // call id should be next sibling in sequence expression
                    callIdPath = seqEx.getNextSibling();
                    if (t.isIdentifier(callIdPath)) {
                        foundId = true;
                        break;
                    }
                }

                seqEx = seqEx.parentPath;
            }

            // replace with sequence expression of return and result variable
            ret2Ast.expression.new = true;
            callIdPath.replaceWith(t.sequenceExpression([ret2Ast.expression, callIdPath.node]));

            node.callLogged = true;
        }
    },


    "ReturnStatement": {
        // instrument on exit so return(1) is the last emission of the function
        exit(path) {
            const node = path.node;

            // skip already visited nodes
            if(node.returnLogged || node.ignore)
                return;

            const ret1Ast = templates.genReturnAst(true, node.loc);
            util.injectAst(ret1Ast, path);

            node.returnLogged = true;
        }
    },

    "YieldExpression": {
        // instrument on exit so injection is last emission
        exit(path) {
            const node = path.node;
            
            // skip already visited
            if (node.yieldLogged || node.ignore) 
            return;

            // find statement parent
            let statementParentPath = util.getStatementParent(path);

            // inject before to log returning from yield
            const ret1Ast = templates.genReturnAst(true, node.loc);
            util.injectAst(ret1Ast, path, true);

            // inject after yield to log resumption 
            // except for the final yield
            if (statementParentPath.parent.body.at(-1) != statementParentPath.node) {
                const yieldResAst = templates.genYieldAst(true, node.loc);
                util.injectAst(yieldResAst, path, false);
            }

            node.yieldLogged = true;
        }
    }
}

const branchVisitor = {
    // general:
    // * add "taken" emission to conditional body

    // notes:
    //  conditional expressions have EXPRESSIONS as consequence/alternate -> use SEQUENCEEXPRESSION
    //  switchcase has an ARRAY for consequent -> insert statement into array (mb works out of the box?)
    "IfStatement|SwitchCase|While|For"(path) {
        const node = path.node;
        let bodyPath = null;

        if (node.branchlogged)
            return;

        // get 'body' path
        switch (true) {
            case (t.isIfStatement(node)): 
                bodyPath = path.get("consequent");
                break;
            case t.isSwitchCase(node):
                // ignore the switch case if it's empty
                if (node.consequent.length === 0)
                    return 

                // consequent is an array and we want a node, so wrap everything in a block statement and use that
                bodyPath = path.get("consequent")[0];
                bodyPath.replaceWith(t.blockStatement(path.get("consequent").map(p => p.node)));
                // remove remaining elements (since they're now in the block statement)
                node.consequent.splice(1);

                break;
            
            case (t.isFor(node) || t.isWhile(node)):
                util.ensureBlock(path);
                bodyPath = path.get("body");
                break;
        }

        const bodyLoc = bodyPath.has('body') ? bodyPath.node.body[0]?.loc : bodyPath.node.loc;

        let bodyAst = templates.genBranchAst(node.loc, bodyLoc ?? bodyPath.node.loc);
        util.injectAst(bodyAst, bodyPath);
        
        // handle alternate (if statement)
        if (node.alternate) {
            const alternate = path.get('alternate');
            let alternateLoc = alternate.node.body?.loc ?? alternate.node.body?.at(0)?.loc ?? alternate.node.loc;

            bodyAst = templates.genBranchAst(node.loc, alternateLoc);
            util.injectAst(bodyAst, alternate);
        }

        node.branchlogged = true;
    },

    ConditionalExpression(path) {
        if (path.node.branchlogged) {
            return;
        }

        const node = path.node;
        const consequent = path.get('consequent');
        const alternate = path.get('alternate');

        const branchConsAst = templates.genBranchAst(node.loc, consequent.node.loc);
        util.injectAst(branchConsAst, consequent, true, undefined, true);

        const branchAltAst = templates.genBranchAst(node.loc, alternate.node.loc);
        util.injectAst(branchAltAst, alternate, true, undefined, true);

        node.branchlogged = true;
    },

    "ContinueStatement|BreakStatement"(path) {
        const label = path.node.label?.name;
        let dest = path;
        
        if (label) {
            // look for labeled statement with correct label
            let pathsToCheck = [path.parentPath];
            let currentPath;
            while (pathsToCheck.length > 0) {
                currentPath = pathsToCheck.shift();

                if (t.isLabeledStatement(currentPath) && currentPath.node.label.name == label) {
                    // ding ding - found the label
                    dest = currentPath;
                    break;
                }

                // add all siblings to queue
                if (currentPath.listKey == 'body') {
                    pathsToCheck.push(...currentPath.getAllPrevSiblings());
                    pathsToCheck.push(...currentPath.getAllNextSiblings());
                }

                // remove children from q
                if (currentPath.has('body') && pathsToCheck.length > 0) {
                    pathsToCheck = pathsToCheck.filter(e => {
                        currentPath.node.body.includes(e)
                    })
                }

                // add parent to queue
                if (!t.isProgram(currentPath)) {
                    pathsToCheck.push(currentPath.parentPath);
                }
            }
        } else {
            // look for parent loop of statement
            while(dest.parentPath && !t.isLoop(dest) && !t.isProgram(dest)) {
                    dest = dest.parentPath;
            }
        }

        if (t.isBreakStatement(path)) {
            // destination is next sibling of label/loop
            const nextSibling = dest.getNextSibling();
            if (nextSibling.node) {
                dest = nextSibling;
            }
            else {
                dest.node.loc.start = dest.node.loc.end;
            }
        }

        const jmpAst = templates.genJumpAst(path.node.loc, dest.node.loc);
        util.injectAst(jmpAst, path);
    }
}

const importVisitor = {
    ImportDeclaration(path) {
        const node = path.node;
        
        const importModule = node.source.value;
        
        // Enqueue for static instrumentation and update path
        node.source.value = enqueueForInstrumentation(importModule, true);
    }
}

// Merge visitors
const instrumentationVisitor = visitors.merge([generalVisitor, writeVisitor, readVisitor, callVisitor, branchVisitor, importVisitor]);

// Unwrap enter
instrumentationVisitor['enter'] = instrumentationVisitor['enter'][0];

/**
 * Enqueues the given module for instrumentation.
 * @param {string} module The module to instrument.
 * @param {boolean} isEsModule Whether the module is an ES module or not.
 * @returns {string} The path to the instrumented module.
 */
function enqueueForInstrumentation(module, isEsModule)
{
    // Get file path of import
    let modulePath = module;
    if(module.startsWith('.'))
    {
        let potentialPath = pathModule.resolve(currentDir, module);
        if(fs.existsSync(potentialPath))
            modulePath = potentialPath;
    }
    if(!module.startsWith('/'))
    {
        // Some named module, try to resolve it
        if(isEsModule)
            modulePath = importMetaResolve.resolve(module, pathToFileURL(currentFilePath)); //import.meta.resolve(module, pathToFileURL(currentFilePath));
        else
            modulePath = requireFunc.resolve(module);

        if(modulePath.startsWith('file://'))
            modulePath = fileURLToPath(modulePath);
    }

    // Put in instrumentation queue, if we could resolve the file
    // We do not translate imports of built-in modules
    if(modulePath != module)
    {
        if(!filesToInstrument.has(modulePath) && !filesInstrumented.has(modulePath))
            console.log(`    enqueueing ${module} (-> ${modulePath}) for instrumentation`);

        filesToInstrument.add(modulePath);
        return getInstrumentedName(modulePath);
    }
    else
    {
        console.log(`    SKIPPING ${module} for instrumentation`);
    }

    return module;
}

export function getInstrumentedName(filePath) {
    const suffix = constants.VALID_SUFFIXES.find((e) => filePath.endsWith(e));
    return filePath.replace(suffix, `${constants.PLUGIN_SUFFIX}${suffix}`);
}

import printAST from "ast-pretty-print";
import { notEqual } from "node:assert";
import { is } from "@babel/types";
export function instrumentAst(filePath, ast) {
    currentDir = pathModule.dirname(filePath);
    currentFilePath = filePath;

    requireFunc = createRequire(filePath);

    // Setup: Simplify AST, split up certain constructs
    traverse.default(ast, setup.setupVisitor);
    traverse.default(ast, setup.setupCallExpressionsVisitor);

    // Debugging: Dump intermediate AST after setup
    fs.writeFileSync(getInstrumentedName(filePath) + ".tmp", generate.default(ast, {comments: false}).code);
    fs.writeFileSync(getInstrumentedName(filePath) + ".ast", printAST(ast));

    // Actual instrumentation
    try { 
        traverse.default(ast, instrumentationVisitor);
    }
    catch (error) {
        console.error(error.message);
        console.error(error.stack);
    }
}

export function instrumentFileTree(filePath) {

    // If the file is not already instrumented, instrument it and all its dependencies
    let filePathInstrumented = getInstrumentedName(filePath);
    if (!fs.existsSync(filePathInstrumented)) {
        try {
            // Pending files to instrument
            filesToInstrument.add(filePath);
            while (filesToInstrument.size > 0) {
                let currentFile = filesToInstrument.values().next().value;
                filesToInstrument.delete(currentFile);
                filesInstrumented.add(currentFile);

                // Skip if already instrumented
                let currentFileInstrumented = getInstrumentedName(currentFile);
                if (fs.existsSync(currentFileInstrumented))
                    continue;

                console.log(`Instrumenting ${currentFile}`);

                // Read and parse given file
                const code = fs.readFileSync(currentFile, { encoding: "utf-8" });
                const ast = parser.parse(code, { sourceFilename: path.basename(currentFile), sourceType: "unambiguous" });

                // Do instrumentation
                instrumentAst(currentFile, ast);

                // Get absolute path of instrumented file and write it
                console.log(`    writing ${getInstrumentedName(currentFile)}`);
                fs.writeFileSync(getInstrumentedName(currentFile), generate.default(ast, {comments: false}).code);
            }
        } catch (error) {
            console.error(error.message);
            console.error(error.stack);
        }
    }
}