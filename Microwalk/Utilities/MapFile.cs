using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.XPath;

namespace Microwalk.Utilities
{
    /// <summary>
    /// Allows loading a MAP file and provides lookup functionality to query symbol names for given addresses.
    /// </summary>
    internal class MapFile
    {
        /// <summary>
        /// Sorted symbol addresses, used for finding the nearest match of a given address.
        /// </summary>
        private List<uint> _addresses;

        /// <summary>
        /// Maps symbol addresses to symbol names.
        /// </summary>
        private Dictionary<uint, string> _symbolNames;

        /// <summary>
        /// Returns the name of the associated image file.
        /// </summary>
        public string ImageName{ get; private set; }

        /// <summary>
        /// Empty constructor. Only allow instantiation from within this class.
        /// </summary>
        private MapFile()
        {
        }

        /// <summary>
        /// Parses the given MAP file. The file must have the following format:
        /// [image name]
        /// [hex start address 1] [symbol name 1]
        /// [hex start address 2] [symbol name 2]
        /// ...
        /// </summary>
        /// <param name="mapFileName">Path to the MAP file.</param>
        /// <returns></returns>
        public static async Task<MapFile> ReadFromFileAsync(string mapFileName)
        {
            // Initialize object
            var mapFile = new MapFile
            {
                _addresses = new List<uint>(),
                _symbolNames = new Dictionary<uint, string>()
            };

            // Read entire map file
            var mapFileLines = await File.ReadAllLinesAsync(mapFileName);

            // Read image name
            if(mapFileLines.Length < 1 || string.IsNullOrWhiteSpace(mapFileLines[0]))
            {
                await Logger.LogErrorAsync("Invalid MAP file. A MAP file has to contain the associated image name in the very first line.");
                throw new InvalidDataException("Invalid MAP file.");
            }
            mapFile.ImageName = mapFileLines[0];

            // Parse entries
            var entryRegex = new Regex("^(?:0x)?([0-9a-fA-F]+)\\s+([^\\s]+)\\s*$", RegexOptions.Compiled);
            foreach(var line in mapFileLines.Skip(1))
            {
                // Ignore entry lines
                if(string.IsNullOrWhiteSpace(line))
                    continue;

                // Parse entry
                var match = entryRegex.Match(line);
                if(!match.Success
                   || match.Groups.Count != 3
                   || !uint.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, null, out uint entryAddress))
                {
                    await Logger.LogWarningAsync($"Ignoring unrecognized line in MAP file: {line}\n");
                    continue;
                }
                string entrySymbolName = match.Groups[2].Value;

                // Store entry in lookup tables
                mapFile._addresses.Add(entryAddress);
                mapFile._symbolNames.Add(entryAddress, entrySymbolName);
            }

            // Sort address lookup, to allow binary search
            mapFile._addresses.Sort();

            return mapFile;
        }

        /// <summary>
        /// Finds the nearest smaller symbol to the given image address.
        /// </summary>
        /// <param name="address">Image relative address.</param>
        /// <returns>The symbol data corresponding to the given address, or null.</returns>
        public (uint StartAddress, string Name)? GetSymbolDataByAddress(uint address)
        {
            // Find address index in address array
            int index = _addresses.BinarySearch(address);
            if(index >= 0)
            {
                // Found, this is a symbol base address
                return (address, _symbolNames[address]);
            }

            // Not a base address, but BinarySearch yields the negated index of the next larger element
            index = ~index;
            if(index == 0)
            {
                // Not found
                return null;
            }

            // Get the index of the next smaller element
            --index;
            return (_addresses[index], _symbolNames[_addresses[index]]);
        }
    }
}
