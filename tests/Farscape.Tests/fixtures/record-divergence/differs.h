/* fx_rec with a narrower second field than first.h's. */
struct fx_rec
{
  int a;
  short b;
};

void fx_differs(struct fx_rec *rec);
