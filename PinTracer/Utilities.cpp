

/* INCLUDES */

#include "Utilities.h"
#include <algorithm>
#include <cctype>


/* FUNCTIONS */

void tolower(std::string& str)
{
    std::transform(str.begin(), str.end(), str.begin(), [](unsigned char c) -> unsigned char { return std::tolower(c); });
}