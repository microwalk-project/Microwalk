#!/bin/bash

set -e

thisDir=$(pwd)
repoRootDir=$(realpath $thisDir/..)
resultsDir=$thisDir/results

mkdir -p $resultsDir

reports=""

for target in $(find . -name "target-*.c" -print)
do
  targetName=$(basename -- ${target%.*})
  
  echo "Running target ${targetName}..."
  
  export TESTCASE_DIRECTORY=$thisDir/testcases/$targetName
  export TARGET_NAME=$targetName
  
  mkdir -p $WORK_DIR/$targetName/work
  mkdir -p $WORK_DIR/$targetName/persist
  
  cd $MICROWALK_PATH
  dotnet Microwalk.dll $thisDir/config.yml
  
  cd $CQR_GENERATOR_PATH
  reportFile=$resultsDir/report-$targetName.sarif
  dotnet CiReportGenerator.dll $WORK_DIR/$targetName/persist/results/call-stacks.json $targetName $reportFile sarif dwarf $thisDir $repoRootDir
  
  cd $thisDir
  cp $WORK_DIR/$targetName/persist/results/call-stacks.txt $resultsDir/call-stacks-$targetName.txt
  
  reports="${reports} ${reportFile}"
  
  echo "Running target ${targetName} successful, generated report ${reportFile}"
done

echo "Merging report files..."
cat $reports | jq -s '.[0].runs[0].results=([.[].runs[0].results]|flatten)|.[0]' > $resultsDir/report.sarif