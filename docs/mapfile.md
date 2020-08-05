# MAP file format

Microwalk has built-in support for loading map files, which correspond to symbol tables which map image offsets to symbol names. As the map file format varies for each compiler, Microwalk defines a very simple own format which all other formats need to be converted to.

## Format

The first line consists of the image file name this MAP file belongs to.

The following lines are offset/symbol name pairs, separated by a single space.

Example:
```
mylibrary.dll
00000000 __ImageBase
00001010 Function1
00001ab0 Function2
```

## Tools

Existing tools for producing Microwalk MAP files:
- [IDA Python script](../Tools/IDA/CreateMapFile/create_map_file.py)