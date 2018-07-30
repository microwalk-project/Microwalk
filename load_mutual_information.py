filePath = idaapi.askstr(0, "mutual_information_instructions.txt", "MI file path: ")
f = open(filePath, 'r')
line = f.readline()
while line:
  line = line.strip()
  sect = line.split(' ')
  if float(sect[2]) > 0:
	target = idaapi.get_imagebase()+int(sect[1][:-1], 16)
	val = float(sect[2])
	print hex(target), val
	cmnt = "MutualInformation %s"%(val)
	MakeComm(target, cmnt)
	SetColor(target, CIC_ITEM, 0xFFFF00);
  line = f.readline()

  