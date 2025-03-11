using System;
using System.Collections.Generic;
using System.Text;

namespace Microwalk.Analysis.Modules.CallTree;

public static class Utilities
{
    /// <summary>
    /// Formats a sequence of integers in compressed form.
    /// Example:
    ///     1 2 3 4 6 7 8 10
    ///   becomes
    ///     1-4 6-8 10
    /// </summary>
    /// <param name="sequence">Number sequence, in ascending order.</param>
    /// <returns>Compressed sequence of integers, formatted as string.</returns>
    public static string FormatIntegerSequence(IEnumerable<int> sequence)
    {
        StringBuilder result = new();

        // Number of consecutive integers to trigger a merge
        const int consecutiveThreshold = 2;

        bool first = true;
        int consecutiveStart = 0;
        int consecutiveCurrent = 0;
        foreach(var i in sequence)
        {
            if(first)
            {
                // Initialize first sequence
                consecutiveStart = i;
                consecutiveCurrent = i;

                first = false;
            }
            else if(i == consecutiveCurrent + 1)
            {
                // We are still in a sequence
                consecutiveCurrent = i;
            }
            else
            {
                // We left the previous sequence
                // Did it reach the threshold? -> write it in the appropriate format
                if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
                    result.Append($"{consecutiveStart}-{consecutiveCurrent} ");
                else
                {
                    // Threshold missed, just write the numbers
                    for(int j = consecutiveStart; j <= consecutiveCurrent; ++j)
                        result.Append($"{j} ");
                }

                // New sequence
                consecutiveStart = i;
                consecutiveCurrent = i;
            }
        }

        // Write remaining elements of last sequence
        if(consecutiveCurrent - consecutiveStart >= consecutiveThreshold)
            result.Append($"{consecutiveStart}-{consecutiveCurrent} ");
        else
        {
            for(int j = consecutiveStart; j <= consecutiveCurrent; ++j)
                result.Append($"{j} ");
        }

        // Remove trailing space
        if(result[^1] == ' ')
            result.Remove(result.Length - 1, 1);

        return result.ToString();
    }

    /// <summary>
    /// Computes the mean and standard deviation of the given list of values.
    /// </summary>
    /// <param name="values">Values.</param>
    /// <returns></returns>
    public static (double mean, double standardDeviation) ComputeMean(IEnumerable<double> values)
    {
        // Welford's method

        double mean = 0.0;
        double sum = 0.0;
        int i = 0;
        foreach(var v in values)
        {
            ++i;

            double delta = v - mean;

            mean += delta / i;
            sum += delta * (v - mean);
        }

        double standardDeviation = 0.0;
        if(i > 1)
            standardDeviation = Math.Sqrt(sum / i);

        return (mean, standardDeviation);
    }
}