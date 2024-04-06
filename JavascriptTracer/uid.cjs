/**
 * Runtime utilities for UID generation and management.
 */

const constants = require("./constants.cjs");

const uidCtr =
{
    _ctr: 0,
    get ctr()
    {
        return this._ctr;
    },

    set ctr(x)
    {
        // can't be changed externally
    },
    
    next()
    {
        return ++this._ctr;
    },
};

const uidMap = new WeakMap();

function getUid(obj)
{
    // We can't get IDs of undefined objects
    if (!obj)
        return undefined;

    // Primitives don't have IDs
    if (typeof obj !== "object" && typeof obj !== "function")
        return constants.PRIMITIVE_INDICATOR;

    try
    {
        // Add uid if object doesn't already have one
        const uidSymbol = Symbol.for('uid');
        if (!Object.hasOwn(obj, uidSymbol)) {
            Object.defineProperty(obj, uidSymbol, {
                writable: false,
                value: uidCtr.next()
            });
        }
        return obj[uidSymbol];
    }
    catch
    {
        // If the object is not extensible, we can't add properties to it
        // In this rare case, we use map-based ID tracking
        if (!uidMap.has(obj)) {
            uidMap.set(obj, uidCtr.next());
        }
        return uidMap.get(obj);
    }
}

module.exports = {
    getUid
};