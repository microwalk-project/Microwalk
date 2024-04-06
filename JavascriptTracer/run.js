/**
 * Instruments the given file, if necessary, and then runs it.
 * 
 * @param {string} path File to instrument and run
 */

// Instrument if necessary
const { instrumentedName } = await import("./instrument-file.mjs");

// Run instrumented file
await import(instrumentedName);