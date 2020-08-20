using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Microwalk.Utilities
{
    internal class MapFileCollection
    {
        /// <summary>
        /// Loaded MAP files.
        /// </summary>
        private readonly List<MapFile> _mapFiles = new List<MapFile>();

        /// <summary>
        /// Contains (image ID, map file) pairs.
        /// </summary>
        private readonly Dictionary<int, MapFile> _mapFileIdLookup = new Dictionary<int, MapFile>();

        /// <summary>
        /// Loads a MAP file and adds it to the internal list.
        /// </summary>
        /// <param name="mapFileName">Path to the MAP file.</param>
        /// <returns></returns>
        public async Task LoadMapFileAsync(string mapFileName)
        {
            await Logger.LogDebugAsync($"Reading MAP file \"{mapFileName}\"...");
            _mapFiles.Add(await MapFile.ReadFromFileAsync(mapFileName));
        }

        /// <summary>
        /// Formats the given address data.
        /// </summary>
        /// <param name="imageFile">Image file.</param>
        /// <param name="address">Image relative address.</param>
        /// <returns></returns>
        public string FormatAddress(TracePrefixFile.ImageFileInfo imageFile, uint address)
        {
            // Does a map file exist? Does it contain a suitable entry?
            var mapFile = ResolveMapFile(imageFile);
            var symbolData = mapFile?.GetSymbolDataByAddress(address);
            if(symbolData == null)
            {
                // Just format the image name and the image offset
                return $"{imageFile.Name}:{address:X}";
            }

            // Format image name, symbol name and symbol offset
            return $"{imageFile.Name}:{symbolData.Value.Name}+{(address - symbolData.Value.StartAddress):X}";
        }

        /// <summary>
        /// Returns the MAP file object matching the given image ID and name. Caches the resolved ID in the map file ID lookup.
        /// </summary>
        /// <param name="imageFile">Image data.</param>
        /// <returns></returns>
        private MapFile ResolveMapFile(TracePrefixFile.ImageFileInfo imageFile)
        {
            // Map file known?
            if(_mapFileIdLookup.TryGetValue(imageFile.Id, out var mapFile))
                return mapFile;

            // Find MAP file with matching image name
            mapFile = _mapFiles.FirstOrDefault(m => string.Compare(imageFile.Name, m.ImageName, true, CultureInfo.InvariantCulture) == 0);
            _mapFileIdLookup.Add(imageFile.Id, mapFile);
            return mapFile;
        }
    }
}
