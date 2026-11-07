/* A value struct whose field type reaches an aligned(8) typedef (#175): its Clef record is laid
   out from the field's type `int`, which cannot carry the typedef's alignment. */
typedef int fx_wide_int __attribute__((aligned(8)));

struct fx_wide_value
{
  char tag;
  fx_wide_int value;
};

void fx_wide_value_use(struct fx_wide_value value);
