#!/bin/bash

targetDir=./testcases/target-RSASIGN
count=16
size=512

for (( n=0; n<$count; n++ )); do
  openssl genrsa -out ${targetDir}/t${n}.testcase.pem ${size}
  
  openssl rsa -outform der -in ${targetDir}/t${n}.testcase.pem -out ${targetDir}/t${n}.testcase
  rm ${targetDir}/t${n}.testcase.pem
done
