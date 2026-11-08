/* A record whose only member is a zero-width unnamed bit-field (#183): clang measures it at 0
   bytes in C, a GNU empty record, which no descriptor represents. */
struct fx_only_zero
{
  int : 0;
};

void fx_only_zero_use(struct fx_only_zero *only);
