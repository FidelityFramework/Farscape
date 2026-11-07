/* One macro expansion declares two anonymous members of different layouts: clang spells both
   records at the expansion's location, so neither layout in its dump is attributable to one. */
#define FX_TWO_MEMBERS struct { char c; }; struct { long l; };

struct fx_mpair
{
  FX_TWO_MEMBERS
};

void fx_mpair_read(struct fx_mpair *pair);
