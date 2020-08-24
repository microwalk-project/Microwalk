

/* INCLUDES */

#include "Utilities.h"
#include <algorithm>
#include <cctype>


/* FUNCTIONS */

void tolower(std::string& str)
{
    std::transform(str.begin(), str.end(), str.begin(), [](unsigned char c) -> unsigned char { return std::tolower(c); });
}

std::string trim(std::string s)
{
    // Remove left spaces
    s.erase(s.begin(), std::find_if(s.begin(), s.end(), [](int ch)
    {
        return !std::isspace(ch);
    }));

    // Remove right spaces
    s.erase(std::find_if(s.rbegin(), s.rend(), [](int ch)
    {
        return !std::isspace(ch);
    }).base(), s.end());

    return s;
}