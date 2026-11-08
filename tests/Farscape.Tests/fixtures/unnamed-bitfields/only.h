/* A record whose only member is an unnamed bit-field (#183): C names no field of it, but clang
   lays it out at 1 byte, alignment 1, and a record holding it by value places the next field
   after that byte. It is a defined record, laid out by clang's measurement, never a fieldless or
   opaque one; so is an unnamed record of only an unnamed bit-field that a field is typed with. */
struct fx_only
{
  int : 8;
};

struct fx_only_holder
{
  char a;
  struct fx_only pad;
  char b;
};

struct fx_only_named
{
  char a;
  struct
  {
    int : 8;
  } pad;
  char b;
};

void fx_only_use(struct fx_only *only, struct fx_only_holder *holder, struct fx_only_named *named);
