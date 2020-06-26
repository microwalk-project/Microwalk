using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MapFileConverter
{
    class Program
    {
        static void Main(string[] args)
        {
            // Parameter check
            if(args.Length < 4)
            {
                Console.WriteLine("Command line syntax: <input format> <constant offset (hex)> <input file> <output file>");
                Console.WriteLine("Supported input formats: ida");
                Console.WriteLine("The constant offset usually is 1000");
                return;
            }
            string inputFormat = args[0];
            uint constantOffset = uint.Parse(args[1], NumberStyles.HexNumber);
            string inputFile = args[2];
            string outputFile = args[3];

            // Open input and output files
            using var inputStream = new StreamReader(File.OpenRead(inputFile));
            using var outputStream = new StreamWriter(File.OpenWrite(outputFile));

            // Convert
            switch(inputFormat)
            {
                case "ida":
                    ConvertIdaFile(inputStream, outputStream, constantOffset);
                    break;

                default:
                    Console.WriteLine("Unknown input format.");
                    break;
            }

            Console.WriteLine("Done.");
        }

        static void ConvertIdaFile(StreamReader input, StreamWriter output, uint constantOffset)
        {
            // Warning
            Console.WriteLine("Warning: As of version 7.0, IDA seems only to export broken segmentation.\n" +
                              "The segmentation table might need to be fixed manually beforehand.\n" +
                              "You might want to run the export Python script instead.");

            // Ask for name of image file
            Console.Write("Enter name of image file: ");
            output.WriteLine(Console.ReadLine());

            // Skip until segmentation table
            var segmentTableHeaderRegex = new Regex("^\\s*Start\\s+Length");
            string currentLine;
            while(true)
            {
                currentLine = input.ReadLine();
                if(string.IsNullOrWhiteSpace(currentLine))
                    continue;
                if(segmentTableHeaderRegex.IsMatch(currentLine))
                    break;
            }

            // Read segmentation table
            var segmentTableEntryRegex = new Regex("^\\s*([0-9a-fA-F]+):[0-9a-fA-F]+\\s+([0-9a-fA-F]+)H");
            Dictionary<int, uint> segmentBaseAddresses = new Dictionary<int, uint>();
            while(true)
            {
                currentLine = input.ReadLine();
                if(string.IsNullOrWhiteSpace(currentLine))
                    continue;
                var match = segmentTableEntryRegex.Match(currentLine);
                if(!match.Success)
                    break;

                if(!int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, null, out int segmentId)
                   || !uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, null, out uint segmentBaseAddress))
                {
                    Console.WriteLine($"Cannot parse line \"{currentLine}\"");
                    return;
                }
                segmentBaseAddresses.Add(segmentId, segmentBaseAddress);
            }

            // Skip until symbol table
            var symbolTableHeaderRegex = new Regex("^\\s*Address\\s+Publics");
            while(true)
            {
                if(string.IsNullOrWhiteSpace(currentLine))
                    continue;
                if(symbolTableHeaderRegex.IsMatch(currentLine))
                    break;

                currentLine = input.ReadLine();
            }

            // Read symbol table
            var symbolTableEntryRegex = new Regex("^\\s*([0-9a-fA-F]+):([0-9a-fA-F]+)\\s+(.+)$");
            while(true)
            {
                currentLine = input.ReadLine();
                if(string.IsNullOrWhiteSpace(currentLine))
                    continue;
                var match = symbolTableEntryRegex.Match(currentLine);
                if(!match.Success)
                    break;

                if(!int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, null, out int segmentId)
                   || !uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, null, out uint symbolAddress))
                {
                    Console.WriteLine($"Cannot parse line \"{currentLine}\"");
                    return;
                }

                uint address = segmentBaseAddresses[segmentId] + constantOffset + symbolAddress;
                string name = match.Groups[3].Value.Trim();
                output.WriteLine($"{address:X8} {name}");
            }
        }
    }
}
