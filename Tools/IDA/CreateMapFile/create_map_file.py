# Exports a Microwalk-compatible MAP file.

# User input
outputFilePath = idc.AskFile(1, "*.map", "Save MAP file as")
addressOffset = int(idc.AskStr("180000000", "Enter constant offset"), 16)

# Write image name and symbol data
f = open(outputFilePath, "w")
f.write(idaapi.get_root_filename() + "\n")
for n in Names():
    
    # Try to demangle name
    # Remove ".cold" suffix, as IDA for some reason cannot demangle that
    name = n[1]
    if name.endswith(".cold"):
        name = name[:-5]
    nameDemangled = demangle_name(name, idc.GetLongPrm(idc.INF_SHORT_DN))
    
    # If demangling failed, use the raw name
    if nameDemangled == None:
        nameDemangled = n[1]
    
    f.write("{0:08x}".format(n[0] - addressOffset) + " " + nameDemangled + "\n")

f.close()