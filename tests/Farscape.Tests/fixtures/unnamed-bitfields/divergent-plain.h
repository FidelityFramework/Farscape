/* fx_div_pad as divergent-padded.h declares it, without the unnamed bit-field. */
struct fx_div_pad
{
  char a;
  char b;
};

void fx_div_plain(struct fx_div_pad *pad);
