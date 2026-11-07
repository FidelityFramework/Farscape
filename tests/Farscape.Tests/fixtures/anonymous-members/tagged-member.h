/* A tagged record named without a declarator inside another. In C this declares nothing;
   under -fms-extensions clang makes it an anonymous member, the implicit unnamed field
   `struct fx_inner`, with no unnamed record to pair it with. */
struct fx_inner
{
  long x;
};

struct fx_tagged_outer
{
  struct fx_inner;
  int y;
};

int fx_tagged_read(struct fx_tagged_outer *outer);
