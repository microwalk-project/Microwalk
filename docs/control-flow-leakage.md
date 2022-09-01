# Control Flow Leakage Analysis

The `control-flow-leakage` analysis module merges all traces into a single call tree. Points where the traces have differences lead to the tree _diverging_, i.e., it splits into multiple subtrees. This allows for a very accurate assessment of instructions leading to leakages and quantification of the leakage severity.

Contrary to its name, the analysis also produces detailed leakage analysis results for memory accesses. Its results are more accurate than those of the legacy `*-memory-access-trace-leakage` modules, at the cost of a slightly higher resource consumption.

For details about the reasoning and the implementation we refer to the accompanying [paper](https://arxiv.org/abs/2208.14942).

## Reports

The control flow leakage analysis report consists of a consolidated call tree, where each individual call stack holds leakage information about the instructions located in it.