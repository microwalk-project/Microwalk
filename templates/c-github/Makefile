# This is just an example make file for the dummy code in this template.
# Replace this by your own; however, please ensure that
#  1. you link with -shared, i.e., produce a dynamically linked library, and that
#  2. the code contains debugging symbols (-g), else the analysis won't be able to map leakages to code lines.

.PHONY : clean

CFLAGS=-O2 -fPIC -g
LDFLAGS=-shared

SOURCES=$(shell echo src/*.c)
OBJECTS=$(SOURCES:.c=.o)

TARGET=libexample.so

all: $(TARGET)

clean:
	rm -f $(OBJECTS) $(TARGET)

$(TARGET) : $(OBJECTS)
	$(CC) $(CFLAGS) $(OBJECTS) -o $@ $(LDFLAGS)