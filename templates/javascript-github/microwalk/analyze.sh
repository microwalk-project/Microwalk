#!/bin/bash

set -e

thisDir=$(pwd)
mainDir=$(realpath $thisDir/..)
repoRootDir=$mainDir
resultsDir=$thisDir/results

export JS_PATH_PREFIX=$repoRootDir

mkdir -p $resultsDir

reports=""

targets=$(find . -name "target-*.js" -print | grep -P '^(?!.*(jalangi)).*target-.*\.js' || true)
for target in $targets
do
  targetName=$(basename -- ${target%.*})
  
  echo "Running target ${targetName}..."
  
  export JS_TESTCASE_DIRECTORY=$thisDir/testcases/$targetName
  export JS_TRACE_DIRECTORY=$WORK_DIR/$targetName/work/traces
  export TARGET_NAME=$targetName
  
  mkdir -p $WORK_DIR/$targetName/work/traces
  mkdir -p $WORK_DIR/$targetName/persist
  
  cd $JALANGI2_PATH
  time node --max-old-space-size=16384 src/js/commands/jalangi.js --inlineIID --analysis src/js/sample_analyses/ChainedAnalyses.js --analysis src/js/runtime/SMemory.js --analysis $TRACE_TOOL $mainDir/index.js
  
  cd $MICROWALK_PATH
  time dotnet Microwalk.dll $thisDir/config-preprocess.yml
  time dotnet Microwalk.dll $thisDir/config-analyze.yml
  
  cd $CQR_GENERATOR_PATH
  dotnet CiReportGenerator.dll $WORK_DIR/$targetName/persist/results/call-stacks.json $targetName $resultsDir/report-$targetName.sarif sarif js-map $WORK_DIR/$targetName/work/maps
  
  cd $thisDir
  cp $WORK_DIR/$targetName/persist/results/call-stacks.txt $resultsDir/call-stacks-$targetName.txt
  
  reports="${reports} ${resultsDir}/report-${targetName}.sarif"
done

cat $reports | jq -s 'add' > $resultsDir/report.sarif