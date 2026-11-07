/* fx_div_pad as divergent-plain.h declares it, with an unnamed bit-field between its fields. */
struct fx_div_pad
{
  char a;
  int : 3;
  char b;
};

void fx_div_padded(struct fx_div_pad *pad);
