using System;
using System.Collections;
using System.Collections.Generic;

namespace Microwalk.FrameworkBase.Utilities;

/// <summary>
/// Utility class for concatenating two enumerators.
/// </summary>
public class ConcatEnumerator<TValue> : IEnumerator<TValue>
{
    private TValue? _current;

    private readonly IEnumerator<TValue> _enumerator1;
    private readonly IEnumerator<TValue> _enumerator2;

    public ConcatEnumerator(IEnumerator<TValue> enumerator1, IEnumerator<TValue> enumerator2)
    {
        _enumerator1 = enumerator1;
        _enumerator2 = enumerator2;
    }

    public bool MoveNext()
    {
        if(_enumerator1.MoveNext())
        {
            _current = _enumerator1.Current;
            return true;
        }

        if(_enumerator2.MoveNext())
        {
            _current = _enumerator2.Current;
            return true;
        }

        _current = default;
        return false;
    }

    public void Reset()
    {
        _enumerator1.Reset();
        _enumerator2.Reset();
    }

    public TValue Current => _current ?? throw new InvalidOperationException("Current should not be used in this state");

    object IEnumerator.Current => _current ?? throw new InvalidOperationException("Current should not be used in this state");

    public void Dispose()
    {
        _enumerator1.Dispose();
        _enumerator2.Dispose();
    }
}