#!/bin/bash

targetDir=./testcases/target-ED25519
count=16

for (( n=0; n<$count; n++ )); do
  openssl genpkey -out ${targetDir}/t${n}.testcase -outform DER -algorithm ED25519
done
