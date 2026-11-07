/* A value struct carrying an unnamed bit-field (#176): its Clef record holds only the named
   fields, so it cannot carry the padding clang places (`b` at 2, 3 bytes). */
struct fx_pad_value
{
  char a;
  int : 3;
  char b;
};

void fx_pad_value_use(struct fx_pad_value value);
