#pragma once

// Load diff library
#include "../dtl/dtl/dtl.hpp"

// Common C++ classes
#include <vector>
#include <string>
#include <sstream>

using namespace System;
using namespace System::Collections::Generic;

namespace Diff
{
    public ref class DiffItem
    {
    public:
        int LastIndexA;
        int LastIndexB;
        bool Equal;

        DiffItem(int lastIndexA, int lastIndexB, bool equal)
            : LastIndexA(lastIndexA), LastIndexB(lastIndexB), Equal(equal)
        {
        }
    };

    public ref class DiffTools
    {
    public:

        static List<DiffItem ^> ^ DiffIntSequences(array<long long> ^a, array<long long> ^b)
        {
            // Convert input to C++ vector
            std::vector<long long> aVec(a->Length);
            std::vector<long long> bVec(b->Length);
            System::Runtime::InteropServices::Marshal::Copy(a, 0, IntPtr(&aVec[0]), a->Length);
            System::Runtime::InteropServices::Marshal::Copy(b, 0, IntPtr(&bVec[0]), b->Length);

            // Run diff and retrieve edit script
            dtl::Diff<long long> diff(aVec, bVec);
            diff.compose();
            dtl::Ses<long long> edits = diff.getSes();

            // Build return list
            int iA = 0;
            int iB = 0;
            List<DiffItem ^> ^result = gcnew List<DiffItem ^>();
            bool inCommonBlock = false;
            for(auto &edit : edits.getSequence())
            {
                switch(edit.second.type)
                {
                    case dtl::SES_ADD: // Added from B
                        if(inCommonBlock)
                            result->Add(gcnew DiffItem(iA, iB, true));
                        inCommonBlock = false;

                        ++iB;

                        break;

                    case dtl::SES_DELETE: // Deleted in A
                        if(inCommonBlock)
                            result->Add(gcnew DiffItem(iA, iB, true));
                        inCommonBlock = false;

                        ++iA;

                        break;

                    case dtl::SES_COMMON: // Present in A and B
                        if(!inCommonBlock && (iA > 0 || iB > 0))
                            result->Add(gcnew DiffItem(iA, iB, false));
                        inCommonBlock = true;

                        ++iA;
                        ++iB;

                        break;
                }
            }
            result->Add(gcnew DiffItem(iA, iB, inCommonBlock));
            return result;
        }
    };
}
