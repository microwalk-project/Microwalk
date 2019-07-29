using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LeakageDetector
{
    /// <summary>
    /// Allows to parse a linker MAP file.
    /// </summary>
    public class MapFile
    {
        /// <summary>
        /// Maps function start offsets to their names.
        /// </summary>
        private SortedList<ulong, string> _functionNames = new SortedList<ulong, string>();

        /// <summary>
        /// Loads the given MAP file.
        /// </summary>
        /// <param name="mapFile">The MAP file to be loaded.</param>
        public MapFile(string mapFileName)
        {
            // Read file completely
            string[] mapFile = File.ReadAllLines(mapFileName);

            // Throw away all lines until the interesting section is reached
            Regex functionTableHeaderRegex = new Regex("^\\s*Address\\s+Publics by Value", RegexOptions.Compiled);
            int i = 0;
            while(i < mapFile.Length && !functionTableHeaderRegex.IsMatch(mapFile[i]))
                ++i;
            ++i; // Skip header

            // Read entries
            Regex functionTableEntryRegex = new Regex("^\\s*([0-9]+):([0-9a-fA-F]+)\\s+([^\\s]+)", RegexOptions.Compiled);
            for(; i < mapFile.Length; ++i)
            {
                // Skip empty lines
                if(string.IsNullOrWhiteSpace(mapFile[i]))
                    continue;

                // Match entry
                Match functionTableEntryMatch = functionTableEntryRegex.Match(mapFile[i]);
                if(!functionTableEntryMatch.Success)
                    break;

                // TODO hardcoded
                if(!functionTableEntryMatch.Groups[1].Value.EndsWith("01"))
                    continue;

                // Save entry
                _functionNames[ulong.Parse(functionTableEntryMatch.Groups[2].Value, System.Globalization.NumberStyles.HexNumber)] = functionTableEntryMatch.Groups[3].Value;
            }
        }

        /// <summary>
        /// Returns the start address and name of the symbol nearest to the given address.
        /// </summary>
        /// <param name="address">The address to be found.</param>
        /// <returns></returns>
        public (ulong address, string name) GetSymbolNameByAddress(ulong address)
        {
            // Find entry
            int i = _functionNames.Count - 1;
            while(i >= 0 && _functionNames.Keys[i] > address)
                --i;
            if(i < 0)
                return (0, "Unknown");
            return (_functionNames.Keys[i], _functionNames.Values[i]);
        }
    }
}
