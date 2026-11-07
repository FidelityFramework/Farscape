/* A may_alias record carrying an unnamed bit-field (#176, #174): no copy of its named members
   reproduces the padding, so clang cannot confirm that may_alias moves no byte. */
struct __attribute__((__may_alias__)) fx_alias_pad
{
  char a;
  int : 3;
  char b;
};

void fx_alias_pad_use(struct fx_alias_pad *pad);
