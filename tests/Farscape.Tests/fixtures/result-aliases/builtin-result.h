/* strlen is a library function clang knows: clang spells its result `__size_t`, its
   predefined alias for size_t, whatever this declaration writes. */
unsigned long strlen(const char *s);
unsigned long fx_len(const char *s);
