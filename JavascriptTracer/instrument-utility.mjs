/**
 * Contains utility functions for the instrumentation process.
 */

import * as t from "@babel/types";
import * as constants from "./constants.cjs";

/**
 * 
 * @param {Node} node - 
 * @param {boolean} isRead - whether the ids and offsets that are being read should be returned; defaults to true
 * @returns {Array<t.Identifier|string, string>} An array consisting of two arrays, the first one has ids that are used while the second one has offsets. The order of the arrays is matching meaning the offset of id[0] is at offsets[0]
 */
export function gatherIdsAndOffsets(node, isRead=true) {
    const ids = [];
    const properties = [];

    switch (true) {
        // ignore meta properties
        case (t.isMetaProperty(node)):
            return [[], []];
        // ignore window things / self
        case (node?.name === "window" 
                || node?.name === "self" 
                || (t.isMemberExpression(node) && node.object.name === "window")):
            return [[], []];

        case (t.isIdentifier(node)):
            return [[node], properties];

        case (t.isLiteral(node)): {
            const primitive = t.identifier(constants.PRIMITIVE_INDICATOR);
            primitive.readLogged = true;
            primitive.loc = node.loc;
            return [[primitive], properties];
        }

        case t.isUpdateExpression(node):
        case t.isUnaryExpression(node):
        case t.isYieldExpression(node):
        case t.isAwaitExpression(node):
        case (t.isRestElement(node)): 
            return gatherIdsAndOffsets(node.argument);
        
        case (t.isThisExpression(node)): 
            ids.push('this');
            break;

        case t.isObjectMember(node): {
            return gatherIdsAndOffsets(node.key);
        }

        case t.isTupleExpression(node):
        case t.isArrayExpression(node): {
            for (let e of node.elements) {
                const [arrayIds, arrayProperties] = gatherIdsAndOffsets(e);
                ids.push(...arrayIds);
                properties.push(...arrayProperties);
            }
            break;
        }

        case t.isRecordExpression(node):
        case t.isObjectExpression(node): {
            for (let p of node.properties) {
                const [objIds, objProps] = gatherIdsAndOffsets(p);
                ids.push(...objIds);
                properties.push(...objProps);
            }
            break;
        }

        case t.isLogicalExpression(node):
        case t.isBinaryExpression(node): {
            const [leftIds, leftProps] = gatherIdsAndOffsets(node.left);
            const [righIds, rightProps] = gatherIdsAndOffsets(node.right);
            ids.push(...leftIds);
            ids.push(...righIds);
            properties.push(...leftProps);
            properties.push(...rightProps);
            break;
        }
        
        case (isCallOrOptExpression(node)): { 
            const [calleeIds, calleeProps] = gatherIdsAndOffsets(node.callee);

            if (calleeProps.length !== 0) {
                properties.push(...calleeProps);
            }

            ids.push(...calleeIds);
            break;
        }

        // due to previous setup there are no member expression chains (i.e. each member expression is only a single obj.prop / obj[prop] expression)
        case (isMemberOrOptExpression(node)): {
            ids.push(...gatherIdsAndOffsets(node.object)[0]);

            // gather property ids
            // computed properties are a special case -- i.e. o[prop]
            if (node.computed) {
                const property = node.property;
                
                // due to setup all computed properties are guaranteed to be either
                // numeric/string literals or sequence expressions ending in an identifier
                if (t.isNumericLiteral(property) || t.isStringLiteral(property)) {
                    properties.push(`[${property.value}]`);
                }
                else if (t.isSequenceExpression) {
                    properties.push(property.expressions.at(-1));
                }
                else if (t.isIdentifier(property)) {
                    properties.push(property);
                }
                else if (t.isCallExpression(property) 
                            && t.isMemberExpression(property.callee) 
                            && property.callee.object.name == constants.COMPUTEDVARNAME) {
                    properties.push(constants.COMPUTED_OFFSET_INDICATOR);
                }
            }
            // regular properties -- i.e. o.prop
            else {
                properties.push(...gatherIdsAndOffsets(node.property)[0]);
            }

            break;
        }
        
        case t.isVariableDeclarator(node):
        case t.isAssignmentExpression(node): {
            const pat = node.id ?? node.left;
            const base = node.init ?? node.right;
            // destructuring assignment
            if (t.isObjectPattern(pat)) {
                const [baseId] = gatherIdsAndOffsets(base, isRead);
                const [patIds] = gatherIdsAndOffsets(pat, isRead);
                for (let i = 0; i < patIds.length; i++) {
                    const bId = i >= baseId.length ? baseId[0] : baseId[i];
                    ids.push(bId);
                    properties.push(patIds[i]);
                }
            } 
            else if (t.isArrayPattern(pat)) {

                // ignore array expressions on rhs - elements will be handled by later visitors
                // override readlogged since elements of array should be logged individually instead
                if (t.isArrayExpression(base) && isRead) {
                    node.readLogged = false;
                    return [[], []];
                }

                else if (t.isArrayExpression(base) && !isRead) {
                    const [arrayIds, arrayProps] = gatherIdsAndOffsets(pat);
                    ids.push(...arrayIds);
                    properties.push(...arrayProps);
                }

                // not an array -> singular object / id
                // use right as base 
                else {
                    const [baseId] = gatherIdsAndOffsets(base, isRead);
                    // we only care about the number of elements in the pattern
                    const eleAmount = pat.elements.length;
                    // add the base + index for each element in the pattern 
                    for(let i = 0; i < eleAmount; i++) {
                        if (!pat.elements[i]) {
                            // skip null elements
                            continue;
                        }

                        ids.push(baseId[0]);
                        properties.push(`${i}`);
                    }
                    properties.reverse();
                }
            } 
            break;
        }

        case (t.isPattern(node)): {
            let elementsOrProperties;
            switch (true) {
                // array
                case t.isArrayPattern(node):
                    elementsOrProperties = node.elements;
                    break;

                // obj
                case t.isObjectPattern(node):
                    elementsOrProperties = node.properties;
                    break;
            }

            // gather all pattern ids
            for (let e of elementsOrProperties) {
                // unwrap object property value if need be
                if (e && t.isObjectProperty(e)) {
                    e = isRead ? e.key : e.value ?? e.key;
                }
                
                if (t.isIdentifier(e)) 
                    ids.push(e);
                // skip null elements
                else if (!e)
                    continue;
                // other (e.g. rest element, pattern, expression)
                else {
                    const extraIdsAndOffsets = gatherIdsAndOffsets(e);
                    ids.push(...extraIdsAndOffsets[0]);
                    properties.push(...extraIdsAndOffsets[1]);
                }
            }

            break;
        }
    }
    // console.log(node); 

    return [ids, properties];
}

/**
 * Injects a given ast into a path as a sibling node
 * @param {ast} ast - the ast to inject
 * @param {NodePath} path - the path to inject into
 * @param {boolean} [insertBefore=true] - whether to insert the given ast before (or after if false); default is true
 * @param {boolean} [markAsNew=true] whether to mark the inserted nodes as new, i.e. these nodes and the path will be SKIPPED by instrumentation; default true
 * @param {boolean} [insertInto=false] whether to try to insert before the path as a statement or INTO the path as an expression; default false
 * @returns {NodePath<N>[]} array of paths that were inserted
 */
export function injectAst(ast, path, insertBefore=true, markAsNew=true, insertInto=false) {
    const queueLengths = new Map();
    let parent = path, contexts, newPaths = [], isTest = false;
    let containerInsert = path.has('body');

    while (!insertInto && (!t.isStatement(parent) && !containerInsert)) {
        isTest = parent.key == 'test' || isTest;
        parent = parent.parentPath;
    }
    
    // save current queue length
    contexts = parent._getQueueContexts();
    for (let context of contexts) {
        queueLengths.set(context, context.queue?.length ?? 0);
    }

    // mark injection ast as new to stop repeated traversal
    if (markAsNew) {
        ast.new = true;
        if (markAsNew && insertInto && ast?.expression) {
            ast.expression.new = true;
        }
    }


    
    // inject as sibling of the statement parent
    if (containerInsert || t.isBlockStatement(path)) {
        // insert INTO containers and block statements
        // this means the new node will be placed at the start or end of the container

        const insertFn = insertBefore ? parent.unshiftContainer : parent.pushContainer
        // ensure body is a block statement
        const bodyPath = path.get('body');
        if (!t.isBlockStatement(bodyPath) && !Array.isArray(bodyPath)) {
            util.ensureBlock(parent);
            // copy location info to new block
            parent.node.body.loc = bodyPath.node.loc;
        }

        // ensure body is an array of statements so we can do a container insertion
        if (!Array.isArray(parent.get('body'))) {
            parent = parent.get('body');
        }

        newPaths = insertFn.call(parent, 'body', ast);
    } else if (t.isReturnStatement(parent) && !insertBefore && t.isExpressionStatement(ast)) {
        // special handling of return statements since anything BEHIND a return is unreachable
        // this only works with EXPRESSIONS

        const retArgument = parent.get('argument');
        const expression = ast.expression;
        expression.new = true;
        
        if (t.isSequenceExpression(retArgument)) {
            // already a sequence expression so simply prepend 
            retArgument.unshiftContainer('expressions', expression);
        }
        else {
            // wrap argument in a sequence expression
            const seqExpression = t.toSequenceExpression([ast, retArgument.node]);
            retArgument.replaceWith(seqExpression);
        }
        newPaths.push(parent.get('argument'));
    } 
    else if (isTest && ( t.isExpression(ast) || t.isExpressionStatement(ast) ) && t.isIfStatement(parent)) {
        // special handling when the node is in the test of a statement
        // do while is special so it gets its own case
        const testExpression = parent.get('test');
        
        // an expression can be inserted before by making the test into a sequence expression
        if (insertBefore) {
            const expression = ast.expression;
            expression.new = true;

            if (t.isSequenceExpression(testExpression)) {
                testExpression.unshiftContainer('expressions', expression);
            } else {
                // wrap in a sequence expression
                const seqExpression = t.toSequenceExpression([ast, testExpression.node]);
                testExpression.replaceWith(seqExpression);
            }

            newPaths.push(parent.get('test'));
        } 
        else {
            // insert into consequent body
            const consequent = parent.get('consequent');
            consequent.unshiftContainer('body', ast);
            // insert into alternate body (if applicable)
            const alternate = parent.get('alternate');
            if (alternate.node) {
                alternate.unshiftContainer('body', ast);
            } else {
                // create a new block statement for the alternate and insert it there
                parent.alternate = t.blockStatement([ast]);
            }

            newPaths.push(parent.get('consequent'));
            newPaths.push(parent.get('alternate'));
        }
    }
    else if (isTest && ( t.isWhile(parent) || t.isForStatement(parent))) {
    const testExpression = parent.get('test');
        
        // an expression can be inserted before by making the test into a sequence expression
        if (insertBefore) {
            if (t.isExpressionStatement(ast)) {
                const expression = ast.expression;
                expression.new = true;

                if (t.isSequenceExpression(testExpression)) {
                    testExpression.unshiftContainer('expressions', expression);
                } else {
                    // wrap in a sequence expression
                    const seqExpression = t.toSequenceExpression([ast, testExpression.node]);
                    testExpression.replaceWith(seqExpression);
                }

                newPaths.push(parent.get('test'));
            } else {
                // insert before statement
                newPaths = parent.insertBefore(ast);
                // also insert THE SAME NODE as last statement of body
                parent.get('body').pushContainer('body', ast);
                // and before every continue 
                const continues = getAllContinues(parent);

                for (let statPath of continues) {
                    statPath.insertBefore(ast);
                }
            }
        } else {
            // insert into body as first statement
            ensureBlock(parent);
            const body = parent.get('body');
            body.unshiftContainer('body', ast);
    
            newPaths.push(body);
        }
    }
    else {
        // ensure location info persists
        const oldNode = path.node;

        // use the standard library insertBefore / insertAfter
        if(insertInto) {
            // ensure path has a container
            let insertionPath = path;
            while (!insertionPath.container) {
                insertionPath = insertionPath.parentPath;
            }

            // insert around path
            const insertFn = insertBefore ? insertionPath.insertBefore : insertionPath.insertAfter;
            newPaths = insertFn.call(insertionPath, ast);
        }
        else {
            // insert around statement parent
            const insertFn = insertBefore ? parent.insertBefore : parent.insertAfter;
            newPaths = insertFn.call(parent, ast);
        }

        if (!path.node.loc) {
            path.node.loc = oldNode.loc;
        }
    }
    
    // remove injected node path(s) from queue
    contexts = parent._getQueueContexts();
    for (let context of contexts) {
        for (let x = context.queue?.length ?? 0, targetlen = queueLengths.get(context); x > targetlen; x--) {
            context.queue.pop();
        }
    }

    return newPaths;
}

/**
 * Checks whether a node (path) is an (optional) call expression.
 * @param {NodePath|Node} node node to check 
 * @returns {boolean}
 */
export function isCallOrOptExpression(node) {
    return t.isOptionalCallExpression(node) || t.isCallExpression(node);
}

/**
 * Checks whether a node (path) is an (optional) member expression.
 * @param {Node|NodePath} node node to check 
 * @returns {boolean}
 */
export function isMemberOrOptExpression(node) {
    return t.isMemberExpression(node) || t.isOptionalMemberExpression(node);
}

/**
 * Finds and returns all continue statements in a loop body
 * @param {NodePath<t.Loop>} loopPath path to a loop statement that MUST have a block body
 * @returns {Array<NodePath<t.ContinueStatement>>} an array of found continue statements; empty if none are found
 */
export function getAllContinues(loopPath) {
    if (!loopPath.has('body') || !loopPath.get('body').has('body')) {
        throw new Error("Can't find continue statements if loop has no block body");
    }

    const statementStack = [].concat(loopPath.get('body').get('body'));
    const continues = [];

    // find all continue statements
    while (statementStack.length > 0) {
        let statementPath = statementStack.pop();

        switch (true) {
            case t.isContinueStatement(statementPath): {
                // save
                continues.push(statementPath);
                break;
            }
            case t.isBlockStatement(statementPath): {
                statementStack.push(...statementPath.get('body'));
                break;
            }
            // skip loops
            // since continue breaks out of the *innermost* loop
            case t.isLoop(statementPath): {
                continue;
            }
            case t.isWithStatement(statementPath):
            case t.isLabeledStatement(statementPath):
            case t.isCatchClause(statementPath):
            case t.isFunctionDeclaration(statementPath): {
                statementStack.push(statementPath.get('body'));
                break;
            }
            case t.isIfStatement(statementPath): {
                statementStack.push(statementPath.get('consequent'));
                if (statementPath.node.alternate) {
                    statementStack.push(statementPath.get('alternate'));
                }
                break;
            }
            case t.isTryStatement(statementPath): {
                statementStack.push(statementPath.get('block'), statementPath.get('finalizer'));
                break;
            }
        }
    }

    return continues;
}

/**
 * Ensures that a path has a block statement as its body.
 * Also copies the source location from the original node to the new block statement.
 * 
 * @param {NodePath} path path to a statement
 */
export function ensureBlock(path) {

    // Generate block statement and copy source location
    let oldNode = path.node;
    let blockNode = path.ensureBlock();
    if(!blockNode.loc)
        blockNode.loc = oldNode.loc;

    // If we generated a return statement, copy location info there as well
    let newBlockStatement;
    if (t.isArrowFunctionExpression(blockNode))
        newBlockStatement = blockNode.body;
    if (t.isBlockStatement(newBlockStatement) && t.isReturnStatement(newBlockStatement.body[0])) {
        newBlockStatement.body[0].loc = oldNode.loc;
    }
}

/**
 * Finds the nearest statement parent of a path and returns it.
 * @param {NodePath} path 
 * @returns {NodePath} the nearest statement parent
 */
export function getStatementParent(path) {
    let statementParentPath = path;
    while (statementParentPath.parentPath && !t.isStatement(statementParentPath)) {
        statementParentPath = statementParentPath.parentPath;
    }

    return statementParentPath;
}