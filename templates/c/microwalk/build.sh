#!/bin/bash

thisDir=$(pwd)
mainDir=$(realpath $thisDir/..)

# Build library
# TODO You may either directly use the CI build of your library, or replace the `make -j all` by the command needed
# to compile you library.
pushd $mainDir
make -j all
popd

# Generate MAP file for library
pushd $MAP_GENERATOR_PATH
dotnet MapFileGenerator.dll $mainDir/libexample.so $thisDir/libexample.map
popd

# Build targets
for target in $(find . -name "target-*.c" -print)
do
  targetName=$(basename -- ${target%.*})
  
  # TODO Adjust command line to link against your library
  gcc main.c $targetName.c -g -fno-inline -fno-split-stack -L "$mainDir" -lexample -I "$mainDir/src" -o $targetName
  
  pushd $MAP_GENERATOR_PATH
  dotnet MapFileGenerator.dll $thisDir/$targetName $thisDir/$targetName.map
  popd
done
