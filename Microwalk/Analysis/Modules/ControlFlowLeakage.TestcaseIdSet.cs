using System;
using System.Collections.Generic;
using System.Numerics;
using Standart.Hash.xxHash;

namespace Microwalk.Analysis.Modules;

public partial class ControlFlowLeakage
{
    /// <summary>
    /// Utility class for efficient storage of a testcase ID set.
    /// Assumes that testcase IDs are small and don't have large gaps in between.
    /// </summary>
    /// <remarks>
    /// This class is not thread-safe.
    /// </remarks>
    private class TestcaseIdSet
    {
        private ulong[] _testcaseIdBitField = new ulong[1];

        private void EnsureArraySize(int id)
        {
            if(id / 64 < _testcaseIdBitField.Length)
                return;

            int newSize = 2 * _testcaseIdBitField.Length;
            while(newSize <= id / 64)
                newSize *= 2;

            // Resize
            ulong[] newBitField = new ulong[newSize];
            _testcaseIdBitField.CopyTo(newBitField, 0);

            _testcaseIdBitField = newBitField;
        }

        /// <summary>
        /// Adds the given testcase ID to this set, if it is not yet included.
        /// </summary>
        /// <param name="id">Testcase ID.</param>
        public void Add(int id)
        {
            EnsureArraySize(id);

            _testcaseIdBitField[id / 64] |= (1ul << (id % 64));
        }

        /// <summary>
        /// Removes the given testcase ID from this set, if it is included.
        /// </summary>
        /// <param name="id">Testcase ID.</param>
        public void Remove(int id)
        {
            EnsureArraySize(id);

            _testcaseIdBitField[id / 64] &= ~(1ul << (id % 64));
        }

        /// <summary>
        /// Creates a new empty testcase ID set.
        /// </summary>
        public TestcaseIdSet()
        {
        }

        /// <summary>
        /// Creates a new testcase ID set with the given values.
        /// </summary>
        /// <param name="testcaseIds">Testcase IDs to add.</param>
        public TestcaseIdSet(params int[] testcaseIds)
        {
            foreach(var t in testcaseIds)
                Add(t);
        }

        /// <summary>
        /// Returns a deep copy of this set.
        /// </summary>
        public TestcaseIdSet Copy()
        {
            TestcaseIdSet s = new TestcaseIdSet
            {
                _testcaseIdBitField = new ulong[_testcaseIdBitField.Length]
            };
            _testcaseIdBitField.CopyTo(s._testcaseIdBitField, 0);

            return s;
        }

        /// <summary>
        /// Returns a deep copy of this set, with the given ID removed.
        /// </summary>
        /// <param name="id">ID to exclude from the copied set.</param>
        /// <returns></returns>
        public TestcaseIdSet Without(int id)
        {
            var newSet = Copy();
            newSet.Remove(id);

            return newSet;
        }

        /// <summary>
        /// Returns the included testcase ID in ascending order.
        /// </summary>
        public IEnumerable<int> AsEnumerable()
        {
            for(int i = 0; i < _testcaseIdBitField.Length; ++i)
            {
                ulong b = _testcaseIdBitField[i];
                for(int j = 0; j < 64; ++j)
                {
                    if((b & 1) != 0)
                        yield return 64 * i + j;

                    b >>= 1;
                }
            }
        }

        public override string ToString()
        {
            return FormatIntegerSequence(AsEnumerable());
        }

        /// <summary>
        /// Returns the number of IDs contained in this object.
        /// </summary>
        public int Count
        {
            get
            {
                // This is much faster than _testcaseIdBitField.Sum(BitOperations.PopCount), probably due to better inlining
                int count = 0;
                foreach(var b in _testcaseIdBitField)
                    count += BitOperations.PopCount(b);
                return count;
            }
        }
    }
}