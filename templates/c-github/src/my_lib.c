#include <stdint.h>
#include <stdlib.h>

#include "my_lib.h"


void init(void)
{
	// Empty
}

void some_function(uint8_t *array)
{
	// Some branch leakage
	for(int i = 0; i < array[0]; i++)
		array[1] += array[2];
}