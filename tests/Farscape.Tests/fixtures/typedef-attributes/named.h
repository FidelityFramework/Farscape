/* A record C names only by a typedef that carries aligned(16) (#175). Clang gives the type
   fx_named_wide alignment 16 and sizeof 8, while its record layout dump measures the untagged
   record at 8 bytes, alignment 4: the dump does not carry the typedef's attribute. */
typedef struct
{
  char tag;
  int value;
} fx_named_wide __attribute__((aligned(16)));

void fx_named_use(fx_named_wide *named);
