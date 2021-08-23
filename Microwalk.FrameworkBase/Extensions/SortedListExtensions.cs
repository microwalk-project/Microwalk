using System.Collections.Generic;

namespace Microwalk.FrameworkBase.Extensions
{
    public static class SortedListExtensions
    {
        public static bool TryFindNearestLowerKey<TValue>(this SortedList<ulong, TValue> list, ulong search, out ulong nearestKey)
        {
            // Use binary search to find entry with key <= search
            var keys = list.Keys;
            int left = 0;
            int right = keys.Count - 1;
            int index;
            while(left <= right)
            {
                index = left + ((right - left) / 2);
                ulong key = keys[index];
                if(key == search)
                {
                    nearestKey = key;
                    return true;
                }
                if(key < search)
                    left = index + 1;
                else
                    right = index - 1;
            }
            
            // Search terminated, but key not found: Use the next smaller one -> one to the left         
            index = left - 1;
            if(index < 0)
            {
                nearestKey = 0;
                return false;
            }

            nearestKey = keys[index];
            return true;
        }
    }
}