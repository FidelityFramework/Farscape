/* clang spells strlen's result `__size_t`, but after this header `strlen` names a constant,
   so no file-scope probe can call the function to read its canonical result type. */
unsigned long strlen(const char *s);
#define strlen 0
