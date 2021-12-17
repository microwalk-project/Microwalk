using ElfTools;
using ElfTools.Chunks;
using ElfTools.Enums;

// Load ELF file
if(args.Length < 2)
{
    Console.WriteLine("Please specify an input ELF file and an output MAP file.");
    return;
}

var elf = ElfReader.Load(args[0]);

// Open output file
using var outputWriter = new StreamWriter(File.Open(args[1], FileMode.Create, FileAccess.Write));

// Find base address
ulong baseAddress = elf.ProgramHeaderTable?.ProgramHeaders.First(ph => (ph.Flags & SegmentFlags.Executable) != 0).VirtualMemoryAddress ?? 0;

// Write object name
outputWriter.WriteLine(Path.GetFileNameWithoutExtension(args[0]));

// Find symbol table
var symtabSectionHeader = elf.SectionHeaderTable.SectionHeaders.FirstOrDefault(s => s.Type == SectionType.SymbolTable);
if(symtabSectionHeader == null)
{
    Console.WriteLine("Couldn't find SYMTAB section.");
    return;
}

var symtabSectionChunkIndex = elf.GetChunkAtFileOffset(symtabSectionHeader.FileOffset);
if(symtabSectionChunkIndex == null)
{
    Console.WriteLine("Couldn't find SYMTAB chunk.");
    return;
}

var symtabSectionChunk = (SymbolTableChunk)elf.Chunks[symtabSectionChunkIndex.Value.chunkIndex];

// Find corresponding string table
var strtabSectionHeader = elf.SectionHeaderTable.SectionHeaders[(int)symtabSectionHeader.Link];
if(strtabSectionHeader == null || strtabSectionHeader.Type != SectionType.StringTable)
{
    Console.WriteLine("Couldn't find STRTAB section.");
    return;
}

var strtabSectionChunkIndex = elf.GetChunkAtFileOffset(strtabSectionHeader.FileOffset);
if(strtabSectionChunkIndex == null)
{
    Console.WriteLine("Couldn't find STRTAB chunk.");
    return;
}

var strtabSectionChunk = (StringTableChunk)elf.Chunks[strtabSectionChunkIndex.Value.chunkIndex];

// Dump symbols
ulong lastRelativeAddress = unchecked((ulong)-1);
foreach(var symbol in symtabSectionChunk.Entries.OrderBy(s => s.Value))
{
    if(symbol.Value < baseAddress)
        continue;

    ulong relativeAddress = symbol.Value - baseAddress;
    string name = strtabSectionChunk.GetString(symbol.Name);

    if(relativeAddress == lastRelativeAddress)
        continue;

    if(string.IsNullOrWhiteSpace(name))
        continue;

    outputWriter.WriteLine($"{relativeAddress:x8} {name}");

    lastRelativeAddress = relativeAddress;
}