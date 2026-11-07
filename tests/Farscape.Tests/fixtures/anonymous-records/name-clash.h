/* The generated name of fx_clash's unnamed field record is fx_clash_inner, which a declared
   record already names. */
struct fx_clash_inner
{
  int z;
};

struct fx_clash
{
  struct
  {
    int a;
  } inner;
};

void fx_clash_read(struct fx_clash *clash, struct fx_clash_inner *inner);
