///////////////////////////////////////////////////////////////////////////////
/////////////////////////////// Import needed /////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

import { open, mkdir, writeFile, readFile, readdir } from 'node:fs/promises';
import * as path from 'path'
import { Buffer } from 'node:buffer';

///////////////////////////////////////////////////////////////////////////////
/////////////////////// Paths and Directories /////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

const CT_REPORTS_DIR = '../microwalk/ct_reports';
const jsonReportPath = '../microwalk/results/report.json';
const coverageDir = '../microwalk/coverage';
const sourceCodeDir = '../';

///////////////////////////////////////////////////////////////////////////////
/////////////////////////////// REGEX /////////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

const TARGET_NAME_REGEX = /\(target-(?<name>[^)]+)\)/;
const LEAKAGE_SCORE_REGEX = /leakage\sscore\s(?<number>(?:\d{1,3}\.)\d{1,3})\045/;

///////////////////////////////////////////////////////////////////////////////
//////////////////////// Measure against XSS //////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

const HTML_ESCAPE_CHARS = {
	34: '&quot;', // "
	38: '&amp;',  // &
	39: '&#39;',  // '
	60: '&lt;',   // <
	62: '&gt;',   // >
};

function escapeHtml(string) {
	let html = '';
	for (const char of string) {
		const escapeChar = HTML_ESCAPE_CHARS[char.charCodeAt(0)];
		if (escapeChar) {
			html += escapeChar;
		} else {
			html += char;
		}
	}
	return html;
}

///////////////////////////////////////////////////////////////////////////////
//////////////////////// Leakage severity /////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

const SEVERITY_STYLES = {
	critical: { severityClass: 'issue-critical', icon: '💥' },
	major: { severityClass: 'issue-major', icon: '🚨' },
	minor: { severityClass: 'issue-minor', icon: '⚠' },
};

///////////////////////////////////////////////////////////////////////////////
///////////////////// HTML/CSS & JS script ////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////


const HTML_BEGIN0 = `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <title>CQR visualizer</title>
    <meta name="viewport" content="width=device-width,initial-scale=1">
	<meta http-equiv="content-security-policy" content="script-src 'unsafe-inline'">
	<style>
		html, body, div, span, applet, object, iframe,
		h1, h2, h3, h4, h5, h6, p, blockquote, pre,
		a, abbr, acronym, address, big, cite, code,
		del, dfn, em, img, ins, kbd, q, s, samp,
		small, strike, strong, sub, sup, tt, var,
		b, u, i, center,
		dl, dt, dd, ol, ul, li,
		fieldset, form, label, legend,
		table, caption, tbody, tfoot, thead, tr, th, td,
		article, aside, canvas, details, embed,
		figure, figcaption, footer, header, hgroup,
		menu, nav, output, ruby, section, summary,
		time, mark, audio, video {
			margin: 0;
			padding: 0;
			border: 0;
			font-size: 100%;
			font: inherit;
			vertical-align: baseline;
		}
		/* HTML5 display-role reset for older browsers */
		article, aside, details, figcaption, figure,
		footer, header, hgroup, menu, nav, section {
			display: block;
		}
		header {
            position : static;
        }
		body {
			line-height: 1;
		}
		ol, ul {
			list-style: none;
		}
		blockquote, q {
			quotes: none;
		}
		blockquote:before, blockquote:after,
		q:before, q:after {
			content: '';
			content: none;
		}
		table {
			border-collapse: collapse;
			border-spacing: 0;
		}

		:root {
			--accent-color: #DC8ADD78;
            --height: 12px;
		}
		body {
			font-family: monospace;
		}
		header {
    		background-color: var(--accent-color);
    		color: black;
    		margin-bottom: 1em;
    		padding: 1em 3em;
    		font-size: 20px;
    	}
    	code {
    		white-space: break-spaces;
    	}

        .header-text {
            font-size: 0.6em;
        }

    	.line-number {
    		text-align: right;
    		display: inline-block;
    		min-width: 4em;
    		margin-right: 8px;
    		border-right: 4px solid var(--accent-color);
    		padding-right: 8px;
    	}
    	.line-number, code {
    		padding-top: 2px;
    		padding-bottom: 2px;
    	}
    	.issue-line .line-number {
    		background-color: #ff00008f;
    	}
    	.issue-inline-message {
    		background-color: #e8e3e8;
			margin: 5px 0;
    		padding: 1em 5em;
    		border-top: 1px solid #4E464E;
    		border-bottom: 1px solid #4E464E;
    	}
    	.issue-inline-message > details {
    		font-weight: bold;
    		margin-bottom: 1em;
    	}
    	.issue-inline-message.issue-critical > summary {
    		color: #C81E1E;
    	}
    	.issue-inline-message.issue-major > summary {
    		color: #AD5100;
    	}
    	.issue-inline-message.issue-minor > summary {
    		color: #615900;
    	}
    	p {
    		font-size: 0.em;
    		margin-top: 0.5em;
    	}
    	h1 {
    	    font-size: 2em;
            margin-bottom: 0.2em;

    	}
    	.In-the-target {
    	    font-size: 0.7em;
    	}
    	details {
            border-radius: 4px;
            padding: 0.5em 0.5em 0;
        }
        summary {
            font-weight: bold;
            margin: -0.5em -0.5em 0;
            padding: 0.5em;
        }
        details.header-issue-counter[open] {
            padding: 0.5em;
        }
        details.issue-inline-message[open] {
            background-color: #c5299e24;
            padding: 1em 5em;
            /*margin-bottom: 1em;*/
        }
        details.header-issue-counter[open] summary {
            border-bottom: 1px solid black;
            margin-bottom: 0.5em;
        }
        details.issue-inline-message[open] summary {
            /*background-color: #c5299e24;*/
    		margin-bottom: 1em;
        }
        i {
            color: black;
        }
        input[type="checkbox"] {
			accent-color: black;
		}
		button {
		    border: 0;
            line-height: 2.5;
            padding: 0 20px;
            font-size: 1rem;
            text-align: center;
            color: #fff;
            text-shadow: 1px 1px 1px #000;
            border-radius: 10px;
            background-color: rgba(220, 0, 0, 1);
            background-image: linear-gradient(to top left, rgba(0, 0, 0, 0.2), rgba(0, 0, 0, 0.2) 30%, rgba(0, 0, 0, 0));
            box-shadow: inset 2px 2px 3px rgba(255, 255, 255, 0.6), inset -2px -2px 3px rgba(0, 0, 0, 0.6);
		}
		.deploy-button:hover {
            background-color: rgba(255, 0, 0, 1);
        }

        .deploy-button:active {
            box-shadow: inset -2px -2px 3px rgba(255, 255, 255, 0.6), inset 2px 2px 3px rgba(0, 0, 0, 0.6);
        }

        .cstat-no {
          background: #F6C6CE;
        }
        .line-number2 {
    		text-align: right;
    		display: inline-block;
    		min-width: 4em;
    		padding-right: 8px;
            border-right: 4px solid var(--accent-color);
    	}
    	.line-number2, code {
    		padding-top: 2px;
    		padding-bottom: 2px;
    	}
    	.source-line {
        }
        .line-counter {
    		text-align: center;
    		display: inline-block;

            height: var(--height);
    		min-width: 4em;
    		margin-right: 8px;
    		border-right: 4px
    		padding-right: 8px;
    	}

        .cline-yes {
          background: rgb(230,245,208);
          padding-top: 2px;
          padding-bottom: 2px;
        }

        .cline-no {
          background: #FCE1E5;
          padding-top: 2px;
          padding-bottom: 2px;
        }

        .header-option{
          line-height: 1;
        }

        .filter-inputs {
            display: flex;
            align-items: center;
            margin-bottom: 0.5em;
        }
        
        .button-container {
            flex: 0 0 auto;
            margin-right: 20px; 
            display: flex;
            align-items: center;
        }
        
        .options-container {
            display: flex;
            flex-direction: column;
        }
        
        .severity-options, .issue-type-options {
            margin-top: 1px;
        }

    </style>
  </head>
  <body>
  <div class="grid-container">
    <div class="head">
    <header>
`;

const HTML_BEGIN1 = `
    </header>
    </div>`;

let HTML_MIDDLE = ``;

let HTML_END0 = `
    </div>
    <script>

        const ISSUE_TYPE_CLASSES_OR_NAME = {
			'severity-critical': 'issue-critical',
			'severity-major': 'issue-major',
			'severity-minor': 'issue-minor',
			'issue-type-memory-access': 'issue-memory-access',
			'issue-type-branch': 'issue-branch',
		};`;

const HTML_END1 = `

		const checkboxes = document.querySelectorAll('input[type="checkbox"]');
        const buttons = document.querySelectorAll('input[type="button"]');

		function change() {
            var elem = document.getElementById('button1');
            const fold = ' Fold leak details ';
            const deploy = ' Deploy leak details ';
            if (elem.innerHTML === deploy) {
                elem.innerHTML = fold;
            } else if (elem.innerHTML === fold) {
                elem.innerHTML = deploy;
            } else {
                console.log(elem.innerHTML);
            }

            const issueSummaries = document.querySelectorAll('details.issue-inline-message' + ' summary');
            const issueSummariesOpen = document.querySelectorAll('details.issue-inline-message[open]' + ' summary');
            const issueSummariesArray = Array.from(issueSummaries);
            const issueSummariesOpenArray = Array.from(issueSummariesOpen);

            if (elem.innerHTML === fold) {
                let issueSummariesClose = [];
                for (let i = 0; i < issueSummariesArray.length; i++) {
                    const issue = issueSummariesArray[i];
                    if (!(issueSummariesOpenArray.includes(issue))) {
                        issueSummariesClose.push(issue);
                    }
                }
                for (let k = 0; k < issueSummariesClose.length; k++) {
		            issueSummariesClose[k].click();
		            for (let i = 0; i < 5 ; i++) {
		                checkboxes[i].checked = true;
            	    }
            	}
            } else if (elem.innerHTML === deploy) {
                for (let k = 0; k < issueSummariesOpen.length; k++) {
	                issueSummariesOpenArray[k].click()
	                for (let i = 0; i < 5 ; i++) { // 5 first checkboxes are Severity and Issue type one, others are for target-name
		                checkboxes[i].checked = false;
            	   }
                }
            } else {
                console.log("error");
            }
        }

        change();

        var lastClick = ["0"];
		for (let i = 0; i < checkboxes.length; i++) {
		    checkboxes[i].addEventListener('change', () => {
// 		        console.log(ISSUE_TYPE_CLASSES_OR_NAME[checkboxes[i].name]);
		        const issueSelector = '.' + ISSUE_TYPE_CLASSES_OR_NAME[checkboxes[i].name];
		        const issueSummaries = document.querySelectorAll('details' + issueSelector + ' > summary');
		        for (let i = 0; i < issueSummaries.length; i++) {
			        issueSummaries[i].click();
			    }
	        });
	   }
       
    </script>
  </body>
</html>`;


///////////////////////////////////////////////////////////////////////////////
///////////////////// Objects & Arrays Build //////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

var reportCoverage = [];

let files = await readdir(coverageDir);

for (const file of files) {
    const filePath = path.join(coverageDir, file);
    reportCoverage.push(JSON.parse(await readFile(filePath, { encoding: 'utf8' })));
}

const issues = JSON.parse(await readFile(jsonReportPath));

const fileIssues = {};
const targetName = {};
const reportCoverageWithGoodIssue = {};

/*
 *  The following loop fill both fileIssues & targetName such as :
 * fileIssues = { path1 : [
 *                 {
 *                  description: "",
 *                  fingerprint: "",
 *                  severity: "",
 *                  location: [object]
 *                 },
 *                 { ... },
 *                  ...
 *                      ],
 *                path2 : ...,
 *                ...
 *             }
 * targetName = { targetName1 : [
 *                 {
 *                  description: "",
 *                  fingerprint: "",
 *                  severity: "",
 *                  location: [object]
 *                 },
 *                 { ... },
 *                  ...
 *                      ],
 *                targetName2 : ...,
 *                ...
 *             }
*/

if (issues !== null) {
    for (const issue of issues) {

        const path = issue.location.path;

        if (Object.keys(fileIssues).includes(path)) {
            fileIssues[path].push(issue);
        } else {
            fileIssues[path] = [issue];
        }

        const name = extractTarget(issue.description);

        if (Object.keys(targetName).includes(name)) {
            targetName[name].push(issue);
        } else {
            targetName[name] = [issue];
        }
        await mkdir(CT_REPORTS_DIR, { recursive: true });
    }

    var ALL_ISSUE_PER_FILE_NAME = Object.values(fileIssues).sort();
    var ALL_FILE_NAME = Object.keys(fileIssues).sort();
    var ALL_TARGETS_NAME = Object.keys(targetName).sort();

    /*
    *  The following loop fill reportCoverageWithGoodIssue such as :
    * reportCoverageWithGoodIssue =
    *          {
    *              path1 : {
    *                  targetName1: [
    *                      [ {
    *                      functionName: "",
    *                      ranges: [Array],
    *                      isBlockCoverage: Boolean
    *                       },
    *                       { ... },
    *                      ...
    *                     ],
    *                  targetName2: [ : ...,
    *                  ...
    *              },
    *              path2 : ...,
    *              ...
    *           }
    */

    for (const fileName of ALL_FILE_NAME) {
        for (const name of ALL_TARGETS_NAME) {
            for (let i = 0; i < reportCoverage.length; i++) {
                for (const issuecoverage of reportCoverage[i].result) {
                    if (issuecoverage.url.includes(fileName)) {
                        for (const issuecoveragename of reportCoverage[i].result) {
                            reportCoverageWithGoodIssue[fileName] = reportCoverageWithGoodIssue[fileName] || {};
                            if (issuecoveragename.url.includes(name)) {
                                reportCoverageWithGoodIssue[fileName][name] = reportCoverageWithGoodIssue[fileName][name] || {};
                                reportCoverageWithGoodIssue[fileName][name] = [issuecoverage.functions];
                            }
                        }
                    }
                }
            }
        }
    }


///////////////////////////////////////////////////////////////////////////////
//////////////////// Counters & Proportions ///////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

    var COUNTER_ARRAY = counterErrorTotalAndPerFile(ALL_ISSUE_PER_FILE_NAME);

    var TOTAL_NUMBER_OF_ISSUES = COUNTER_ARRAY[0].slice(-1);
    //const CRITICAL_COUNTER = COUNTER_ARRAY[1].slice(-1)[0];
    //const MAJOR_COUNTER = COUNTER_ARRAY[1].slice(-1)[1];
    //const MINOR_COUNTER = COUNTER_ARRAY[1].slice(-1)[2];
    //const CRITICAL_PROPORTION = Math.round((CRITICAL_COUNTER / TOTAL_NUMBER_OF_ISSUES) * 100);
    //const MAJOR_PROPORTION = Math.round((MAJOR_COUNTER / TOTAL_NUMBER_OF_ISSUES) * 100);
    //const MINOR_PROPORTION = Math.round((MINOR_COUNTER / TOTAL_NUMBER_OF_ISSUES) * 100);
    //const MEMORY_ACCESS_ERROR_COUNTER = COUNTER_ARRAY[2].slice(-1)[0];
    //const JUMP_INSTRUCTION_ERROR_COUNTER = COUNTER_ARRAY[2].slice(-1)[1];
    //const MEMORY_ACCESS_ERROR_PROPORTION = Math.round((MEMORY_ACCESS_ERROR_COUNTER / TOTAL_NUMBER_OF_ISSUES) * 100);
    //const JUMP_INSTRUCTION_ERROR_PROPORTION = Math.round((JUMP_INSTRUCTION_ERROR_COUNTER / TOTAL_NUMBER_OF_ISSUES) * 100);

///////////////////////////////////////////////////////////////////////////////
///////////////////// tableOfIntervals build //////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

    var TOTAL_NUMBER_OF_LINE = 0;

    let index1 = 0; // in order to know in which file we are

    for (const issues of ALL_ISSUE_PER_FILE_NAME) {

        const path = issues[0].location.path;

        const sourceCodeFile2 = await open(`${sourceCodeDir}/${path}`);

        var tableOfIntervals = [];

        var counterOfCharacters = 0;

        var lineCounter = 1;
        TOTAL_NUMBER_OF_LINE = 0;

        const targetNameForCoverageWithGoodIssue = Object.keys(reportCoverageWithGoodIssue[path]);
        const targetNameForCoverageWithGoodIssueLength = targetNameForCoverageWithGoodIssue.length;

        const name = targetNameForCoverageWithGoodIssue[0];

        for await (const line of sourceCodeFile2.readLines()) {

            const lineLength = line.length;
            const startOfLine = counterOfCharacters;
            const endOfLine = counterOfCharacters + lineLength;

            let tableOfIntervalsForOneLine = [];

            const contentSize = reportCoverageWithGoodIssue[path][name][0].length;

            for (let i = 0; i < contentSize; i++) {
                const amount = reportCoverageWithGoodIssue[path][name][0][i].ranges.length;

                for (let j = 0; j < amount; j++) {
                    const startOffset = reportCoverageWithGoodIssue[path][name][0][i].ranges[j].startOffset;
                    const endOffset = reportCoverageWithGoodIssue[path][name][0][i].ranges[j].endOffset;

                    /* For an interval bordered by:
                    * A = startOffset - (lineCounter - 1)
                    * B = endOffset - (lineCounter - 1)
                    *
                    * on a line bordered by:
                    * C = startOfLine
                    * D = endOfLine
                    *
                    * we get four cases:
                    *
                    * C _____ A---B_____ D  = > [A - C, B - C]
                    * A-------C---B______D  = > [C - C, B - C]
                    * C_______A---D------B  = > [A - C, D - C]
                    * A-------C---D------B  = > [C - C, D - C]
                    */

                    const A = startOffset - (lineCounter - 1);
                    const B = endOffset - (lineCounter - 1);
                    const C = startOfLine;
                    const D = endOfLine;

                    if (C <= A && B <= D) {
                        if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                            tableOfIntervalsForOneLine.push([A - C, B - C]);
                        }
                    } else if (A <= C && B <= D && B > C) {
                        if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                            tableOfIntervalsForOneLine.push([0, B - C]);
                        }
                    } else if (C <= A && A < D && B > D) {
                        if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                            tableOfIntervalsForOneLine.push([A - C, D - C]);
                        }
                    } else if (A <= C && D <= B) {
                        if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                            tableOfIntervalsForOneLine.push([0, D - C]);
                        }
                    }
                }
            }

            tableOfIntervals.push(tableOfIntervalsForOneLine);
            counterOfCharacters += lineLength;

            lineCounter++;
        }

        var TOTAL_NUMBER_OF_CHARACTERS = counterOfCharacters;
        TOTAL_NUMBER_OF_LINE = lineCounter - 1;

        if (targetNameForCoverageWithGoodIssueLength !== 1) {

            counterOfCharacters = 0;

            lineCounter = 1;

            const sourceCodeFile3 = await open(`${sourceCodeDir}/${path}`);

            for await (const line of sourceCodeFile3.readLines()) {

                const lineLength = line.length;
                const startOfLine = counterOfCharacters;
                const endOfLine = counterOfCharacters + lineLength;

                for (let ctr = 1; ctr < targetNameForCoverageWithGoodIssueLength; ctr++) {

                    const name = targetNameForCoverageWithGoodIssue[ctr];

                    let tableOfIntervalsForOneLine = [];

                    const contentSize = reportCoverageWithGoodIssue[path][name][0].length;

                    for (let i = 0; i < contentSize; i++) {

                        const amount = reportCoverageWithGoodIssue[path][name][0][i].ranges.length;

                        for (let j = 0; j < amount; j++) {
                            const startOffset = reportCoverageWithGoodIssue[path][name][0][i].ranges[j].startOffset;
                            const endOffset = reportCoverageWithGoodIssue[path][name][0][i].ranges[j].endOffset;

                            /* For an interval bordered by:
                            * A = startOffset - (lineCounter - 1)
                            * B = endOffset - (lineCounter - 1)
                            *
                            * on a line bordered by:
                            * C = startOfLine
                            * D = endOfLine
                            *
                            * we get four cases:
                            *
                            * C _____ A---B_____ D  = > [A - C, B - C]
                            * A-------C---B______D  = > [C - C, B - C]
                            * C_______A---D------B  = > [A - C, D - C]
                            * A-------C---D------B  = > [C - C, D - C]
                            */

                            const A = startOffset - (lineCounter - 1);
                            const B = endOffset - (lineCounter - 1);
                            const C = startOfLine;
                            const D = endOfLine;

                            if (C <= A && B <= D) {
                                if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                                    tableOfIntervalsForOneLine.push([A - C, B - C]);
                                }
                            } else if (A <= C && B <= D && B > C) {
                                if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                                    tableOfIntervalsForOneLine.push([0, B - C]);
                                }
                            } else if (C <= A && A < D && B > D) {
                                if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                                    tableOfIntervalsForOneLine.push([A - C, D - C]);
                                }
                            } else if (A <= C && D <= B) {
                                if (reportCoverageWithGoodIssue[path][name][0][i].ranges[j].count === 0) {
                                    tableOfIntervalsForOneLine.push([0, D - C]);
                                }
                            }
                        }
                    }

                    if (tableOfIntervalsForOneLine.length !== 0) { // To check whether it is an empty table or not

                        if (tableOfIntervals[lineCounter - 1].length !== 0) {
                            for (let i = 0; i < tableOfIntervals[lineCounter - 1].length; i++) {
                                for (let j = 0; j < tableOfIntervalsForOneLine.length; j++) {

                                    /* For an interval bordered by:
                                        * A = tableOfIntervals[lineCounter - 1][i][0]
                                        * B = tableOfIntervals[lineCounter - 1][i][1]
                                        *
                                        * and an interval bordered by:
                                        * C = tableOfIntervalsForOneLine[j][0]
                                        * D = tableOfIntervalsForOneLine[j][1]
                                        *
                                        * we get four cases:
                                        *
                                        * C _____ A---B_____ D  = > [A, B]
                                        * A-------C---B______D  = > [C, B]
                                        * C_______A---D------B  = > [A, D]
                                        * A-------C---D------B  = > [C, D]
                                        */

                                    const A = tableOfIntervals[lineCounter - 1][i][0];
                                    const B = tableOfIntervals[lineCounter - 1][i][1];
                                    const C = tableOfIntervalsForOneLine[j][0];
                                    const D = tableOfIntervalsForOneLine[j][1];

                                    if (C <= A && B <= D) {
                                        tableOfIntervals[lineCounter - 1][i] = tableOfIntervals[lineCounter - 1][i];
                                    } else if (A <= C && B <= D && C < B) {
                                        tableOfIntervals[lineCounter - 1][i][0] = C;
                                    } else if (C <= A && A < D && D < B) {
                                        tableOfIntervals[lineCounter - 1][i][1] = D;
                                    } else if (A <= C && D <= B) {
                                        tableOfIntervals[lineCounter - 1][i] = tableOfIntervalsForOneLine[j];
                                    }
                                }
                            }
                        } else {
                            tableOfIntervals[lineCounter - 1] = tableOfIntervals[lineCounter - 1]; // with tableOfIntervals[lineCounter - 1] === []
                        }
                    } else {
                        tableOfIntervals[lineCounter - 1] = tableOfIntervalsForOneLine;
                    }

                }
                counterOfCharacters += lineLength;

                lineCounter++;
            }
        }

        TOTAL_NUMBER_OF_LINE = lineCounter - 1;

///////////////////////////////////////////////////////////////////////////////
///////////////////// tableOfIntervals sorted /////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

        let NUMBER_OF_CHARACTERS = 0;
        let NUMBER_OF_LINE = 0;

        for (let i = 0; i < tableOfIntervals.length; i++) {

            tableOfIntervals[i] = tableOfIntervals[i].sort();

            const len = tableOfIntervals[i].length;

            if (len >= 2) {
                let tampon = [];
                let j = 0;
                outer : while (j < tableOfIntervals[i].length - 1) {
                    let test = true;
                    if ((tableOfIntervals[i][j][0] === tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][1] === tableOfIntervals[i][j + 1][1])) {
                        /*
                        * [[a, b], [a, b]]
                        *  a === a ; b === b ;
                        */
                        for (let k = 0; k < tampon.length; k++) {
                            /* Check if tableOfIntervals[i][j] is already in tampon or not */
                            if (JSON.stringify(tampon[k]) === JSON.stringify(tableOfIntervals[i][j])) {
                                console.log("tableOfIntervals[i][j] is already in tampon");
                                test = false;
                                break;
                            }
                        }
                        if (test) {
                            tampon = tampon.concat([tableOfIntervals[i][j]]);
                        }
                        j++;
                    } else if ((tableOfIntervals[i][j][0] === tableOfIntervals[i][j + 1][0])) {
                        /*
                        * [[a, b], [a, c]]
                        *  a === a ; b > c ;
                        *  a === a ; b < c ;
                        */
                        if (tableOfIntervals[i][j][1] > tableOfIntervals[i][j + 1][1]) {
                            tampon = tampon.concat([tableOfIntervals[i][j + 1]]);
                        } else {
                            tampon = tampon.concat([tableOfIntervals[i][j]]);
                        }
                        j++;
                    } else if ((tableOfIntervals[i][j][1] === tableOfIntervals[i][j + 1][1])) {
                        /*
                        * [[a, c], [b, c]]
                        *  a > b ; c === c ;
                        *  a < b ; c === c ;
                        */
                        if (tableOfIntervals[i][j][0] > tableOfIntervals[i][j + 1][0]) {
                            tampon = tampon.concat([tableOfIntervals[i][j]]);
                        } else {
                            tampon = tampon.concat([tableOfIntervals[i][j + 1]]);
                        }
                        j++;
                    } else if ((tableOfIntervals[i][j][0] < tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][1] > tableOfIntervals[i][j + 1][1])) {
                        /*
                        * [[a, c], [b, c]]
                        *  a > b ; c === c ;
                        *  a < b ; c === c ;
                        */
                        tampon = tampon.concat([tableOfIntervals[i][j + 1]]);
                        j++;
                    } else if ((tableOfIntervals[i][j][0] > tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][1] < tableOfIntervals[i][j + 1][1])) {
                        /*
                        * [[a, b], [c, d]]
                        *  a > c ; b < d ;
                        *  c____a---b____d
                        */
                        tampon = tampon.concat([tableOfIntervals[i][j]]);
                        j++;
                    } else if (len === 2) {
                        /*
                        * [[a, b], [c, d]]
                        *  b < c ;
                        *  a____b  c____d
                        */
                        if ((tableOfIntervals[i][j][0] < tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][0] < tableOfIntervals[i][j + 1][1]) && (tableOfIntervals[i][j][1] < tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][1] < tableOfIntervals[i][j + 1][1])) {
                            tampon = tableOfIntervals[i];
                            j++;
                        }
                    } else if ((tableOfIntervals[i][j][0] < tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][0] < tableOfIntervals[i][j + 1][1]) && (tableOfIntervals[i][j][1] < tableOfIntervals[i][j + 1][0]) && (tableOfIntervals[i][j][1] < tableOfIntervals[i][j + 1][1])) {
                        /*
                        * [[a, b], [c, d]]
                        *  b < c ;
                        *  a____b  c____d
                        */
                        if (tampon.length === 0) {
                            tampon.push(tableOfIntervals[i][j]);
                            j++;
                        } else {
                            tampon.push(tableOfIntervals[i][j]);
                            tampon.push(tableOfIntervals[i][j + 1]);
                            j++;
                        }
                    } else {
                        console.log("new case");
                        tampon = tableOfIntervals[i];
                        console.log(tableOfIntervals[i]);
                        break outer;
                    }
                }
                tableOfIntervals[i] = tampon;
            }

            for (let j = 0; j < tableOfIntervals[i].length; j++) {
                NUMBER_OF_CHARACTERS += tableOfIntervals[i][j][1] - tableOfIntervals[i][j][0];
                NUMBER_OF_LINE += 1;
            }
        }

///////////////////////////////////////////////////////////////////////////////
///////////////////// HTML/CSS report build ///////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

        const TOTAL_NUMBER_OF_ISSUES_IN_FILE = COUNTER_ARRAY[0][index1];
        const CRITICAL_COUNTER_IN_FILE = COUNTER_ARRAY[1][index1][0];
        //const MAJOR_COUNTER_IN_FILE = COUNTER_ARRAY[1][index1][1];
        //const MINOR_COUNTER_IN_FILE = COUNTER_ARRAY[1][index1][2];
        const CRITICAL_PROPORTION_IN_FILE = Math.round((CRITICAL_COUNTER_IN_FILE / TOTAL_NUMBER_OF_ISSUES_IN_FILE) * 100);
        const MEMORY_ACCESS_ERROR_COUNTER_IN_FILE = COUNTER_ARRAY[2][index1][0];
        //const JUMP_INSTRUCTION_ERROR_COUNTER_IN_FILE = COUNTER_ARRAY[2][index1][1];
        const MEMORY_ACCESS_ERROR_PROPORTION_IN_FILE = Math.round((MEMORY_ACCESS_ERROR_COUNTER_IN_FILE / TOTAL_NUMBER_OF_ISSUES_IN_FILE) * 100);

        const NUMBER_OF_CHARACTERS_PROPORTION_IN_FILE = Math.round((NUMBER_OF_CHARACTERS/TOTAL_NUMBER_OF_CHARACTERS)*100);
        const NUMBER_OF_LINE_PROPORTION_IN_FILE = Math.round((NUMBER_OF_LINE/TOTAL_NUMBER_OF_LINE)*100);
        index1 ++;

        let reportHtml = HTML_BEGIN0;

        const sourceCodeFile = await open(`${sourceCodeDir}/${path}`);
        reportHtml += `<h1>${escapeHtml(path.replace("node_modules/", ""))}</h1>
        <div class="filter-inputs">
            <div class="button-container">
                <button onclick="change()" title="Click here to expand or collapse all leak details!" class="deploy-button" Id="button1"> Deploy leak details </button>
            </div>
            <div class="options-container">
                <div class="severity-options">
                    <span class="header-option">Severity:</span>
                    <label><input type="checkbox" name="severity-critical" > Critical</label>
                    <label><input type="checkbox" name="severity-major" > Major</label>
                    <label><input type="checkbox" name="severity-minor" > Minor</label>
                </div>
                <div class="issue-type-options">
                    <span>Issue type:</span>
                    <label><input type="checkbox" name="issue-type-memory-access" > Memory access</label>
                    <label><input type="checkbox" name="issue-type-branch" > Branch</label>
                </div>
            </div>
        </div>
        <p class="header-text">Total number of problem found: ${TOTAL_NUMBER_OF_ISSUES_IN_FILE} out of ${TOTAL_NUMBER_OF_ISSUES}</p>
        <p class="header-text">Number of critical in file: ${CRITICAL_COUNTER_IN_FILE}   \u2245 ${CRITICAL_PROPORTION_IN_FILE}%</p>
        <p class="header-text">Number of memory access error in file: ${MEMORY_ACCESS_ERROR_COUNTER_IN_FILE}   \u2245 ${MEMORY_ACCESS_ERROR_PROPORTION_IN_FILE}%</p>
        <p class="header-text">Coverage proportion (on the number of character): ${NUMBER_OF_CHARACTERS} \/ ${TOTAL_NUMBER_OF_CHARACTERS}   \u2245 ${NUMBER_OF_CHARACTERS_PROPORTION_IN_FILE}%</p>
        <p class="header-text">Coverage proportion (on the number of line): ${NUMBER_OF_LINE} \/ ${TOTAL_NUMBER_OF_LINE}   \u2245 ${NUMBER_OF_LINE_PROPORTION_IN_FILE}%</p>`;

        const COUNTER_ARRAY_PER_TARGET = counterErrorPerName(issues);

        let index2 = 0; // To know which target we are in

        for (const name of ALL_TARGETS_NAME) {

            const TARGET = issues.filter((issue) => extractTarget(issue.description) === name);

            const TOTAL_NUMBER_OF_ISSUES_IN_NAMES = COUNTER_ARRAY_PER_TARGET[0][index2];

            if (TOTAL_NUMBER_OF_ISSUES_IN_NAMES){
                const CRITICAL_COUNTER_IN_NAMES = COUNTER_ARRAY_PER_TARGET[1][index2][0];
                //const MAJOR_COUNTER_IN_NAMES = COUNTER_ARRAY_PER_TARGET[1][index2][1];
                //const MINOR_COUNTER_IN_NAMES = COUNTER_ARRAY_PER_TARGET[1][index2][2];
                const CRITICAL_PROPORTION_IN_NAMES = Math.round((CRITICAL_COUNTER_IN_NAMES / TOTAL_NUMBER_OF_ISSUES_IN_NAMES) * 100);
                const MEMORY_ACCESS_ERROR_COUNTER_IN_NAMES = COUNTER_ARRAY_PER_TARGET[2][index2][0];
                //const JUMP_INSTRUCTION_ERROR_COUNTER_IN_NAMES = COUNTER_ARRAY_PER_TARGET[2][index2][1];
                const MEMORY_ACCESS_ERROR_PROPORTION_IN_NAMES = Math.round((MEMORY_ACCESS_ERROR_COUNTER_IN_NAMES / TOTAL_NUMBER_OF_ISSUES_IN_NAMES) * 100);


                reportHtml += `<details open class="header-issue-counter"><summary class="In-the-target"><name="target-${name}" Id="${name}" <h2>In the target-${name}</h2></summary>
            <p class="header-text">Number of problem found: ${TOTAL_NUMBER_OF_ISSUES_IN_NAMES}</p>
            <p class="header-text">Number of critical: ${CRITICAL_COUNTER_IN_NAMES}   \u2245 ${CRITICAL_PROPORTION_IN_NAMES}%</p>
            <p class="header-text">Number of memory access error: ${MEMORY_ACCESS_ERROR_COUNTER_IN_NAMES}   \u2245 ${MEMORY_ACCESS_ERROR_PROPORTION_IN_NAMES}%</p></details>`;

                HTML_END0 += `
            ISSUE_TYPE_CLASSES_OR_NAME['target-${name}'] = 'target-${name}';`;
            }

            index2++;

        }

        reportHtml += HTML_BEGIN1;

        counterOfCharacters = 0;
        lineCounter = 1;
        var reportLine = ``;

        for await (const line of sourceCodeFile.readLines()) {

            const issuesFound = issues.filter((issue) => issue.location.lines.begin === lineCounter);

            const lineLength = line.length;
            const sizeOfTableOfIntervals = tableOfIntervals[lineCounter - 1].length;

            if (sizeOfTableOfIntervals === 0) {
                let reportLine = `<div class="source-line"><span class="line-number ">${lineCounter}</span><span class="line-counter cline-yes">&nbsp</span><code>${Buffer.from(escapeHtml(line))}</code></div>\n`;

                const issuesFound = issues.filter((issue) => issue.location.lines.begin === lineCounter);

                if (issuesFound.length > 0) {
                    HTML_MIDDLE += `<mark class="issue-line">${reportLine}</mark>`;

                    for (const name of ALL_TARGETS_NAME) {

                        let issuesWithGoodNameFound = issuesFound.filter((issue) => extractTarget(issue.description) === name);

                        if (issuesWithGoodNameFound.length > 1) {
                            issuesWithGoodNameFound = comparator(issuesWithGoodNameFound);
                        } 

                        for (const issueFound of issuesWithGoodNameFound) {
                            let vulnType = 'Unknown type of issue';
                            let vulnTypeClass =  '';

                            if (issueFound.description.includes('memory access instruction')) {
                                vulnType = 'Secret-dependent memory access';
                                vulnTypeClass = 'issue-memory-access';
                            } else if (issueFound.description.includes('jump instruction')) {
                                vulnType = 'Secret-dependent branch';
                                vulnTypeClass = 'issue-branch';
                            }

                            const description =  issueFound.description
                                .replace(' Check analysis result in artifacts for details.', '')
                                .replace(TARGET_NAME_REGEX, '[$<name>]'); // Extract the target name

                            const severityClass = SEVERITY_STYLES[issueFound.severity].severityClass;
                            const severityIcon = SEVERITY_STYLES[issueFound.severity].icon;

                            HTML_MIDDLE += `<details class="issue-inline-message ${severityClass} ${vulnTypeClass} target-${name}">
                        <summary>${severityIcon}&nbsp;<i>${name}</i> ${vulnType}</summary>
                <div class="issue-description">${escapeHtml(description)}</div>
            </details>`;
                        }
                    }
                } else {
                    HTML_MIDDLE += reportLine;
                }
            } else {
                let lineBeginning = 0;
                let reportLine = `<div class="source-line"><span class="line-number ">${lineCounter}</span><span class="line-counter cline-no">！</span><code>`;
                for (let i = 0; i < sizeOfTableOfIntervals; i++) {
                    if (tableOfIntervals[lineCounter - 1][i][0] === tableOfIntervals[lineCounter - 1][i][1]) {
                        reportLine += `<span class="cstat-no" title="statement not covered"><br/></span>`;
                    } else {
                        reportLine += `<span>${Buffer.from(escapeHtml(line.substring(lineBeginning, tableOfIntervals[lineCounter - 1][i][0])))}</span>`;
                        lineBeginning = tableOfIntervals[lineCounter - 1][i][0];
                        reportLine += `<span class="cstat-no" title="statement not covered">${Buffer.from(escapeHtml(line.substring(lineBeginning, tableOfIntervals[lineCounter - 1][i][1])))}</span>`;
                        lineBeginning = tableOfIntervals[lineCounter - 1][i][1];
                    }
                }
                const lineEnding = line.length;
                if (lineBeginning <= lineEnding) {
                    reportLine += `<span>${Buffer.from(escapeHtml(line.substring(lineBeginning, lineEnding)))}</span>`;
                }
                reportLine += `</code></div>\n`;
                HTML_MIDDLE += reportLine;
            }
            counterOfCharacters += lineLength;
            lineCounter++;

        }
        reportHtml += HTML_MIDDLE;
        HTML_MIDDLE = ``;
        reportHtml += HTML_END0;
        reportHtml += HTML_END1;

        const reportFilePath = `${CT_REPORTS_DIR}/${path.replaceAll('/','_').replaceAll('node_modules_','')}.html`;

        await writeFile(reportFilePath, reportHtml);

        console.log(`HTML report ${reportFilePath} has been saved!`);
    }
} else {
    console.log("The json report file is empty. Please generate some test with the test_generation.js file and generate some test cases thanks to the testcase_geneation.js file.");
}

///////////////////////////////////////////////////////////////////////////////
///////////////////// Auxiliary Functions /////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////

function extractTarget(description) {
	return TARGET_NAME_REGEX.exec(description).groups.name;
}

function counterErrorTotalAndPerFile(object) {
    /*
     *  Example of return : (here we have three files)
     *    On the first line : number of error for each file, then number total of error for all files
     *    On the second line : number of [critical, major, minor] error for each file, then number total of [critical, major, minor] error for all files
     *    On the third line : number of [memory access, jump instruction] error for each file, then number total of [memory access, jump instruction] error for all files
     * [
        [ 9, 54, 484, 547 ],
        [ [ 8, 1, 0 ], [ 43, 11, 0 ], [ 472, 12, 0 ], [ 523, 24, 0 ] ],
        [ [ 0, 9 ], [ 42, 12 ], [ 484, 0 ], [ 526, 21 ] ]
       ]
     */

	let ctrTotal = 0;
	let ctrTotalSeverityCritical = 0;
	let ctrTotalSeverityMajor = 0;
	let ctrTotalSeverityMinor = 0;
	let ctrTotalTypeMemoryAccess = 0;
	let ctrTotalTypeJumpInstruction = 0;

    let ctrArrayTotal = [];
	let ctrArray = [];
    let ctrArraySeverity = [];
    let ctrArrayTypeOfError = [];

    for (const issue of ALL_ISSUE_PER_FILE_NAME) {
        let ctr = 1;
        let ctrCriticalPerFile = 0;
        let ctrMajorPerFile = 0;
        let ctrMinorPerFile = 0;
        let ctrMemoryAccessError = 0;
        let ctrJumpInstructionError = 0;

        issue.sort((a, b) => Number(a.location.lines.begin) - Number(b.location.lines.begin));
        const keysOfObject = Object.keys(issue);
        const sizeOfObject = Number(keysOfObject.slice(-1)[0]) + 1;

        for (let i = 0; i < (sizeOfObject - 1); i++) {
            if (issue[i].location.lines.begin !== issue[(i + 1)].location.lines.begin) {
                ctr++;
                switch (issue[i].severity) {
                    case 'critical':
                        ctrCriticalPerFile++;
                        break;
                    case 'major':
                        ctrMajorPerFile++;
                        break;
                    case 'minor':
                        ctrMinorPerFile++;
                        break;
                    default:
                        console.log(`New severity found in ${issue}`);
                }
                const typeOfError = issue[i].description;
                if (typeOfError.includes('memory access instruction')) {
                    ctrMemoryAccessError++;
                } else if (typeOfError.includes('jump instruction')) {
                    ctrJumpInstructionError++;
                } else {
                    console.log(`New vulnerability type found in ${issue}`);
                }
            }
            if ((issue[i].location.lines.begin === issue[(i + 1)].location.lines.begin) && ((extractTarget(issue[i].description)) !== (extractTarget(issue[i + 1].description)))) {
                ctr++;
                switch (issue[i].severity) {
                    case 'critical':
                        ctrCriticalPerFile++;
                        break;
                    case 'major':
                        ctrMajorPerFile++;
                        break;
                    case 'minor':
                        ctrMinorPerFile++;
                        break;
                    default:
                        console.log(`New severity found in ${issue}`);
                }
                const typeOfError = issue[i].description;
                if (typeOfError.includes('memory access instruction')) {
                    ctrMemoryAccessError++;
                } else if (typeOfError.includes('jump instruction')) {
                    ctrJumpInstructionError++;
                } else {
                    console.log(`New vulnerability type found in ${issue}`);
                }
            }
        }
        switch (issue[sizeOfObject - 1].severity) {
            case 'critical':
                ctrCriticalPerFile++;
                break;
            case 'major':
                ctrMajorPerFile++;
                break;
            case 'minor':
                ctrMinorPerFile++;
                break;
            default:
                console.log(`New severity found in ${issue}`);
        }
        const typeOfError = issue[sizeOfObject - 1].description;
        if (typeOfError.includes('memory access instruction')) {
            ctrMemoryAccessError++;
        } else if (typeOfError.includes('jump instruction')) {
            ctrJumpInstructionError++;
        } else {
            console.log(`New vulnerability type found in ${issue}`);
        }
        ctrTotal += ctr;
        ctrArray.push(ctr);

		ctrTotalSeverityCritical += ctrCriticalPerFile;
		ctrTotalSeverityMajor += ctrMajorPerFile;
		ctrTotalSeverityMinor += ctrMinorPerFile;

        ctrArraySeverity.push([ctrCriticalPerFile, ctrMajorPerFile, ctrMinorPerFile]);

		ctrTotalTypeMemoryAccess += ctrMemoryAccessError;
		ctrTotalTypeJumpInstruction += ctrJumpInstructionError;

        ctrArrayTypeOfError.push([ctrMemoryAccessError, ctrJumpInstructionError]);
	}

    ctrArray.push(ctrTotal);
    ctrArraySeverity.push([ctrTotalSeverityCritical, ctrTotalSeverityMajor, ctrTotalSeverityMinor]);
	ctrArrayTypeOfError.push([ctrTotalTypeMemoryAccess, ctrTotalTypeJumpInstruction]);
    ctrArrayTotal.push(ctrArray);
	ctrArrayTotal.push(ctrArraySeverity);
	ctrArrayTotal.push(ctrArrayTypeOfError);

    return ctrArrayTotal;
}

function counterErrorPerName(object) {
    /*
     *  Example of return : (here we have nine target name)
     *    On the first line : number of error for each target name,  then number total of error in the entire file
     *    On the second line : number of [critical, major, minor] error for each target name, then number total of [critical, major, minor] error in the entire file
     *    On the third line : number of [memory access, jump instruction] error for target name, then number total of [memory access, jump instruction] error in the entire file
     * [
        [
          1, 1, 1,  1,  1,  1,
          1, 1, 1, 15, 15, 15,
          54
        ],
        [
          [ 1, 0, 0 ],   [ 0, 1, 0 ],
          [ 1, 0, 0 ],   [ 1, 0, 0 ],
          [ 1, 0, 0 ],   [ 0, 1, 0 ],
          [ 0, 1, 0 ],   [ 1, 0, 0 ],
          [ 0, 1, 0 ],   [ 12, 3, 0 ],
          [ 13, 2, 0 ],  [ 13, 2, 0 ],
          [ 43, 11, 0 ]
        ],
        [
          [ 0, 1 ],   [ 0, 1 ],
          [ 0, 1 ],   [ 0, 1 ],
          [ 0, 1 ],   [ 0, 1 ],
          [ 0, 1 ],   [ 0, 1 ],
          [ 0, 1 ],   [ 14, 1 ],
          [ 14, 1 ],  [ 14, 1 ],
          [ 42, 12 ]
        ]
       ]
     */

	let ctrTotal = 0;
	let ctrTotalSeverityCritical = 0;
	let ctrTotalSeverityMajor = 0;
	let ctrTotalSeverityMinor = 0;
	let ctrTotalTypeMemoryAccess = 0;
	let ctrTotalTypeJumpInstruction = 0;

    let ctrArrayTotal = [];
	let ctrArray = [];
    let ctrArraySeverity = [];
    let ctrArrayTypeOfError = [];

    for (const name of ALL_TARGETS_NAME) {

        const problem = object.filter((issue) => extractTarget(issue.description) === name);

        if (problem.length === 0) {

            ctrArray.push(0);
            ctrArraySeverity.push([0, 0, 0]);
            ctrArrayTypeOfError.push([0, 0]);

        } else {

            let ctr = 0;
            let ctrCriticalPerFile = 0;
            let ctrMajorPerFile = 0;
            let ctrMinorPerFile = 0;
            let ctrMemoryAccessError = 0;
            let ctrJumpInstructionError = 0;

            problem.sort((a, b) => Number(a.location.lines.begin) - Number(b.location.lines.begin));
            const keysOfObject = Object.keys(problem);
            const sizeOfObject = Number(keysOfObject.slice(-1)[0]) + 1;

            if (sizeOfObject === 1) {

                ctr++;
                switch (problem[0].severity) {
                    case 'critical':
                        ctrCriticalPerFile++;
                        break;
                    case 'major':
                        ctrMajorPerFile++;
                        break;
                    case 'minor':
                        ctrMinorPerFile++;
                        break;
                    default:
                        console.log(`New severity found in ${problem}`);
                }

                const typeOfError = problem[0].description;

                if (typeOfError.includes('memory access instruction')) {
                    ctrMemoryAccessError++;
                } else if (typeOfError.includes('jump instruction')) {
                    ctrJumpInstructionError++;
                } else {
                    console.log(`New vulnerability type found in ${problem}`);
                }

            } else {

                ctr++;

                for (let i = 0; i < (sizeOfObject - 1); i++) {

                    if (problem[i].location.lines.begin !== problem[(i + 1)].location.lines.begin) {

                        ctr++;
                        switch (problem[i].severity) {
                            case 'critical':
                                ctrCriticalPerFile++;
                                break;
                            case 'major':
                                ctrMajorPerFile++;
                                break;
                            case 'minor':
                                ctrMinorPerFile++;
                                break;
                            default:
                                console.log(`New severity found in ${problem}`);
                        }

                        const typeOfError = problem[i].description;

                        if (typeOfError.includes('memory access instruction')) {
                            ctrMemoryAccessError++;
                        } else if (typeOfError.includes('jump instruction')) {
                            ctrJumpInstructionError++;
                        } else {
                            console.log(`New vulnerability type found in ${problem}`);
                        }
                    }

                    if ((problem[i].location.lines.begin === problem[(i + 1)].location.lines.begin) && ((extractTarget(problem[i].description)) !== (extractTarget(problem[i + 1].description)))) {

                        ctr++;
                        switch (problem[i].severity) {
                            case 'critical':
                                ctrCriticalPerFile++;
                                break;
                            case 'major':
                                ctrMajorPerFile++;
                                break;
                            case 'minor':
                                ctrMinorPerFile++;
                                break;
                            default:
                                console.log(`New severity found in ${problem}`);
                        }

                        const typeOfError = problem[i].description;

                        if (typeOfError.includes('memory access instruction')) {
                            ctrMemoryAccessError++;
                        } else if (typeOfError.includes('jump instruction')) {
                            ctrJumpInstructionError++;
                        } else {
                            console.log(`New vulnerability type found in ${problem}`);
                        }
                    }
                }

                switch (problem[sizeOfObject - 1].severity) {
                    case 'critical':
                        ctrCriticalPerFile++;
                        break;
                    case 'major':
                        ctrMajorPerFile++;
                        break;
                    case 'minor':
                        ctrMinorPerFile++;
                        break;
                    default:

                      console.log(`New severity found in ${problem}`);
                }

                const typeOfError = problem[sizeOfObject - 1].description;
                if (typeOfError.includes('memory access instruction')) {
                    ctrMemoryAccessError++;
                } else if (typeOfError.includes('jump instruction')) {
                    ctrJumpInstructionError++;
                } else {
                    console.log(`New vulnerability type found in ${problem}`);
                }
            }

            ctrTotal += ctr;
            ctrArray.push(ctr);

            ctrTotalSeverityCritical += ctrCriticalPerFile;
            ctrTotalSeverityMajor += ctrMajorPerFile;
            ctrTotalSeverityMinor += ctrMinorPerFile;

            ctrArraySeverity.push([ctrCriticalPerFile, ctrMajorPerFile, ctrMinorPerFile]);

            ctrTotalTypeMemoryAccess += ctrMemoryAccessError;
            ctrTotalTypeJumpInstruction += ctrJumpInstructionError;

            ctrArrayTypeOfError.push([ctrMemoryAccessError, ctrJumpInstructionError]);
        }
    }

    ctrArray.push(ctrTotal);
    ctrArraySeverity.push([ctrTotalSeverityCritical, ctrTotalSeverityMajor, ctrTotalSeverityMinor]);
	ctrArrayTypeOfError.push([ctrTotalTypeMemoryAccess, ctrTotalTypeJumpInstruction]);
    ctrArrayTotal.push(ctrArray);
	ctrArrayTotal.push(ctrArraySeverity);
	ctrArrayTotal.push(ctrArrayTypeOfError);

    return ctrArrayTotal;
}

function higherLeakagePourcentage(issue, stamp) {
	/*
	 * Return true if the leakage score of the stamp is strictly smaller than
	 * the leakage score of the issue. Return false otherwise.
    */
	const leakageScoreStamp = LEAKAGE_SCORE_REGEX.exec(stamp.description)[1];
	const leakageScoreIssue = LEAKAGE_SCORE_REGEX.exec(issue.description)[1];
	if (parseFloat(leakageScoreStamp) >= parseFloat(leakageScoreIssue)) {
		return false ;
	}
	return true ;
}

function comparator(object) {
    /*
     * Return the leak with the highest severity leakage poucentage when there are many times the same leak
    */
    let onlyOneErrorAmongAllTheWorst = [];

	let sameErrorWithCritical = object.filter((issue) => issue.severity === "critical");

	const keysOfSameErrorWithCritical = Object.keys(sameErrorWithCritical);
    const sizeOfSameErrorWithCritical = Number(keysOfSameErrorWithCritical.slice(-1)[0]) + 1; // give the length of keysOfSameErrorWithCritical

	if (sizeOfSameErrorWithCritical === 1) {

        return sameErrorWithCritical;

	} else if (sizeOfSameErrorWithCritical > 1) {

		let stamp = sameErrorWithCritical[0];
		for (const issue of sameErrorWithCritical) {
			if (higherLeakagePourcentage(issue,stamp) === true){
				stamp = issue;
            }
		}
        onlyOneErrorAmongAllTheWorst.push(stamp);
		return onlyOneErrorAmongAllTheWorst;

	} else { // (sizeOfSameErrorWithCritical === 0)

		let sameErrorWithMajor = object.filter((issue) => issue.severity === 'major');

        const keysOfSameErrorWithMajor = Object.keys(sameErrorWithMajor);
        const sizeOfSameErrorWithMajor = Number(keysOfSameErrorWithMajor.slice(-1)[0]) + 1; // give the length of keysOfSameErrorWithMajor

		if (sizeOfSameErrorWithMajor === 1) {

			return sameErrorWithMajor;

		} else if (sizeOfSameErrorWithMajor > 1) {

            let stamp = sameErrorWithMajor[0];
            for (const issue of sameErrorWithMajor) {
                if (higherLeakagePourcentage(issue,stamp) === true){
                    stamp = issue;
                }
            }
            onlyOneErrorAmongAllTheWorst.push(stamp);
            return onlyOneErrorAmongAllTheWorst;

		} else { // (sizeOfSameErrorWithCritical === 0) && (sizeOfSameErrorWithMajor === 0)

			let sameErrorWithMinor = object.filter((issue) => issue.severity === 'minor');

			const keysOfSameErrorWithMinor = Object.keys(sameErrorWithMinor);
            const sizeOfSameErrorWithMinor = Number(keysOfSameErrorWithMinor.slice(-1)[0]) + 1; // give the length of keysOfSameErrorWithMajor

			if (sizeOfSameErrorWithMinor === 1) {

                return sameErrorWithMinor;

			} else if (sizeOfSameErrorWithMinor > 1) {

                let stamp = sameErrorWithMinor[0];
                for (const issue of sameErrorWithMinor) {
                    if (higherLeakagePourcentage(issue,stamp) === true){
                        stamp = issue;
                    }
                }
                onlyOneErrorAmongAllTheWorst.push(stamp);
                return onlyOneErrorAmongAllTheWorst;

			} else { // (sizeOfSameErrorWithCritical === 0) && (sizeOfSameErrorWithMajor === 0) && (sizeOfSameErrorWithMinor === 0) === problem
                console.log(`New severity found in ${object}`);
			}
		}
	}
}
