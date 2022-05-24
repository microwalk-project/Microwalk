#include <stdint.h>
#include <stdlib.h>

#include "my_lib.h"

uint8_t lookup[256];

void init(void)
{
	srand(0);
	
	for(int i = 0; i < sizeof(lookup) / sizeof(lookup[0]); ++i)
		lookup[i] = (uint8_t)rand();
}

int some_function(uint8_t *array)
{
	for(int i = 0; i < array[0]; ++i)
	{
		if(lookup[i] < 5)
			return 1;
	}
	
	return 2;
}