#!/bin/bash

# TODO Add linker dependencies and other compile flags

g++ PinTracerWrapper.cpp -o wrapper -fno-split-stack -lcrypto
