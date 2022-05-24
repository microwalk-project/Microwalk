#!/bin/bash

set -e

thisDir=$(pwd)
repoRootDir=$(realpath $thisDir/..)
resultsDir=$thisDir/results

mkdir -p $resultsDir

for target in $(find . -name "target-*.c" -print)
do
  targetName=$(basename -- ${target%.*})
  
  echo "Running target ${targetName}..."
  
  export TESTCASE_DIRECTORY=$thisDir/testcases/$targetName
  export TARGET_NAME=$targetName
  
  mkdir -p $WORK_DIR/work/$targetName
  mkdir -p $WORK_DIR/persist/$targetName
  
  cd $MICROWALK_PATH
  dotnet Microwalk.dll $thisDir/config.yml
done
