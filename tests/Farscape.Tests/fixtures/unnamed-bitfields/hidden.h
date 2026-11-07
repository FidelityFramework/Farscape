/* A record carrying a zero-width unnamed bit-field, whose tag a later object-like macro hides
   (#176): the parse's probe spells `struct fx_hidden_pad`, which the macro rewrites to an
   incomplete `struct fx_hidden_other`, so clang measures no layout of it. The record must be
   refused, never laid out from its named fields. */
struct fx_hidden_pad
{
  char a;
  int : 0;
  char b;
};

void fx_hidden_pad_use(struct fx_hidden_pad *pad);

#define fx_hidden_pad fx_hidden_other
