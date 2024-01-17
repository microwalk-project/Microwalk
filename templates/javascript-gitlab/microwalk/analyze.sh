#!/bin/bash

set -e

thisDir=$(pwd)
mainDir=$(realpath $thisDir/..)
repoRootDir=$mainDir
resultsDir=$thisDir/results
resultsCoverageDir=$thisDir/coverage

export JS_PATH_PREFIX=$repoRootDir

mkdir -p $resultsDir
mkdir -p $resultsCoverageDir

reports1=""
reports2=""

targets=$(find . -name "target-*.js" -print | grep -P '^(?!.*(jalangi)).*target-.*\.js' | sort || true)

for target in $targets
do
  targetName=$(basename -- ${target%.*})
  
  echo "Running target ${targetName}..."
  
  export JS_TESTCASE_DIRECTORY=$thisDir/testcases/$targetName
  export JS_TRACE_DIRECTORY=$WORK_DIR/$targetName/work/traces
  export TARGET_NAME=$targetName
  
  mkdir -p $WORK_DIR/$targetName/work/traces
  mkdir -p $WORK_DIR/$targetName/persist

  mkdir -p $WORK_DIR/coverage/$targetName
  cd $WORK_DIR/coverage/$targetName

  TARGET_NAME=$targetName JS_TESTCASE_DIRECTORY=$thisDir/testcases/$targetName c8 node $mainDir/microwalk-index.js
  
  c8 report -r
  
  cd $WORK_DIR/coverage/$targetName/coverage/tmp/
  
  find . -name "*.json" | xargs cp -vt $resultsCoverageDir

  cd $resultsCoverageDir
  
  mv -v "coverage-"* "${targetName}.json"
  
  reports2="${reports2} ${resultsCoverageDir}/${targetName}.json" 
  
  cd $thisDir
  
  cd $JALANGI2_PATH
  /usr/bin/time --verbose node --max-old-space-size=16384 src/js/commands/jalangi.js --inlineIID --analysis src/js/sample_analyses/ChainedAnalyses.js --analysis src/js/runtime/SMemory.js --analysis $TRACE_TOOL $mainDir/microwalk-index.js
  
  cd $MICROWALK_PATH
  /usr/bin/time --verbose dotnet Microwalk.dll $thisDir/config-preprocess.yml
  /usr/bin/time --verbose dotnet Microwalk.dll $thisDir/config-analyze.yml
  
  cd $CQR_GENERATOR_PATH
  dotnet CiReportGenerator.dll $WORK_DIR/$targetName/persist/results/call-stacks.json $targetName $resultsDir/report-$targetName.json gitlab-code-quality js-map $WORK_DIR/$targetName/work/maps

  cd $thisDir
  cp $WORK_DIR/$targetName/persist/results/call-stacks.txt $resultsDir/call-stacks-$targetName.txt
  
  reports1="${reports1} ${resultsDir}/report-${targetName}.json"
done

cat $reports1 | jq -s 'add' > $resultsDir/report.json
