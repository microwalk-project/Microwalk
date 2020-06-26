# Exports a Microwalk-compatible MAP file.

# User input
outputFilePath = idc.AskFile(1, "*.map", "Save MAP file as")
addressOffset = int(idc.AskStr("180000000", "Enter constant offset"), 16)

# Write image name and symbol data
f = open(outputFilePath, "w")
f.write(idaapi.get_root_filename() + "\n")
for n in Names():
	f.write("{0:08x}".format(n[0] - addressOffset) + " " + n[1] + "\n")
f.close()
