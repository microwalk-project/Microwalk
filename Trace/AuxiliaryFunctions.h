#pragma once
/*
Contains helper functions.
*/

/* INCLUDES */
#include <string>
#include <algorithm>
#include <cctype>


/* FUNCTIONS */

// Converts the given string into its lower case representation.
void tolower(std::string &str)
{
	std::transform(str.begin(), str.end(), str.begin(), [](unsigned char c) -> unsigned char { return std::tolower(c); });
}