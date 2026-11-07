/* A may_alias record whose field type is a typedef carrying aligned(8), and the same record
   without the attribute. Clang lays both out at 16 bytes, alignment 8, with `value` at 8:
   the typedef moves the field, may_alias moves no byte. Natural alignment of the declared
   field types would place `value` at 4, so only clang's measurement of the copy confirms it. */
typedef int fx_wide_int __attribute__((aligned(8)));

struct __attribute__((__may_alias__)) fx_skewed
{
  char tag;
  fx_wide_int value;
};

struct fx_skewed_plain
{
  char tag;
  fx_wide_int value;
};

void fx_skewed_use(struct fx_skewed *skewed);
