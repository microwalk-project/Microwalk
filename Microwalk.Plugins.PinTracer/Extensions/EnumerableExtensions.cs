using System.Collections.Generic;

namespace Microwalk.Plugins.PinTracer.Extensions
{
    internal static class EnumerableExtensions
    {
        /// <summary>
        /// Adds the given range of elements to the given collection.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="destination">The target collection.</param>
        /// <param name="source">The items to add.</param>
        public static void AddRange<T>(this ICollection<T> destination, IEnumerable<T> source)
        {
            foreach(T item in source)
                destination.Add(item);
        }
    }
}