using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Microwalk.FrameworkBase.Utilities
{
    /// <summary>
    /// Manages a set of MAP file objects and provides utility methods for symbol name retrieval and formatting.
    /// </summary>
    public class MapFileCollection
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Loaded MAP files.
        /// </summary>
        private readonly List<MapFile> _mapFiles = new();

        /// <summary>
        /// Tracks image IDs for which no MAP file is available.
        /// We output a warning the first time a MAP file lookup fails.
        /// </summary>
        private static readonly ConcurrentDictionary<int, object> _imageIdsWithoutMapFile = new();

        /// <summary>
        /// Contains (image ID, map file) pairs.
        /// </summary>
        private readonly ConcurrentDictionary<int, MapFile> _mapFileIdLookup = new();

        /// <summary>
        /// Creates a new MAP file collection.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        public MapFileCollection(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Loads a MAP file and adds it to the internal list.
        /// </summary>
        /// <param name="mapFileName">Path to the MAP file.</param>
        /// <returns></returns>
        public async Task LoadMapFileAsync(string mapFileName)
        {
            await _logger.LogDebugAsync($"Reading MAP file \"{mapFileName}\"...");
            var mapFile = new MapFile(_logger);
            await mapFile.InitializeFromFileAsync(mapFileName);
            _mapFiles.Add(mapFile);
        }

        /// <summary>
        /// Formats the given address data.
        /// </summary>
        /// <param name="imageId">Image ID.</param>
        /// <param name="imageFileName">Image file name.</param>
        /// <param name="address">Image relative address.</param>
        /// <returns></returns>
        public string FormatAddress(int imageId, string imageFileName, uint address)
        {
            // Does a map file exist? Does it contain a suitable entry?
            var mapFile = ResolveMapFile(imageId, imageFileName);
            var symbolData = mapFile?.GetSymbolDataByAddress(address);
            if(symbolData == null)
            {
                // Just format the image name and the image offset
                return $"{imageFileName}:{address:x}";
            }

            // Format image name, symbol name and symbol offset
            return $"{imageFileName}:{symbolData.Value.Name}+{(address - symbolData.Value.StartAddress):x}";
        }

        /// <summary>
        /// Returns the MAP file object matching the given image ID and name. Caches the resolved ID in the map file ID lookup.
        /// </summary>
        /// <param name="imageId">Image ID.</param>
        /// <param name="imageFileName">Image file name.</param>
        /// <returns></returns>
        private MapFile? ResolveMapFile(int imageId, string imageFileName)
        {
            // Map file known?
            if(_mapFileIdLookup.TryGetValue(imageId, out var mapFile))
                return mapFile;

            // Find MAP file with matching image name
            mapFile = _mapFiles.FirstOrDefault(m => string.Compare(imageFileName, m.ImageName, true, CultureInfo.InvariantCulture) == 0);
            if(mapFile != null)
                _mapFileIdLookup[imageId] = mapFile;
            else if(_imageIdsWithoutMapFile.TryAdd(imageId, new object()))
            {
                _logger.LogWarningAsync($"No MAP file found for image #{imageId} \"{imageFileName}\"").Wait();
            }

            return mapFile;
        }
    }
}