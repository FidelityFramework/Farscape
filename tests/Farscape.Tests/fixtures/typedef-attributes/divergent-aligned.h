/* fx_div as divergent-plain.h declares it, but with a field typedef carrying aligned(8) (#175):
   clang lays it out at 16 bytes here and at 8 bytes there. */
typedef int fx_div_int __attribute__((aligned(8)));

struct fx_div
{
  char tag;
  fx_div_int value;
};

void fx_div_aligned(struct fx_div *div);
