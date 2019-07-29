using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace WpfPlus.MvvmHelpers
{
    public interface IItemViewModel<TModel>
    {
        TModel BaseModel { get; }
    }
}
