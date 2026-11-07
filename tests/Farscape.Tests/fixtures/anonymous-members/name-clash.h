/* A record whose own member is named as Farscape would name its first anonymous member. */
struct fx_mclash
{
  int anonymous1;
  union { int a; float b; };
};

void fx_mclash_read(struct fx_mclash *clash);
