/* Unnamed records nested two deep, as an array element, and cv-qualified. */
struct fx_outer
{
  char tag;
  union
  {
    int whole;
    struct
    {
      short lo;
      short hi;
    } parts;
  } slots[2];
  const struct
  {
    long q;
  } fixed;
};

void fx_outer_read(struct fx_outer *outer);
