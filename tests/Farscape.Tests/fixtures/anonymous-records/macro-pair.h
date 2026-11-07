/* One macro expansion declares two unnamed records of different layouts: clang spells both
   at the expansion's location, so neither layout in its dump is attributable to one field. */
#define FX_TWO_UNNAMED struct { char c; } narrow; struct { long l; } wide;

struct fx_pair
{
  FX_TWO_UNNAMED
};

void fx_pair_read(struct fx_pair *pair);
