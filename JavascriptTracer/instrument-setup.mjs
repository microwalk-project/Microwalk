/**
 *  This file contains a setup pass that transforms the AST before it gets instrumented.
 */

import * as t from "@babel/types";
import * as util from "./instrument-utility.mjs";
import * as constants from "./constants.cjs";
import template from "@babel/template";

// counter that gets incremented with each switch statement
// since each switch statement gets their own UNIQUE labeled block
let switchctr = 0; 
// counter that gets incremented for each chain split
// since each split gets a UNIQUE array
let chainctr = 0;
// counter for each call split
let callctr = 0;

/**
 * Splits a given member expression chain (>1 member expression) into multiple equivalent expressions and assigns the final result
 * to a variable, which then replaces the member expression chain
 * @param {NodePath} path NodePath of a member expression chain 
 * @param {boolean} [genExpressions=true]  whether to generate a sequence expression of expressions or a block statement of statements
 * @returns 
 */
function explodeMemberExpression(path, genExpressions=true) {
    const stack = [];
    let currentNode = path.node, memberExpressionCount = 0;

    // traverse to base of member expression (from right to left)
    while (util.isMemberOrOptExpression(currentNode) || util.isCallOrOptExpression(currentNode)) {
        stack.push(currentNode);
        switch (true) {
            case t.isOptionalMemberExpression(currentNode):
            case t.isMemberExpression(currentNode):
                // update to next node
                currentNode = currentNode.object;
                memberExpressionCount++;

                break;

            case t.isOptionalCallExpression(currentNode):
            case t.isCallExpression(currentNode):
                // update to next node
                currentNode = currentNode.callee;
                break;
        }
    }

    // only continue if it's a chain (more than 1 member expression)
    if (memberExpressionCount <= 1) {
        return;
    } 

    const baseNumber = chainctr++;
    const varBaseString = `${constants.INSTR_VAR_PREFIX}tmp${baseNumber}`;


    const varDec = template.default.ast(`
        let ${constants.CHAINVARNAME}${baseNumber};
    `);
    // use location info of nearest statement
    const varDecLoc = util.getStatementParent(path).node.loc;
    varDec.loc = varDecLoc;
    const declarationsArray = varDec.declarations;
    // insert declarations directly prior to node
    const statParent = util.getStatementParent(path);
    statParent.insertBefore(varDec);

    /**
     * 
     * @param {t.Node} node 
     * @param {number} splitNr 
     * @returns {t.AssignmentExpression}
     */
    const genMemberSplit = function(node, splitNr) {
        // ensure correct node type
        if (!util.isMemberOrOptExpression(node)) {
            throw new Error(`Expected MemberExpression node for splitting; instead got ${node.type}`);
        }

        const oldObjLoc = node.object.loc;
        // change passed node to use previous var as base
        node.object = t.identifier(`${varBaseString}_${splitNr-1}`);
        node.object.loc = oldObjLoc;

        // temp var that stores evaluation
        const tmpId = t.identifier(`${varBaseString}_${splitNr}`);
        tmpId.loc = node.loc;
        // add to declarations
        declarationsArray.push(t.variableDeclarator(tmpId));

        const rhs = t.cloneNode(node, true, false);
        const assigment = template.default(`%%lhs%% = %%rhs%%;`)({lhs: tmpId, rhs}).expression;

        // mark helper nodes as logged
        assigment.writeLogged = true;
        tmpId.writeLogged = true;
        tmpId.readLogged = true;

        return assigment;
    }

    const genCallSplit = function(node, splitNr) {
        // ensure correct node type
        if (!util.isCallOrOptExpression(node)) {
            throw new Error(`Expected CallExpression node for splitting; instead got ${node.type}`);
        }

        // tmp var that stores evaluation
        const tmpId = t.identifier(`${varBaseString}_${splitNr}`);
        tmpId.loc = node.loc;
        // add to declarations
        declarationsArray.push(t.variableDeclarator(tmpId));

        // callee id is previous tmp var
        const calleeId = t.identifier(`${varBaseString}_${splitNr - 1}`);
        let callee;
        if (t.isOptionalCallExpression(node)) {
            callee = template.default(`%%calleeId%%?.call`)({calleeId}).expression;
        } else {
            callee = template.default(`%%calleeId%%.call`)({calleeId}).expression;
        }

        // "this" id is two prior
        if (splitNr - 2 < 0) {
            throw new Error(`Illegal call in chain split. Can't reference negative tmp var.`);
        }
        const thisId = t.identifier(`${varBaseString}_${splitNr - 2}`);
        thisId.loc = sequenceArr.at(-1).right.loc;

        // update callee in node
        node.callee = callee;
        // prepend "this" to args
        node.arguments.unshift(thisId);

        const rhs = t.cloneNode(node, true, false);
        const assigment = template.default(`%%lhs%% = %%rhs%%;`)({lhs: tmpId, rhs}).expression;

        // mark helper nodes as logged
        assigment.writeLogged = true;
        tmpId.writeLogged = true;
        tmpId.readLogged = true;
        thisId.readLogged = true;
        rhs.squashed = true;

        return assigment;
    }

    // current node is base object -> tmp0
    // tmp var for evaluation result
    const tmp0Id = t.identifier(`${varBaseString}_0`);
    // add to declarations
    declarationsArray.push(t.variableDeclarator(tmp0Id));

    const firstAssigment = template.default(`%%tmpId%% = %%rhs%%;`)({tmpId: tmp0Id, rhs: currentNode}).expression;


    // this is the array for the sequence expression that will contain ALL temp results etc.
    const sequenceArr = [firstAssigment];

    // mark helper nodes as logged
    firstAssigment.writeLogged = true;
    tmp0Id.writeLogged = true;
    tmp0Id.readLogged = true;
    

    // create temp variables (from left to right of member expression)
    let splitNr = sequenceArr.length;
    while (stack.length > 2) {
        // get top node from stack
        currentNode = stack.pop();

        switch (true) {
            case t.isMemberExpression(currentNode):
            case t.isOptionalMemberExpression(currentNode): {
                // generate and save assignment expression
                const assign = genMemberSplit(currentNode, splitNr++);
                sequenceArr.push(assign);

                break;
            }           
            // wrap the last var into (optional) call expression
            case t.isCallExpression(currentNode): 
            case t.isOptionalCallExpression(currentNode): {
                // generate and save assignment
                const assign = genCallSplit(currentNode, splitNr++);
                // also add call result var to sequence
                sequenceArr.push(assign, assign.left);

                break;
            }
        }
    }

    // second to last split is assigned to chain var
    currentNode = stack.pop();

    const finalVarId = t.identifier(`${constants.CHAINVARNAME}${baseNumber}`);
    let finalAssignment
    const finalLoc = currentNode.loc;
    finalVarId.loc = finalLoc;
    
    if (util.isMemberOrOptExpression(currentNode)) {
        finalAssignment = genMemberSplit(currentNode, splitNr++);
    } else if (util.isCallOrOptExpression(currentNode)) {
        finalAssignment = genCallSplit(currentNode, splitNr++);
    }
    else {
        throw new Error(`Unexpected node type ${currentNode.type} encountered during member expression split.`);
    }

    // replace lhs since
    //    tmpX = tmpX-1.prop
    // was generated
    finalAssignment.left = finalVarId;

    // mark as logged
    finalAssignment.writeLogged = true;
    finalVarId.readLogged = true;

    sequenceArr.push(finalAssignment);
    if (util.isCallOrOptExpression(currentNode)) {
        sequenceArr.push(finalAssignment.left);
    }

    // final member expression is retained
    currentNode = stack.pop();
    const finalEx = t.cloneNode(currentNode, true, false);
    // replace object with chain id
    finalEx.object = finalVarId;
    // copy location 
    finalEx.loc = finalLoc;

    // use sequence expression
    if (genExpressions) {
        sequenceArr.push(finalEx);

        // create sequence expression
        const seqEx = t.sequenceExpression(sequenceArr);
        seqEx.loc = path.node.loc;

        // use function method for call if parent is call expression
        if (util.isCallOrOptExpression(path.parent) && path.listKey !== 'arguments') {
            const callParent = path.parent;

            const callVarId = t.identifier(`${constants.CALLVARNAME}${callctr++}`);
            callVarId.loc = callParent.loc;
            declarationsArray.push(t.variableDeclarator(callVarId));

            let callee;
            if (t.isOptionalCallExpression(callParent)) {
                callee = template.default(`%%seq%%?.call;`)({
                    seq: seqEx
                }).expression;
            }
            else {
                callee = template.default(`%%seq%%.call;`)({ 
                    seq: seqEx
                }).expression;
            }

            const callAssignEx = template.default(`
            %%callVarId%% = %%callee%%(%%thisVal%%, %%args%%);
            `)({
                callVarId,
                callee:     callee,
                thisVal:    finalVarId,
                args:       callParent.arguments
            }).expression;

            // copy location 
            callAssignEx.right.loc = callParent.loc;
            callAssignEx.loc = callParent.loc;

            // mark helper nodes as logged
            callAssignEx.right.squashed = true;
            callVarId.readLogged = true;
            callVarId.writeLogged = true;

            const callSeq = t.sequenceExpression([callAssignEx, callVarId]);
            callSeq.loc = callParent.loc;

            // replace call parent with sequence expression 
            // (vCall = (chain...).call(), vCall)
            path.parentPath.replaceWith(callSeq);
        } 
        else {
            // simply replace chain with sequence expression
            path.replaceWith(seqEx);
        }
    }
    // create a block statement
    else {
        // create block with statements instead of expressions
        const block = t.blockStatement(sequenceArr.map((val) => t.expressionStatement(val)));
        block.loc = path.node.loc;

        // insert block before node
        util.getStatementParent(path).insertBefore(block);

        path.replaceWith(finalEx);
    }

    return;
}

/**
 * Squashes call expressions so that every call expression is assigned to a variable with an identifier as the callee.
 * example: 
    before: a()()() 
    after:
        call0_0 = a(), 
        call0_1 = call0_0(), 
        call0_2 = call0_1(),
        call0_2

    before: a.b(1,2,3)
    after:
          this1 = a,
        call1_0 = this1.b,
        call1_1 = call1_0.call(this1, 1, 2, 3),
        call1_1

 * @param {NodePath} path Path of the call expression
 * @returns
 */
function squashCallees(path) {
    // sanity checks
    if (path.node.squashed || path.node.ignore) {
        // path has already been squashed
        return;
    }
    
    let callee = path.node.callee;
    
    if (!util.isCallOrOptExpression(callee) && !util.isMemberOrOptExpression(callee)) {
        callee = path.node;
    }

    if (!util.isCallOrOptExpression(path.node)) {
        // path isn't a call expression or callee isn't applicable
        return;
    }
    
    const baseNumber = callctr++;
    const varBaseString = `${constants.CALLVARNAME}${baseNumber}`;

    const varDec = template.default.ast(`
        let ${varBaseString}_0;
    `);
    const varDecLoc = util.getStatementParent(path).node.loc;
    varDec.loc = varDecLoc;
    const declarationsArray = varDec.declarations;
    declarationsArray[0].writeLogged = true;
    // insert declarations directly prior to statment of node
    util.getStatementParent(path).insertBefore(varDec);

    // array containing all expressions from call split
    const sequenceArr = [];

    if (util.isMemberOrOptExpression(callee)) {
        
        // save member object as this value of call
        const thisId = t.identifier(`${constants.THISVARNAME}${baseNumber}`);
        thisId.loc = callee.object.loc;
        // add this var to declarations
        declarationsArray.push(t.variableDeclarator(thisId));
        const thisAssign = template.default(`
            %%thisId%% = %%callee%%;
        `)({ thisId, callee: callee.object }).expression;
        // save expression
        sequenceArr.push(thisAssign);

        // assign member expression to call var
        const callVarId = t.identifier(`${varBaseString}_0`);
        callVarId.loc = callee.loc;
        const baseAssign = template.default(`
            %%callVarId%% = %%thisId%%.%%prop%%;
        `)({ callVarId, thisId, prop: callee.property }).expression;
        // save expression
        sequenceArr.push(baseAssign);

        // mark helper nodes as logged
        thisAssign.writeLogged = true;
        baseAssign.writeLogged = true;
        callVarId.readLogged = true;
        callVarId.writeLogged = true;
        thisId.readLogged = true;
        thisId.writeLogged = true;

        // generate call
        // $$call.call
        const callMemberEx = t.memberExpression(callVarId, t.identifier("call"));
        // update callee to $$call.call
        path.node.callee = callMemberEx;
        // add $$this as first argument
        path.node.arguments.unshift(thisId);

        // mark as logged
        path.node.squashed = true;
        path.node.readLogged = true;        
        // callee already gets logged in assignment expression
        callMemberEx.readLogged = true;

        // convert args to array accesses
        // and generate array of corresponding expressions saving the args
        const argsExpArr = convertArgs(path, `${baseNumber}_0`, declarationsArray);
        // add to sequence array
        sequenceArr.push(...argsExpArr);
        
        const callResId = t.identifier(`${varBaseString}_1`);
        callResId.loc = path.node.loc;
        declarationsArray.push(t.variableDeclarator(callResId));

        // assign call result to final var
        const finalAssign = template.default(`
            %%callResId%% = %%call%%;
        `)({ callResId, call: path.node }).expression;

        // mark helpers as logged
        callResId.readLogged = true;
        callResId.writeLogged = true;
        finalAssign.writeLogged = true;


        sequenceArr.push(finalAssign);
        // final expression in sequence is the result of the call
        sequenceArr.push(callResId);

        const seqEx = t.sequenceExpression(sequenceArr);
        seqEx.loc = path.node.loc;

        // replace path with sequence expression
        path.replaceWith(seqEx);

        return;
    } 
    else if (util.isCallOrOptExpression(callee)) {
        let currentCallee = path.get("callee"); 
        const stack = [path];
        let callCtr = 0;

        // save all calls from chain
        while (util.isCallOrOptExpression(currentCallee)) {
            stack.push(currentCallee);
            currentCallee = currentCallee.get("callee");
        }

        // innermost node is the base object (i.e. not a call expression)
        // so we need to wrap that in the first call
        const firstCall = stack.pop();
        firstCall.node.callee = currentCallee.node;
        // generate first assignment expression with the first call
        const firstAssign = template.default(`
                %%callResId%% = %%call%%;
            `)({ 
                callResId: t.identifier(`${varBaseString}_0`), 
                call: firstCall.node
            }).expression;

        // mark as logged
        firstAssign.writeLogged = true;
        firstCall.node.squashed = true;

        // convert args to array accesses
        // and generate array of corresponding expressions saving the args
        const argsExpArr = convertArgs(firstCall, `${baseNumber}_0`, declarationsArray);
        // add to sequence array
        sequenceArr.push(...argsExpArr);

        // add to sequence 
        sequenceArr.push(firstAssign);
        callCtr++;

        // unwind stack into separate calls with each call being of the form 
        // $$call = $$call();
        while (stack.length > 0) {
            currentCallee = stack.pop();
            const currentCallNode = currentCallee.node;

            // update callee to $$call
            currentCallNode.callee = t.identifier(`${varBaseString}_${callCtr - 1}`);

            // identifier for result of call
            const callResId = t.identifier(`${varBaseString}_${callCtr}`);
            callResId.loc = currentCallee.node.loc;
            // add to declarations
            declarationsArray.push(t.variableDeclarator(callResId));

            const assign = template.default(`
                %%callResId%% = %%call%%;
            `)({ callResId, call: currentCallNode }).expression;

            // convert args to array accesses
            // and generate array of corresponding expressions saving the args
            const argsExpArr = convertArgs(currentCallee, `${baseNumber}_${callCtr}`, declarationsArray);
            // add to sequence array
            sequenceArr.push(...argsExpArr);

            // mark helper nodes as logged
            assign.writeLogged = true;
            callResId.writeLogged = true;
            callResId.readLogged = true;
            currentCallNode.squashed = true;

            sequenceArr.push(assign);
            callCtr++;
        } 

        // set final call result as last expression in sequence
        sequenceArr.push(sequenceArr.at(-1).left);

        const seqEx = t.sequenceExpression(sequenceArr);
        seqEx.loc = path.node.loc;

        // replace chain with sequence expression
        path.replaceWith(seqEx);

        return;
    }   
}

/**
 * Alters the ast around a given call expression to save arguments of the call into an array and then
 * pass the array accesses as arguments. Insertion of the generated block statement can be handled by the
 * function or the caller. 
 * @param {NodePath} path path of the call expression whose arguments should be converted
 * @param {NodePath} placeholderPath path of the placeholder node that is then replaced with the arg block
 * @returns  
 */
function convertArgs(path, ctr, declarationsArray) {
    // sanity check
    if (path.node.convertedArgs || path.node.ignore) {
        return t.blockStatement([]);
    }

    const argsId = t.identifier(`${constants.ARGSVARNAME}${ctr}`);
    argsId.writeLogged = true;
    argsId.readLogged = true;
    
    // array that has the member expressions of all new args 
    const newCallArgs = [];

    // save original args
    const originalArgs = path.node.arguments;

    // init local tmp array
    // const args = []
    const varDecor = t.variableDeclarator(argsId, t.arrayExpression([]));
    varDecor.ignore = true;
    declarationsArray.push(varDecor);

    // array to save all arg expressions 
    const argSequenceArr = [];

    // save args into local scoped tmp array $$args
    for (let i = 0; i < originalArgs.length; i++) {
        // skip $$this
        if (t.isIdentifier(originalArgs[i]) && originalArgs[i].name == constants.THISVARNAME) {
            newCallArgs.push(originalArgs[i]);
            continue;
        }

        // member expression: $$args.push (for saving the arg)
        const argsPushMemberEx = t.memberExpression(argsId, t.identifier("push"));
        argsPushMemberEx.readLogged = true;

        // call expression: $$args.unshift() (for arg in call)
        const newArgEx = template.default(`
            %%argsId%%.shift();
        `)({ argsId }).expression;

        newArgEx.ignore = true;
        newCallArgs.push(newArgEx);

        const argPath = path.get(`arguments.${i}`);

        // try to find a member expression in arg
        let argMemExPath = argPath;
        while (t.isCallExpression(argMemExPath)) {
            argMemExPath = argMemExPath.get('callee');
        }

        if (t.isMemberExpression(argMemExPath)) {
            // explode member expression chains if applicable
            explodeMemberExpression(argMemExPath);
        }

        // call expression: $$args.push(originalArgs[i])
        const pushArgEx = t.callExpression(argsPushMemberEx, [originalArgs[i]]);

        // avoid superfluous logging 
        pushArgEx.ignore = true;

        argSequenceArr.push(pushArgEx);
    }

    // replace args in call with array access
    path.node.arguments = [];
    path.pushContainer("arguments", newCallArgs);

    return argSequenceArr;
}

/**
 * Ensures a given if- or switch statement has a block statement for a body
 * @param {NodePath} path path of IfStatement or SwitchStatement  
 * @returns 
 */
function ensureConsequentBlock(path) {
    const consPath = path.get('consequent');
    const consNode = consPath.node;

    // already a block statement?
    if (t.isBlockStatement(consNode))
        return;

    if (t.isIfStatement(path)) {
        // if statements have a singular statement as their consequence
        // wrap that in a block statement
        const block = t.toBlock(consNode, path.node);
        block.loc = consNode.loc;
        // replace
        consPath.replaceWith(block);

        // also ensure alternate is a block
        const altPath = path.get('alternate');
        if (altPath.node) {
            // alternate exists
            const altBlock = t.toBlock(altPath.node, path.node);
            altBlock.loc = altPath.node.loc;
            altPath.replaceWith(altBlock);
        }
    }
    else if (t.isSwitchCase(path)) {
        // consequent is an array of statements
        // add a block statement with all statements as the first item
        path.node.consequent[0] = t.blockStatement(path.node.consequent);
        // remove all other items
        path.node.consequent.splice(1);
    }
}

/**
 * Assigns evaluated computed property values to temp variables so they can be traced.
 * example
 * before:
 *      a[1+1]
 * after:
 *      computed = 1+1
 *      a[computed]
 * @param {NodePath<t.MemberExpression|t.OptionalMemberExpression>} path 
 */
function assignComputedToTemp(path) {
    const node = path.node;

    // ignore static member expressions
    if (!node.computed || node.computeassigned || node.ignore) return;

    // properties that are already identifiers or numeric literals are fine
    if (t.isNumericLiteral(node.property) || t.isStringLiteral(node.property)) return;

    // property is guaranteed to be an expression that is not an identifier
    // create new statement with assignment 
    const computedVar = t.identifier(constants.COMPUTEDVARNAME);
    computedVar.ignore = true;
    // copy loc from expression
    computedVar.loc = node.property.loc;

    const computedAssign = template.default(`
        %%computedVar%% = %%propertyExpression%%;
    `)({
        computedVar,
        propertyExpression: node.property
    }).expression;

    computedAssign.ignore = true;
    computedAssign.loc = node.loc;

    // replace property with assigned var
    path.get('property').replaceWith(computedVar);
    // make property into sequence expression of assignment and var
    // todo: nested computed properties don't get assigned
    // todo: probably add to traversal q instead of using injectAst ?
    path.get('property').insertBefore(computedAssign)

    node.computeassigned = true;
}

export const setupVisitor = {
    "MemberExpression|OptionalMemberExpression"(path) {
        // check parents (until statement parent) for call expression
        let parentPath = path, ignore = false, genExpressions = true;
        while (parentPath && !t.isStatement(parentPath) && !t.isBlockParent(parentPath)) {
            // check the list key (we want to ignore member expressions that are call arguments)
            if (parentPath.listKey == "arguments") {
                ignore = true;
                break;
            }

            // generate statements for lhs of assignments
            // and update expressions
            if (t.isAssignmentExpression(parentPath.parent) && parentPath.key == "left"
                || t.isUpdateExpression(parentPath)) {
                genExpressions = false;
                break;
            }

            parentPath = parentPath.parentPath;
        }


        // don't explode member expressions that are call arguments at this point
        if (!ignore && !path.node.ignore) {
            explodeMemberExpression(path, genExpressions);
        } 
    },

    // we want all functions to have a proper block body
    "Function"(path) {
        util.ensureBlock(path);
    },

    // transform switch statement to if/else statements
    SwitchStatement(path) {
        // wrap everything in a labeled block statement (to ensure break functionality)
        const switchBlock = t.blockStatement([]);
        switchBlock.loc = path.node.loc;
        const switchLabel = t.identifier(`${constants.SWITCHLABELNAME}${switchctr++}`);

        const fallthroughVarId = t.identifier(constants.SWITCHFALLTHROUGHVARNAME);
        fallthroughVarId.readLogged = true;
        fallthroughVarId.writeLogged = true;

        const genAssignFallthroughStat = (bool) => {
            const assignmentExpression = t.assignmentExpression("=", fallthroughVarId, t.booleanLiteral(bool));
            const expressionStatement = t.expressionStatement(assignmentExpression);
            expressionStatement.new = true;
            return expressionStatement;
        }
        // set fallthrough var to false
        switchBlock.body.push(genAssignFallthroughStat(false));

        const switchCases = path.get('cases');
        let currentCase, defaultCase, completeTest;
        const genTestExpression = (test) => {
            // we want each of these to be their own node so we have to clone
            const discriminantExp = t.cloneNode(path.node.discriminant, true, false);
            return t.binaryExpression("===", discriminantExp, test);
        };

        while (switchCases.length > 0) {
            currentCase = switchCases.shift();
            let currentTest = currentCase.node.test;
            let newTest;

            if (!currentTest) {
                // test == null means it's the default case
                defaultCase = currentCase;
            } else {
                newTest = genTestExpression(currentTest);
            }

            // empty consequent means case gets combined with following case
            while(currentCase.node.consequent.length == 0 && switchCases.length > 0) {
                currentCase = switchCases.shift();
                currentTest = currentCase.node.test;
                newTest = currentTest ? 
                            t.logicalExpression("||", newTest, genTestExpression(currentTest)) : 
                            newTest;
            }

            // update complete test (for default case)
            if (newTest) {
                completeTest = completeTest ? t.logicalExpression("||", completeTest, newTest) : newTest;
            }

            const consequentStatStack = [].concat(currentCase.node.consequent);

            // update break statements to break out of labeled block
            while (consequentStatStack.length > 0) {
                let statement = consequentStatStack.pop();

                switch (true) {
                    case t.isBreakStatement(statement): {
                        // update label
                        statement.label = switchLabel;
                        break;
                    }
                    case t.isSwitchCase(statement): {
                        consequentStatStack.push(...statement.consequent);
                        break;
                    }
                    case t.isBlockStatement(statement): {
                        consequentStatStack.push(...statement.body);
                        break;
                    }
                    case t.isWithStatement(statement):
                    case t.isLabeledStatement(statement):
                    case t.isWhileStatement(statement):
                    case t.isDoWhileStatement(statement):
                    case t.isCatchClause(statement):
                    case t.isFunctionDeclaration(statement):
                    case t.isFor(statement): {
                        consequentStatStack.push(statement.body);
                        break;
                    }
                    case t.isIfStatement(statement): {
                        consequentStatStack.push(statement.consequent);
                        if (statement.alternate) {
                            consequentStatStack.push(statement.alternate);
                        }
                        break;
                    }
                    case t.isTryStatement(statement): {
                        consequentStatStack.push(statement.block, statement.finalizer);
                        break;
                    }
                }
            }

            // wrap consequent statement(s) in block since if accepts singular statement as body
            const consequentBlock = t.blockStatement([genAssignFallthroughStat(true)].concat(currentCase.node.consequent));
            // test fallthrough first to short-circuit 
            const finalTest = newTest ? t.logicalExpression("||", fallthroughVarId, newTest) : fallthroughVarId;
            const ifReplacement = t.ifStatement(finalTest, consequentBlock);
            // copy location info
            ifReplacement.loc = currentCase.node.loc;
            if (currentCase.node.consequent.length > 0) {
                consequentBlock.loc = currentCase.node.consequent[0]?.loc;
                consequentBlock.loc.end = currentCase.node.consequent.at(-1)?.loc?.end;
            } // copy location of case if there is no consequence (i.e. empty default case)
            else {
                consequentBlock.loc = currentCase.node.loc;
            }

            if (currentCase == defaultCase) {
                defaultCase = ifReplacement;
            }

            switchBlock.body.push(ifReplacement);
        }

        // update default case test
        if (defaultCase) {
            const defaultTest = t.unaryExpression("!", t.cloneNode(completeTest, true, false), true);
            defaultCase.test = t.logicalExpression("||", defaultTest, fallthroughVarId);
        }

        // replace switch statement with labeled block
        const labeledStatement = t.labeledStatement(switchLabel, switchBlock);
        labeledStatement.loc = path.node.loc;
        path.replaceWith(labeledStatement);
    },

    "IfStatement"(path) {
        ensureConsequentBlock(path);
    },

    // move variable declarations to their own statements
    For(path) {
        // rewrite for expression to equivalent while loop
        const node = path.node;

        // skip for nodes that have already been transformed
        if (node.forhoisted) 
            return;

        switch (true) {
            case (t.isForXStatement(node)): {
                let loopVarKind = t.isVariableDeclaration(node.left) ? node.left.kind : "var";
                let allBindingsExist = true;

                if (!t.isVariableDeclaration(node.left)) {
                    // check whether all bindings exist
                    let [loopVarIds] = util.gatherIdsAndOffsets(node.left);
                    for (let loopVarId of loopVarIds) {
                        let idString = t.isIdentifier(loopVarId) ? loopVarId.name : loopVarId;
                        allBindingsExist &&= path.scope.hasBinding(idString);
                    } 
                }
                
                // only change forX if they have a variable declaration
                // OR it's an implicit declaration bc a binding doesn't exist 
                if (t.isVariableDeclaration(node.left) || !allBindingsExist) {
                    let leftVarDec = path.get('left'), forPath = path;

                    // unwrap destructuring patterns since we only need the vars to exist beforehand
                    let loopIds = [];
                    for (let varDeclarator of leftVarDec.node.declarations ?? [leftVarDec.node]) {
                        [loopIds] = util.gatherIdsAndOffsets(varDeclarator.id ?? varDeclarator);
                    }
                    const declarators = [];
                    for (let id of loopIds) {
                        declarators.push(t.variableDeclarator(id));
                    }

                    // check scope -> var is in the same scope as for, others loop local
                    if (loopVarKind == "var") 
                        path.insertBefore(t.variableDeclaration("var", declarators));
                    else {
                        // loop local -> wrap in block
                        // replace const with let since constants need to be assigned values upon declaration
                        const blockWrapper = t.blockStatement([t.variableDeclaration("let", declarators), path.node]);
                        path.replaceWith(blockWrapper);
                        forPath = path.get('body')[1];
                    }
                    forPath.node.forhoisted = true;

                    // replace variable declaration with init value
                    if (t.isVariableDeclaration(leftVarDec))
                        leftVarDec.replaceWith(leftVarDec.node.declarations.at(-1).id);
                }

                break;
            }
            case t.isForStatement(node): {
                const statements = [];
                // extract components
                const init = node.init;
                const test = node.test;
                const update = node.update;

                let initStatement; 
                if (init) {
                    initStatement = t.isStatement(init) ? init : t.expressionStatement(init);
                    initStatement.loc = init.loc;
                }
                let testExpr = test ?? t.booleanLiteral(true);
                testExpr.loc = test?.loc;
                let updateStatement = update ? t.expressionStatement(update) : null;

                util.ensureBlock(path);

                // find all continue statements
                const continues = util.getAllContinues(path);
                
                // add update statement to end of body  
                if (updateStatement) {
                    updateStatement.loc = update.loc;
                    node.body.body.push(updateStatement);
                    // and before any continue statements
                    for (let statement of continues) {
                        const freshClone = t.cloneNode(updateStatement, true, false);
                        freshClone.loc = update.loc;
                        statement.insertBefore(freshClone);
                    }
                }

                // init is first statement of block
                if (initStatement) statements.push(initStatement);
                // create while with test and body
                const whileStatement = t.whileStatement(testExpr, node.body);
                // while has loc of for node
                whileStatement.loc = node.loc;
                // ensure body has a location
                if (!whileStatement.body.loc) {
                    const loc = node.loc;
                    loc.start = whileStatement.body.body.at(0).loc.start;
                    // second to last item since the last item is our injected update statement
                    loc.end = whileStatement.body.body.at(-2).loc.end;

                    whileStatement.body.loc = loc;
                }
                
                // add while loop as second statement
                statements.push(whileStatement);

                // replace for with block containing init and while loop
                path.replaceWith(t.blockStatement(statements));
            }
        }
    },
    "DoWhileStatement"(path) {
        const statements = [];
        const node = path.node;

        // copy body statements
        if (t.isBlockStatement(node.body)) {
            const bodyClone = node.body.body.map(n => t.cloneNode(n, true, false));
            statements.push(...bodyClone);
        } else {
            statements.push(t.cloneNode(node.body, true, false));
        }

        // create a while statement with the same test and body
        const newWhile = t.whileStatement(node.test, node.body);
        newWhile.loc = node.loc;
        statements.push(newWhile);

        // replace with a block statement that has the entire body and then the new while
        path.replaceWith(t.blockStatement(statements));
    },

    // ensure while has block body
    Loop(path) {
        util.ensureBlock(path);
    }
};

export const setupCallExpressionsVisitor = {
    "CallExpression|OptionalCallExpression"(path) {

        if (!path.node.squashed && !path.node.ignore)
            squashCallees(path);
    },

    "MemberExpression|OptionalMemberExpression"(path) {
        if (path.node.computed) {
            assignComputedToTemp(path);
        }
    }
};