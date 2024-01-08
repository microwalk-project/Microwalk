import { mkdir, writeFile, readFile, readdir, stat} from 'node:fs/promises';
import * as path from 'path';
import * as acorn from '/usr/local/lib/node_modules/acorn/dist/acorn.js';

const LIST_OF_FUNCTION_REPORTS_DIR = '../microwalk/List_Of_Functions_In_Lib';

const cliArgs = process.argv.slice(2);
const FUNCTIONS_DIR_OR_FILE = cliArgs[0];

class Visitor {
  visitNodes(nodes, code, functions) {
    for (const node of nodes) {
      this.visitNode(node, code, functions);
    }
  }

  visitNode(node, code, functions) {
    //console.log(node);
    switch (node.type) {
      case 'Program':
        return this.visitProgram(node, code, functions);

      case 'ExpressionStatement':
        return this.visitExpressionStatement(node, code, functions);

      case 'AssignmentExpression':
        return this.visitAssignmentExpression(node, code, functions);

      case 'FunctionExpression':
        return this.visitFunctionExpression(node, code, functions);

      case 'FunctionDeclaration':
        return this.visitFunctionDeclaration(node, code, functions);

      case 'CallExpression':
        return this.visitCallExpression(node, code, functions);

      case 'BlockStatement':
        return this.visitBlockStatement(node, code, functions);

      case 'VariableDeclaration':
        return this.visitVariableDeclaration(node, code, functions);

      case 'VariableDeclarator':
        return this.visitVariableDeclarator(node, code, functions);

      case 'ArrowFunctionExpression':
        return this.visitArrowFunctionExpression(node, code, functions);

      case 'Identifier':
        return this.visitIdentifier(node, code, functions);

      case 'Literal':
        return this.visitLiteral(node, code, functions);

      case 'Property':
        return this.visitProperty(node, code, functions);
    
      case 'IfStatement':
        return this.visitIfStatement(node, code, functions);
      
      case 'ReturnStatement':
        return this.visitReturnStatement(node, code, functions);
      
      case 'NewExpression':
        return this.visitNewExpression(node, code, functions);
      
      case 'UnaryExpression':
        return this.visitUnaryExpression(node, code, functions);

      case 'WhileStatement':
        return this.visitWhileStatement(node, code, functions);

      case 'DoWhileStatement':
        return this.visitDoWhileStatement(node, code, functions);
      
      case 'ForStatement':
        return this.visitForStatement(node, code, functions);
      
      case 'ForInStatement':
        return this.visitForInStatement(node, code, functions);

      case 'ForOfStatement':
        return this.visitForOfStatement(node, code, functions);

      case 'LabeledStatement':
        return this.visitLabeledStatement(node, code, functions);
      
      case 'BreakStatement':
        return this.visitBreakStatement(node, code, functions);

      case 'ContinueStatement':
        return this.visitContinueStatement(node, code, functions);

      case 'SwitchStatement':
        return this.visitSwitchStatement(node, code, functions);
      
      case 'SwitchCase':
        return this.visitSwitchCase(node, code, functions);
      
      case 'ThrowStatement':
        return this.visitThrowStatement(node, code, functions);
      
      case 'ObjectExpression':
        return this.visitObjectExpression(node, code, functions);

      case 'TryStatement':
        return this.visitTryStatement(node, code, functions);

      case 'CatchClause':
        return this.visitCatchClause(node, code, functions);

      case 'ConditionalExpression':
        return this.visitConditionalExpression(node, code, functions);

      case 'ClassDeclaration':
        return this.visitClassDeclaration(node, code, functions);

      case 'ClassBody':
        return this.visitClassBody(node, code, functions);

      case 'MethodDefinition':
        return this.visitMethodDefinition(node, code, functions);

      case 'PropertyDefinition':
        return this.visitPropertyDefinition(node, code, functions);

      case 'StaticBlock':
        return this.visitStaticBlock(node, code, functions);

      case 'YieldExpression':
        return this.visitYieldExpression(node, code, functions);

      case 'AwaitExpression':
        return this.visitAwaitExpression(node, code, functions);
      
      case 'MemberExpression':
        return this.visitMemberExpression(node, code, functions);
      
      case 'LogicalExpression':
        return this.visitLogicalExpression(node, code, functions);
      
      case 'SequenceExpression':
        return this.visitSequenceExpression(node, code, functions);

      case 'BinaryExpression':
        return this.visitBinaryExpression(node, code, functions);

      case 'UnaryExpression':
        return this.visitUnaryExpression(node, code, functions);

      case 'ArrayExpression':
        return this.visitArrayExpression(node, code, functions);
    }
  }

  visitProgram(node, code, functions) {
    return this.visitNodes(node.body, code, functions);
  }

  visitExpressionStatement(node, code, functions) {
    if (node.expression.type === 'AssignmentExpression') {
      if (node.expression.right.type === 'FunctionExpression' || node.expression.right.type === 'ArrowFunctionExpression') {
        if (node.expression.left.id) {
          const functionName = node.expression.left.id.name;
          let functionBody = "";
          const beginnen = node.start;
          const enden = node.end;
          for (let i = 0; i < (enden - beginnen); i++) {
            functionBody += code[beginnen + i];
          }
          functions.push({"name":functionName, "body":functionBody});

        } else {
          let functionName = "";
          let functionBody = "";
          const beginnen = node.start;
          const enden = node.end;
          for (let i = 0; i < (enden - beginnen); i++) {
            if (functionName.slice(-1) !== '=') {
              functionName += code[beginnen + i];
            }
            functionBody += code[beginnen + i];
          }
          functionName = functionName.slice(0, -1);
          if (functionName.slice(-1) === ' ') {
            functionName = functionName.slice(0, -1);
          }
          functions.push({"name":functionName, "body":functionBody});

        }
      }
    }
    return this.visitNode(node.expression, code, functions);
  }

  visitAssignmentExpression(node, code, functions) {
    return this.visitNode(node.right, code, functions);
  }

  visitFunctionExpression(node, code, functions) {
    if (node.id) {
      const functionName = node.id.name;
      let functionBody = "";
      const beginnen = node.start;
      const enden = node.end;
      for (let i = 0; i < (enden - beginnen); i++) {
        functionBody += code[beginnen + i];
      }
      functions.push({"name":functionName, "body":functionBody});
      if (node.body.type === 'BlockStatement') {
        return this.visitNode(node.body, code, functions);
      }
    } else {
      if (node.body.type !== 'BlockStatement') {
        let functionName = "";
        let functionBody = "";
        const beginnen = node.start;
        const enden = node.end;
        for (let i = 0; i < (enden - beginnen); i++) {
          if (functionName.slice(-1) !== '=') {
            functionName += code[beginnen + i];
          }
          functionBody += code[beginnen + i];
        }
        functionName = functionName.slice(0, -1);
        if (functionName.slice(-1) === ' ') {
          functionName = functionName.slice(0, -1);
        }

        functions.push({"name":functionName, "body":functionBody});

      } else {
        return this.visitNode(node.body, code, functions);
      }
    }
  }

  visitFunctionDeclaration(node, code, functions) {
    const functionName = node.id.name;
    let functionBody = "";
    const beginnen = node.start;
    const enden = node.end;
    for (let i = 0; i < (enden - beginnen); i++) {
      functionBody += code[beginnen + i];
    }
    functions.push({"name":functionName, "body":functionBody});
    return this.visitNode(node.body, code, functions);
  }

  visitCallExpression(node, code, functions) {
    if (node.callee) {
      if (node.arguments[1] && ((node.arguments[1].type === 'FunctionExpression') || (node.arguments[1].type === 'ArrowFunctionExpression')) ) {
        const functionName = node.arguments[0].value;
        let functionBody = "";
        const beginnen = node.arguments[1].start;
        const enden = node.arguments[1].end;
        for (let i = 0; i < (enden - beginnen); i++) {
          functionBody += code[beginnen + i];
        }
        functions.push({"name":functionName, "body":functionBody});
        this.visitNode(node.arguments[1].body, code, functions);
      }
      this.visitNode(node.callee, code, functions);
    }
    if (node.arguments) {
      this.visitNodes(node.arguments, code, functions);
    }
  }

  visitBlockStatement(node, code, functions) {
    return this.visitNodes(node.body, code, functions);
  }

  visitVariableDeclaration(node, code, functions) {
    return this.visitNodes(node.declarations, code, functions);

  }

  visitVariableDeclarator(node, code, functions) {
    if (node.id) {
      if (node.init) {
        if (node.init.type === 'FunctionExpression' || node.init.type === 'ArrowFunctionExpression') {
          const functionName = node.id.name;
          let functionBody = "";
          const beginnen = node.start;
          const enden = node.end;
          for (let i = 0; i < (enden - beginnen); i++) {
            functionBody += code[beginnen + i];
          }
          functions.push({"name":functionName, "body":functionBody});

        } else if (node.init.type === 'CallExpression') {
          if (node.init.callee.type === 'FunctionExpression' || node.init.callee.type === 'ArrowFunctionExpression') {
            const functionName = node.id.name;
            let functionBody = "";
            const beginnen = node.start;
            const enden = node.end;
            for (let i = 0; i < (enden - beginnen); i++) {
              functionBody += code[beginnen + i];
            }
          functions.push({"name":functionName, "body":functionBody});

          }
        } else if (node.init.type === 'ObjectExpression') {
          this.visitNodes(node.init.properties, code, functions)

        } else if (node.init.type === 'NewExpression' && (node.init.callee.name && node.init.callee.name === 'Function')) {
          const functionName = node.id.name;
          let functionBody = "";
          const beginnen = node.init.start;
          const enden = node.init.end;
          for (let i = 0; i < (enden - beginnen); i++) {
            functionBody += code[beginnen + i];
          }
          functions.push({"name":functionName, "body":functionBody});
        } else if (node.init.type === 'ConditionalExpression') {
          if (node.init.alternate && (node.init.alternate.type === 'FunctionExpression' || node.init.alternate.type === 'ArrowFunctionExpression')) {
            const functionName = node.id.name;
            let functionBody = "";
            const beginnen = node.init.alternate.start;
            const enden = node.init.alternate.end;
            for (let i = 0; i < (enden - beginnen); i++) {
              functionBody += code[beginnen + i];
            }
            functions.push({"name":functionName, "body":functionBody});
          }
          if (node.init.consequent && (node.init.consequent.type === 'FunctionExpression' || node.init.consequent.type === 'ArrowFunctionExpression')) {
            const functionName = node.id.name;
            let functionBody = "";
            const beginnen = node.init.consequent.start;
            const enden = node.init.consequent.end;
            for (let i = 0; i < (enden - beginnen); i++) {
              functionBody += code[beginnen + i];
            }
            functions.push({"name":functionName, "body":functionBody});
          }
        }
        return this.visitNode(node.init, code, functions);
      }
    } else {
      if (node.init) {
        if (node.init.type === 'FunctionExpression' || node.init.type === 'ArrowFunctionExpression') {
          let functionName = "";
          let functionBody = "";
          const beginnen = node.start;
          const enden = node.end;
          for (let i = 0; i < (enden - beginnen); i++) {
            if (functionName.slice(-1) !== '=') {
              functionName += code[beginnen + i];
            }
            functionBody += code[beginnen + i];
          }
          functionName = functionName.slice(0, -1);
          if (functionName.slice(-1) === ' ') {
            functionName = functionName.slice(0, -1);
          }

          functions.push({"name":functionName, "body":functionBody});

        } else if (node.init.type === 'CallExpression') {
          if (node.init.callee.type === 'FunctionExpression' || node.init.callee.type === 'ArrowFunctionExpression') {
            let functionName = "";
            let functionBody = "";
            const beginnen = node.start;
            const enden = node.end;
            for (let i = 0; i < (enden - beginnen); i++) {
              if (functionName.slice(-1) !== '=') {
                functionName += code[beginnen + i];
              }
              functionBody += code[beginnen + i];
            }
            functionName = functionName.slice(0, -1);
            if (functionName.slice(-1) === ' ') {
              functionName = functionName.slice(0, -1);
            }

            functions.push({"name":functionName, "body":functionBody});
          }
        }
        return this.visitNode(node.init, code, functions);
      }
    }
  }

  visitArrowFunctionExpression(node, code, functions) {
    if (node.id) {
      const functionName = node.id.name;
      let functionBody = "";
      const beginnen = node.start;
      const enden = node.end;
      for (let i = 0; i < (enden - beginnen); i++) {
        functionBody += code[beginnen + i];
      }
      functions.push({"name":functionName, "body":functionBody});
    }
  }

  visitIdentifier(node, code, functions) {
    return node.name;
  }

  visitLiteral(node, code, functions) {
    return node.value;
  }

  visitProperty(node, code, functions) {
    if (node.key) {
      if (node.value.type === 'FunctionExpression' || node.value.type === 'ArrowFunctionExpression') {
        const functionName = node.key.name;
        let functionBody = "";
        const beginnen = node.value.start;
        const enden = node.value.end;
        for (let i = 0; i < (enden - beginnen); i++) {
          functionBody += code[beginnen + i];
        }
        functions.push({"name":functionName, "body":functionBody});
        if (node.value.body) {
          return this.visitNode(node.value.body, code, functions);
        }
        return this.visitNode(node.value, code, functions);

      } else {
        if (node.value.body) {
          return this.visitNode(node.value.body, code, functions);
        }
        return this.visitNode(node.value, code, functions);
      }

    } else if (node.value) {
      visitFunctionExpression(node, code, functions);
    }

  }

  visitIfStatement(node, code, functions) {
    if (node.alternate) {
      this.visitNode(node.alternate, code, functions);
    }
    return this.visitNode(node.consequent, code, functions);
  }

  visitReturnStatement(node, code, functions) {
    if (node.argument) {
      return this.visitNode(node.argument, code, functions);
    }
  }

  visitNewExpression(node, code, functions) {
    if (node.arguments) {
      this.visitNodes(node.arguments, code, functions);
    }
    return this.visitNode(node.callee, code, functions);
  }

  visitUnaryExpression(node, code, functions) {
    return this.visitNode(node.argument, code, functions);
  }

  visitWhileStatement(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitDoWhileStatement(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitForStatement(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitForInStatement(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitForOfStatement(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitLabeledStatement(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitBreakStatement(node, code, functions) {
    if (node.label !== null) {
      return this.visitNode(node.label, code, functions);
    }
  }

  visitContinueStatement(node, code, functions) {
    if (node.label !== null) {
      return this.visitNode(node.label, code, functions);
    }
  }

  visitSwitchStatement(node, code, functions){
    return this.visitNodes(node.cases, code, functions);
  }

  visitSwitchCase(node, code, functions) {
    return this.visitNodes(node.consequent, code, functions);
  }

  visitThrowStatement(node, code, functions) {
    return this.visitNode(node.argument, code, functions);
  }

  visitObjectExpression(node, code, functions) {
    return this.visitNodes(node.properties, code, functions);
  }

  visitTryStatement(node, code, functions) {
    if (node.finalizer) {
      this.visitNode(node.finalizer, code, functions);
    }
    if (node.handler) {
      this.visitNode(node.handler, code, functions);
    }
    return this.visitNode(node.block, code, functions);
  }

  visitCatchClause(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitConditionalExpression(node, code, functions) {
    if (node.alternate) {
      this.visitNode(node.alternate, code, functions);
    }
    return this.visitNode(node.consequent, code, functions);
  }

  visitClassDeclaration(node, code, functions) {
    return this.visitNode(node.body, code, functions);
  }

  visitClassBody(node, code, functions){
    return this.visitNodes(node.body, code, functions);
  }

  visitMethodDefinition(node, code, functions){
    if (node.key && node.value) {
      if (node.value.type === 'FunctionExpression' || node.value.type === 'ArrowFunctionExpression') {
        const functionName = node.key.name;
        let functionBody = "";
        const beginnen = node.value.start;
        const enden = node.value.end;
        for (let i = 0; i < (enden - beginnen); i++) {
          functionBody += code[beginnen + i];
        }
        functions.push({"name":functionName, "body":functionBody});
        return this.visitNode(node.value.body, code, functions);
      } 
    } 
    return this.visitNode(node.value, code, functions);
  }

  visitPropertyDefinition(node, code, functions){
    if (node.key && node.value) {
      if (node.value.type === 'FunctionExpression' || node.value.type === 'ArrowFunctionExpression') {
        const functionName = node.key.name;
        let functionBody = "";
        const beginnen = node.value.start;
        const enden = node.value.end;
        for (let i = 0; i < (enden - beginnen); i++) {
          functionBody += code[beginnen + i];
        }
        functions.push({"name":functionName, "body":functionBody});
        return this.visitNode(node.value.body, code, functions);
      }
    }
    return this.visitNode(node.value, code, functions);
  }

  visitStaticBlock(node, code, functions){
    return this.visitNodes(node.body, code, functions);
  }

  visitYieldExpression(node, code, functions) {
    return this.visitNode(node.argument, code, functions);
  }

  visitAwaitExpression(node, code, functions) {
    return this.visitNode(node.argument, code, functions);
  }

  visitMemberExpression(node, code, functions) {
    if (node.object) {
      return this.visitNode(node.object, code, functions);
    }
  }

  visitLogicalExpression(node, code, functions) {
    if (node.left) {
      this.visitNode(node.left, code, functions);
    }
    if (node.right) {
      this.visitNode(node.right, code, functions);
    }
  }

  visitSequenceExpression(node, code, functions) {
    if (node.expressions) {
      return this.visitNodes(node.expressions, code, functions);
    }
  }

  visitBinaryExpression(node, code, functions) {
    if (node.left) {
      this.visitNode(node.left, code, functions);
    }
    if (node.right) {
      this.visitNode(node.right, code, functions);
    }
  }

  visitUnaryExpression(node, code, functions) {
    if (node.argument) {
      return this.visitNode(node.argument, code, functions);
    }
  }

  visitArrayExpression(node, code, functions) {
    if (node.elements) {
      return this.visitNodes(node.elements, code, functions);
    }
  }
}

async function processDirectory(directoryOrFileHandle) {
  try {
      const entries = await readdir(directoryOrFileHandle, { withFileTypes: true });
      for await (const entry of entries) {
          if (entry.isDirectory()) {
              await processDirectory(path.join('./', entry.path, entry.name));
          } else {
              async function test(entry) {
                  try {
                    const filePath = path.join(directoryOrFileHandle, entry.name);
                    if (path.extname(filePath) === '.js') {
                      const code = await readFile(filePath, { encoding: 'utf8' });
                      var ast = acorn.parse(code, { ecmaVersion: 'latest' });
                      //  Create a Visitor object and use it to traverse the AST
                      var visitor = new Visitor();
                      var functions = [];
                      visitor.visitNode(ast, code, functions);
                  
                      await mkdir(LIST_OF_FUNCTION_REPORTS_DIR, { recursive: true });
                      const filePathFinal = filePath.replaceAll('../','').replaceAll('./','').replaceAll('/','_').replaceAll('.','_').replace(/_js$/,'.js');
                      const reportFilePath = path.join(`.`,`${LIST_OF_FUNCTION_REPORTS_DIR}`,`${filePathFinal}.json`);
                      await writeFile(reportFilePath, JSON.stringify(functions));
                      console.log(`JSON list of functions ${reportFilePath} has been saved!`);
                    }
                  } catch (err) {
                    console.log(err);
               
                  }
                };
              
                test(entry);
          }
      }
  } catch (err) {
    const code = await readFile(directoryOrFileHandle, { encoding: 'utf8' });
    console.log("Name of the file: " + directoryOrFileHandle + "\n");

    async function test(code) {
      try {
          var ast = acorn.parse(code, { ecmaVersion: 'latest' });
          //  Create a Visitor object and use it to traverse the AST
          var visitor = new Visitor();
          var functions = [];
          visitor.visitNode(ast, code, functions);

          await mkdir(LIST_OF_FUNCTION_REPORTS_DIR, { recursive: true });
          const filePathFinal = FUNCTIONS_DIR_OR_FILE.replaceAll('../','').replaceAll('./','').replaceAll('/','_').replaceAll('.','_').replace(/_js$/,'.js');
          const reportFilePath = path.join(`.`,`${LIST_OF_FUNCTION_REPORTS_DIR}`,`${filePathFinal}.json`);
          await writeFile(reportFilePath, JSON.stringify(functions));

          console.log(`JSON list of functions ${reportFilePath} has been saved!`);
      } catch (err) {
          console.log(err);
      }
    };
    test(code);
  }
}

processDirectory(FUNCTIONS_DIR_OR_FILE)
.then()
.catch((error) => {
    console.log(`${FUNCTIONS_DIR_OR_FILE} is neither a directory nor a file.\n`);
});
