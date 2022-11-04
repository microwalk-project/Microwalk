# Introduction

## What is a side-channel attack?
A side-channel attacker does not try to _directly_ extract information (e.g., through a buffer overflow), but only "monitors" the execution of a piece of code. For example, the attacker may measure variations in the execution time to find out how many expensive operations were executed (maybe giving an indication about the structure of a secret key), or employ a so-called _cache attack_, where they observe which data and code the victim process accesses, which in turn allows them to reconstruct the control flow and memory access pattern.

Most side-channel attacks are restricted to a single system, so the attacker needs to be on the same machine as the vulnerable code. However, this is a common scenario in practice, as code may run in a cloud environment (where multiple customers share the same machine) or in browsers (which may execute scripts served by the attacker).

## How does a side-channel leakage look in the code?
A side-channel leakage is present whenever there is _secret-dependent_ control flow or a memory access to a _secret-dependent_ location.

**Leaking control flow:**
```c
for(int i = 0; i < key_length; ++i) {
    if(key[i] == 0) {
        foo();    // Only executed when key[i] == 0
    } else {
        bar();    // Only executed when key[i] == 1
    }
}
```
If an attacker is able to distinguish which line of code is executed, they immediately learn a key bit.

**Leaking memory access:**
```c
char lookup_table[256] = { ... };
...
for(int i = 0; i < key_length; ++i) {
    int x = lookup_table[key[i]];    // Accesses address lookup_table+key[i]
    ...
}
```
If an attacker is able to tell which address was accessed, they learn the value of `key[i]`.


## How does Microwalk find side-channel leakages?
If we run the above examples with two different `key` values `key1[4] = { 1, 0, 1, 0 }` and `key2[4] = { 1, 0, 0, 1 }`, and log the executed code lines and memory accesses, we get the following execution traces:
```
Control flow:

 key1 | key2
 -----|-----
 bar  | bar
 foo  | foo
 bar  | foo
 foo  | bar
```
```
Memory accesses:

 key1            | key2
 ----------------|-----------------
 lookup_table[1] | lookup_table[1]
 lookup_table[0] | lookup_table[0]
 lookup_table[1] | lookup_table[0]
 lookup_table[0] | lookup_table[1]
```

If we now assume that the attacker gets full access to those execution traces (which is realistic with a side-channel attack), they can easily read the traces to infer the values of `key1` and `key2`!

So we somehow have to ensure that _every secret input_ leads to _the same execution trace_. In this case, the attacker would learn nothing by looking at a trace.

This is how Microwalk works: The framework generates execution traces for a variety of secret inputs, and checks whether those traces are identical. If Microwalk finds differences, those are reported as leakage.