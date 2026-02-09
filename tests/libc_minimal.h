// Minimal libc header for testing Fidelity output mode
#include <stddef.h>

// I/O operations
typedef long ssize_t;

ssize_t write(int fd, const void *buf, size_t count);
ssize_t read(int fd, void *buf, size_t count);

// Process control
void _exit(int status);

// Memory operations
void *malloc(size_t size);
void free(void *ptr);
void *memcpy(void *dest, const void *src, size_t n);
void *memset(void *s, int c, size_t n);
